using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Threading;
using RabTrans.Core.Hotkey;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RabTrans;
using RabTrans.Core.OCR;
using RabTrans.Core.Settings;
using RabTrans.Core.Storage;
using RabTrans.Core.Sync;
using RabTrans.Core.Translation;

internal static class Program
{
    private static int _checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "RabTrans-settings-tests-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        var exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Root);
                await TestSession();
                await TestBackup();
                await TestWindow(args.FirstOrDefault() ?? Path.Combine(Root, "screenshots"));
                Console.WriteLine($"PASS: {_checks} settings checks");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        try { Directory.Delete(Root, true); } catch { }
        return exitCode;
    }
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, JsonElement> Values(params (string Key, object Value)[] values) => values.ToDictionary(p => p.Key, p => J(p.Value));
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        _checks++; Console.WriteLine("PASS: " + label);
    }
    private static async Task TestSession()
    {
        using var storage = new StorageService(Path.Combine(Root, "session"));
        var initial = Values(("flag", false), ("text", "old"), ("minimize_to_tray", false), ("close_to_tray", false));
        await storage.UpdateAsync(initial);
        var disk = File.ReadAllBytes(storage.SettingsPath);
        var runtime = initial.ToDictionary();
        var calls = 0;
        var block = false;
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var failApply = false;
        var failRestore = false;
        var session = new SettingsAutoSaveSession(storage, initial, async changes =>
        {
            calls++;
            foreach (var pair in changes) runtime[pair.Key] = pair.Value;
            if (failApply && changes.TryGetValue("flag", out var f) && f.GetBoolean()) throw new InvalidOperationException("Fixture: rejected runtime change");
            if (failRestore && changes.TryGetValue("flag", out f) && !f.GetBoolean()) throw new InvalidOperationException("Fixture: rollback blocked");
            if (block) { block = false; entered.SetResult(); await release.Task; }
        });
        Check(calls == 0 && File.ReadAllBytes(storage.SettingsPath).SequenceEqual(disk), "initialization does not write or apply runtime settings");
        session.Queue(Values(("flag", true)));
        await session.FlushAsync();
        Check(runtime["flag"].GetBoolean() && await storage.GetAsync<bool>("flag"), "successful edit applies and persists");
        Check(!(await storage.SnapshotAsync()).ContainsKey("minimize_to_tray") && !await storage.GetAsync<bool>("close_to_tray"), "first successful save migrates legacy tray semantics");
        Check(runtime["text"].GetString() == "old" && await storage.GetAsync<string>("text") == "old", "unrelated values stay unchanged");
        block = true;
        session.Queue(Values(("flag", false)));
        var flushing = session.FlushAsync();
        await entered.Task;
        session.Queue(Values(("flag", true), ("text", "latest")));
        release.SetResult();
        Check(await flushing && await storage.GetAsync<bool>("flag") && await storage.GetAsync<string>("text") == "latest", "in-flight reversal and later edits are retained");
        session.Queue(Values(("flag", false)));
        await session.FlushAsync();
        failApply = true;
        session.Queue(Values(("flag", true)));
        Check(!await session.FlushAsync() && !runtime["flag"].GetBoolean() && !await storage.GetAsync<bool>("flag"), "runtime failure restores last effective state");
        Check(session.HasPending && session.Error != null, "failed edit remains available to retry");
        failApply = false;
        Check(await session.FlushAsync() && !session.HasPending && await storage.GetAsync<bool>("flag"), "retry commits the retained edit");
        disk = File.ReadAllBytes(storage.SettingsPath);
        using (var locked = new FileStream(storage.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Queue(Values(("flag", false), ("text", "failed disk write")));
            Check(!await session.FlushAsync(), "locked settings file reports write failure");
            Check(runtime["flag"].GetBoolean() && runtime["text"].GetString() == "latest", "disk failure rolls runtime back");
            Check(await storage.GetAsync<bool>("flag") && await storage.GetAsync<string>("text") == "latest", "failed atomic batch keeps storage cache unchanged");
            Check(File.ReadAllBytes(storage.SettingsPath).SequenceEqual(disk), "failed atomic batch keeps file unchanged");
        }
        await session.DiscardPendingAsync();
        Check(!session.HasPending && session.Error == null, "discard preserves saved settings and clears failed edits");
        session.Queue(Values(("flag", false))); await session.FlushAsync();
        failApply = failRestore = true;
        session.Queue(Values(("flag", true)));
        Check(!await session.FlushAsync() && !await session.DiscardPendingAsync(), "failed runtime restoration prevents unsafe discard");
        failApply = failRestore = false;
        Check(await session.DiscardPendingAsync() && !runtime["flag"].GetBoolean(), "discard retries restoration before clearing edits");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => storage.SetAsync("concurrent_" + i, i)));
        var snapshot = await storage.SnapshotAsync();
        Check(Enumerable.Range(0, 20).All(i => snapshot["concurrent_" + i].GetInt32() == i), "concurrent storage writes preserve every unrelated key");
        Check(Directory.GetFiles(storage.AppDataPath, "*.tmp").Length == 0, "atomic writes clean temporary files");
    }
    private static async Task TestBackup()
    {
        using var storage = new StorageService(Path.Combine(Root, "backup"));
        var sync = new ConfigSyncService(storage);
        var zip = Path.Combine(Root, "fresh.zip");
        sync.CreateConfigPackage(zip, false);
        await storage.SetAsync("after_export", true);
        var plugin = Path.Combine(storage.AppDataPath, "plugins", "new", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!); File.WriteAllText(plugin, "{}");
        sync.ImportConfigPackage(zip);
        Check((await storage.SnapshotAsync()).Count == 0 && !File.Exists(plugin), "empty backup restores empty settings and plugin directory");
        await storage.SetAsync("keep", 42);
        var invalid = Path.Combine(Root, "invalid.zip");
        using (var archive = ZipFile.Open(invalid, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("settings.json").Open())) writer.Write("[]");
        try { sync.ImportConfigPackage(invalid); throw new Exception("invalid backup accepted"); }
        catch (InvalidDataException) { Check(await storage.GetAsync<int>("keep") == 42, "invalid backup is rejected before changing local settings"); }
    }
    private static async Task TestWindow(string screenshots)
    {
        Directory.CreateDirectory(screenshots);
        using var storage = new StorageService(Path.Combine(Root, "ui"));
        var translations = Path.Combine(storage.AppDataPath, "plugins", "translation");
        var ocrs = Path.Combine(storage.AppDataPath, "plugins", "ocr");
        foreach (var (id, name) in new[] { ("alpha", "Alpha Translate"), ("beta", "Beta Translate"), ("gamma", "A very long custom translation service name that must remain contained") })
            MakePlugin(translations, id, name, "translation");
        foreach (var (id, name) in new[] { ("ocr_one", "First OCR"), ("ocr_two", "Second OCR") }) MakePlugin(ocrs, id, name, "ocr");
        using var translation = new TranslationService(Path.Combine(Root, "empty-t"), translations);
        using var ocr = new OcrService(Path.Combine(Root, "empty-o"), ocrs);
        await storage.UpdateAsync(Values(("enabled_translation_providers", new[] { "alpha" }), ("translation_provider_order", new[] { "gamma", "alpha", "beta" }), ("enabled_ocr_providers", new[] { "ocr_one" })));
        await storage.UpdateAsync(Values(("ocr_hotkey", "Ctrl+Alt+Shift+F9"), ("translate_hotkey", "Ctrl+Alt+Shift+F10")));
        var main = new MainWindow(); main.Show();
        await main.ApplyPreferencesAsync(Values(("ocr_hotkey", "Ctrl+Alt+Shift+F9"), ("translate_hotkey", "Ctrl+Alt+Shift+F10")));
        using var probeWindow = new HwndSource(new HwndSourceParameters("RabTrans test hotkeys") { Width = 1, Height = 1 });
        using var probe = new HotkeyService(probeWindow.Handle);
        const uint modifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_ALT | HotkeyService.MOD_SHIFT;
        Check(probe.RegisterHotkey(modifiers, 0x78, () => { }) == -1, "configured shortcut is actually registered with Windows");
        try { await main.ApplyPreferencesAsync(Values(("translate_hotkey", "Ctrl+Alt+Shift+F9"))); throw new Exception("duplicate accepted"); }
        catch (InvalidOperationException) { Check(probe.RegisterHotkey(modifiers, 0x79, () => { }) == -1, "duplicate shortcut rejection keeps previous global shortcuts"); }
        Check(probe.RegisterHotkey(modifiers, 0x7A, () => { }) >= 0, "reserve an occupied shortcut for conflict verification");
        try { await main.ApplyPreferencesAsync(Values(("ocr_hotkey", "Ctrl+Alt+Shift+F11"))); throw new Exception("occupied accepted"); }
        catch (InvalidOperationException) { Check(probe.RegisterHotkey(modifiers, 0x78, () => { }) == -1, "occupied shortcut rejection restores the previous global registration"); }
        probe.UnregisterAll();
        var before = File.ReadAllBytes(storage.SettingsPath);
        var applyCalls = new List<string[]>();
        var window = new SettingsWindow(storage, translation, ocr, async changes => { applyCalls.Add(changes.Keys.ToArray()); await main.ApplyPreferencesAsync(changes); }) { Owner = main };
        window.Show();
        await Task.Delay(150);
        Check(File.ReadAllBytes(storage.SettingsPath).SequenceEqual(before) && applyCalls.Count == 0, "opening settings does not save initialized controls");
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var nav = Find<ListBox>("NavigationList");
        var providers = Find<ListBox>("ProviderListBox");
        var items = (ObservableCollection<ProviderSelectionItem>)providers.ItemsSource;
        Check(items.Select(p => p.Id).SequenceEqual(new[] { "gamma", "alpha", "beta" }), "disabled services retain stored positions on load");
        items.Single(p => p.Id == "alpha").IsSelected = false;
        Check(items.Single(p => p.Id == "alpha").IsSelected && Find<TextBlock>("TranslationServiceError").Visibility == Visibility.Visible, "last enabled service cannot be disabled and shows explanation");
        items.Single(p => p.Id == "beta").IsSelected = true;
        await Drain(window);
        Check((await storage.GetAsync<string[]>("enabled_translation_providers"))!.SequenceEqual(new[] { "alpha", "beta" }), "service selection saves immediately in display order (" + string.Join(",", await storage.GetAsync<string[]>("enabled_translation_providers") ?? Array.Empty<string>()) + "; " + Find<TextBlock>("SaveStatus").Text + ")");
        items.Move(0, 2);
        await (Task)Invoke(window, "SaveProviderOrderAsync", false)!;
        Check((await storage.GetAsync<string[]>("translation_provider_order"))!.SequenceEqual(new[] { "alpha", "beta", "gamma" }), "reordering persists all services including disabled ones");
        MakePlugin(translations, "new_service", "New service", "translation");
        Invoke(window, "RefreshServices_Click", new Button { Tag = "translation" }, new RoutedEventArgs());
        await Drain(window);
        Check(items.Select(p => p.Id).SequenceEqual(new[] { "alpha", "beta", "gamma", "new_service" }) && !items.Last().IsSelected, "refresh preserves order and enablement while appending new services");
        nav.SelectedIndex = 3;
        var path = Find<TextBox>("NodePathBox");
        path.Text = Path.Combine(Root, "missing.exe");
        await Task.Delay(850);
        Check(await storage.GetAsync<string>("plugin_node_path") == null && Find<TextBlock>("NodePathHint").Text.Contains("existing executable"), "invalid runtime path stays unsaved with an inline error");
        var executable = Path.Combine(Root, "fixture.exe"); File.WriteAllText(executable, "fixture");
        path.Text = executable;
        await Task.Delay(150);
        Check(await storage.GetAsync<string>("plugin_node_path") == null, "typing waits for the debounce interval");
        await Task.Delay(650);
        Check(await storage.GetAsync<string>("plugin_node_path") == executable, "valid path automatically saves after debounce");
        var proxy = Find<ComboBox>("ProxyModeCombo");
        proxy.SelectedIndex = 2;
        await Drain(window);
        Check(Find<StackPanel>("ProxyAddressPanel").Visibility == Visibility.Visible && await storage.GetAsync<string>("proxy_mode") == null, "HTTP mode remains pending until its address is valid");
        Find<TextBox>("HttpProxyBox").Text = "127.0.0.1:7890";
        await Task.Delay(850);
        Check(await storage.GetAsync<string>("proxy_mode") == "Http" && await storage.GetAsync<string>("http_proxy") == "http://127.0.0.1:7890", "proxy mode and normalized address commit together");
        proxy.SelectedIndex = 1; await Drain(window);
        Check(Find<StackPanel>("ProxyAddressPanel").Visibility == Visibility.Collapsed && await storage.GetAsync<string>("proxy_mode") == "None", "non-HTTP modes hide the proxy field and save immediately");
        nav.SelectedIndex = 1;
        var hotkey = Find<TextBox>("TranslateHotkeyBox");
        var old = hotkey.Text;
        Invoke(window, "RecordHotkey_Click", new Button { Tag = "translation" }, new RoutedEventArgs());
        Check(hotkey.Text.Contains("Press"), "shortcut recording enters capture mode");
        Check(probe.RegisterHotkey(modifiers, 0x78, () => { }) >= 0, "recording suspends global shortcuts");
        probe.UnregisterAll();
        nav.SelectedIndex = 2;
        Check(probe.RegisterHotkey(modifiers, 0x78, () => { }) == -1, "page cancellation immediately restores global shortcuts");
        Check(hotkey.Text == old, "changing page cancels recording and restores text");
        nav.SelectedIndex = 1;
        Invoke(window, "RecordHotkey_Click", new Button { Tag = "translation" }, new RoutedEventArgs());
        Invoke(window, "SettingsWindow_PreviewKeyDown", window, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(hotkey.Text == old && window.IsVisible && probe.RegisterHotkey(modifiers, 0x78, () => { }) == -1, "Esc cancels recording without closing settings and restores shortcuts");
        Invoke(window, "RecordHotkey_Click", new Button { Tag = "translation" }, new RoutedEventArgs());
        Find<ComboBox>("DefaultSourceLangCombo").Focus();
        Check(hotkey.Text == old && probe.RegisterHotkey(modifiers, 0x78, () => { }) == -1, "focus loss cancels recording and restores shortcuts");
        for (var page = 0; page < 5; page++)
        {
            nav.SelectedIndex = page; window.UpdateLayout();
            foreach (var scale in new[] { 1d, 1.5, 2d }) Render(window, Path.Combine(screenshots, $"page-{page}-{scale * 100:0}.png"), scale);
        }
        window.Width = 680; window.Height = 480;
        for (var page = 0; page < 5; page++)
        {
            nav.SelectedIndex = page; window.UpdateLayout();
            Render(window, Path.Combine(screenshots, $"small-{page}.png"), 1);
            var scroll = Find<ScrollViewer>("PageScroll");
            Check(scroll.ViewportWidth > 350 && Find<TextBlock>("SaveStatus").ActualHeight > 0, $"page {page} keeps scroll area and footer accessible at minimum size");
        }
        nav.SelectedIndex = 1;
        Find<ScrollViewer>("PageScroll").ScrollToBottom(); window.UpdateLayout();
        Render(window, Path.Combine(screenshots, "small-translation-bottom.png"), 1);
        window.Close(); await Task.Delay(100);
        Check(!window.IsVisible, "close waits for valid pending saves then closes");
        var reopened = new SettingsWindow(storage, translation, ocr, _ => Task.CompletedTask);
        reopened.Show(); await Task.Delay(100);
        Check(((ListBox)reopened.FindName("ProviderListBox")).Items.Cast<ProviderSelectionItem>().Select(p => p.Id).SequenceEqual(new[] { "alpha", "beta", "gamma", "new_service" }), "full service order survives reopening settings");
        ((ListBox)reopened.FindName("NavigationList")).SelectedIndex = 3;
        ((TextBox)reopened.FindName("NodePathBox")).Text = Path.Combine(Root, "does-not-exist.exe");
        SelectCloseAction("Keep editing"); reopened.Close(); await Task.Delay(150);
        Check(reopened.IsVisible, "Keep editing leaves invalid edits open");
        SelectCloseAction("Discard edits"); reopened.Close(); await Task.Delay(150);
        Check(!reopened.IsVisible && await storage.GetAsync<string>("plugin_node_path") == executable, "Discard edits closes without persisting invalid text");
        await main.ApplyPreferencesAsync(Values(("close_to_tray", true)));
        Invoke(main, "CloseButton_Click", new Button(), new RoutedEventArgs());
        Check(!main.IsVisible, "main close button hides the window when close-to-tray is enabled");
        main.Show();
        Invoke(main, "MinimizeButton_Click", new Button(), new RoutedEventArgs());
        Check(main.WindowState == WindowState.Minimized, "minimize remains ordinary window minimization");
        main.Close();
    }
    private static void SelectCloseAction(string label)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "Unsaved edits");
            if (dialog == null) return;
            timer.Stop();
            var buttons = (StackPanel)((StackPanel)dialog.Content).Children[1];
            buttons.Children.OfType<Button>().Single(b => (string)b.Content == label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        };
        timer.Start();
    }
    private static async Task Drain(SettingsWindow window) { await Task.Delay(100); await (Task<bool>)Invoke(window, "CommitTextEditsAsync")!; }
    private static object? Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static void MakePlugin(string root, string id, string name, string kind)
    {
        var dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new { id, name, type = kind, runtime = "process", entry = "index.js", command = "node", arguments = "{entry}" }));
        File.WriteAllText(Path.Combine(dir, "index.js"), ""); File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
    }
    private static void Render(Window window, string path, double scale)
    {
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
