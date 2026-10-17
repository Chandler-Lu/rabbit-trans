using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RabTrans.Core.OCR;
using RabTrans.Core.Settings;
using RabTrans.Core.Storage;
using RabTrans.Core.Sync;
using RabTrans.Core.Translation;

namespace RabTrans;

public partial class SettingsWindow : Window
{
    private readonly StorageService _storageService;
    private readonly ConfigSyncService _configSyncService;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;
    private readonly Func<IReadOnlyDictionary<string, JsonElement>, Task>? _runtimeOverride;
    private readonly ObservableCollection<ProviderSelectionItem> _providerItems = new();
    private readonly ObservableCollection<ProviderSelectionItem> _ocrProviderItems = new();
    private readonly DispatcherTimer _editTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly HashSet<string> _textDirty = new();
    private readonly Dictionary<string, string> _errors = new();
    private SettingsAutoSaveSession? _session;
    private bool _loading = true, _busy, _closing, _allowClose, _closed, _proxyDirty;
    private TextBox? _recordingHotkeyBox;
    private string _recordingOriginal = "";
    private Point? _providerDragStartPoint;
    private ListBox? _dragSource;
    private ListBoxItem? _providerDropContainer;
    private bool _providerDropAfter;
    private const string AutoStartValueName = "RabTrans";
    private const string AutoStartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] PageNames = { "General", "Translation", "OCR", "Runtime & network", "Backup & restore" };
    private static readonly string[] PageDescriptions = {
        "Make RabTrans fit your workflow.", "Choose your languages and translation services.",
        "Set up screenshot recognition and its fallback order.", "Choose how your service plugins run and connect.",
        "Keep a copy of your settings and service configurations." };

    public SettingsWindow() : this(App.GetService<StorageService>(), App.GetService<TranslationService>(), App.GetService<OcrService>()) { }

    // Injectable services keep desktop verification isolated from the user's settings and startup registry.
    public SettingsWindow(StorageService storage, TranslationService translation, OcrService ocr,
        Func<IReadOnlyDictionary<string, JsonElement>, Task>? applyRuntime = null)
    {
        _storageService = storage;
        _configSyncService = new ConfigSyncService(storage);
        _translationService = translation;
        _ocrService = ocr;
        _runtimeOverride = applyRuntime;
        InitializeComponent();
        VersionTextBlock.Text = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        NavigationList.SelectedIndex = 0;
        _editTimer.Tick += async (_, _) => { _editTimer.Stop(); await CommitTextEditsAsync(); };
        Loaded += async (_, _) => { FitToMonitor(); await LoadSettingsAsync(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FitToMonitor);
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        Closing += SettingsWindow_Closing;
        Closed += (_, _) => { _closed = true; _editTimer.Stop(); CancelRecording(); };
    }

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    private static T Get<T>(IReadOnlyDictionary<string, JsonElement> values, string key, T fallback) =>
        values.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<T>()! : fallback;

    private async Task<bool> LoadSettingsAsync(bool readStartupRegistry = true)
    {
        _loading = true;
        Pages.IsEnabled = false;
        try
        {
            var saved = await _storageService.SnapshotAsync();
            var defaults = new Dictionary<string, object>
            {
                ["auto_start"] = false, ["close_to_tray"] = Get(saved, "minimize_to_tray", true),
                ["plugin_node_path"] = "", ["plugin_python_path"] = "", ["proxy_mode"] = "System", ["http_proxy"] = "",
                ["sync_history"] = false, ["default_source_lang"] = "auto", ["default_target_lang"] = "zh-CN",
                ["ocr_hotkey"] = "Alt+Shift+S", ["translate_hotkey"] = "Alt+Shift+T"
            };
            foreach (var (key, value) in defaults) saved.TryAdd(key, Json(value));
            if (_runtimeOverride == null && readStartupRegistry) saved["auto_start"] = Json(IsAutoStartEnabled());
            PopulateProviders(_providerItems, false, saved);
            PopulateProviders(_ocrProviderItems, true, saved);
            ProviderListBox.ItemsSource = _providerItems;
            OcrProviderListBox.ItemsSource = _ocrProviderItems;
            AutoStartCheckBox.IsChecked = Get(saved, "auto_start", false);
            CloseToTrayCheckBox.IsChecked = Get(saved, "close_to_tray", true);
            SyncHistoryCheckBox.IsChecked = Get(saved, "sync_history", false);
            NodePathBox.Text = Get(saved, "plugin_node_path", "");
            PythonPathBox.Text = Get(saved, "plugin_python_path", "");
            HttpProxyBox.Text = Get(saved, "http_proxy", "");
            SelectComboBoxItemByTag(ProxyModeCombo, Get(saved, "proxy_mode", "System"));
            SelectComboBoxItemByTag(DefaultSourceLangCombo, Get(saved, "default_source_lang", "auto"));
            SelectComboBoxItemByTag(DefaultTargetLangCombo, Get(saved, "default_target_lang", "zh-CN"));
            OcrHotkeyBox.Text = Get(saved, "ocr_hotkey", "Alt+Shift+S");
            TranslateHotkeyBox.Text = Get(saved, "translate_hotkey", "Alt+Shift+T");
            _session = new SettingsAutoSaveSession(_storageService, saved, ApplyRuntimeAsync);
            _session.Changed += UpdateSaveStatus;
            _errors.Clear(); _textDirty.Clear(); _proxyDirty = false;
            UpdateProxyVisibility();
            UpdateRuntimeHint(NodePathBox); UpdateRuntimeHint(PythonPathBox);
            UpdateEmptyStates();
        }
        catch { _session = null; SaveStatus.Text = "Could not load settings. Close this window and try again."; return false; }
        finally { _loading = false; Pages.IsEnabled = _session != null; }
        UpdateSaveStatus();
        return true;
    }

    private void PopulateProviders(ObservableCollection<ProviderSelectionItem> items, bool ocr, Dictionary<string, JsonElement> values)
    {
        foreach (var item in items) item.PropertyChanged -= Provider_Changed;
        items.Clear();
        var ids = ocr ? _ocrService.GetProviders() : _translationService.GetProviders();
        var enabledKey = ocr ? "enabled_ocr_providers" : "enabled_translation_providers";
        var orderKey = ocr ? "ocr_provider_order" : "translation_provider_order";
        var enabled = Get(values, enabledKey, Array.Empty<string>()).Where(ids.Contains).ToList();
        if (enabled.Count == 0 && ids.Count > 0) enabled.Add(ids[0]);
        var order = Get(values, orderKey, enabled.ToArray()).Where(ids.Contains).Concat(ids).Distinct().ToArray();
        foreach (var id in order)
        {
            var item = new ProviderSelectionItem {
                Id = id, DisplayName = ocr ? _ocrService.GetProviderDisplayName(id) : _translationService.GetProviderDisplayName(id),
                IsOcr = ocr, IsSelected = enabled.Contains(id),
                CanConfigure = (ocr ? _ocrService.GetProviderConfigPath(id) : _translationService.GetProviderConfigPath(id)) != null
            };
            item.PropertyChanged += Provider_Changed;
            items.Add(item);
        }
        values[enabledKey] = Json(order.Where(enabled.Contains).ToArray());
        values[orderKey] = Json(order);
    }

    private async Task ApplyRuntimeAsync(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (_runtimeOverride != null) { await _runtimeOverride(values); return; }
        if (values.TryGetValue("auto_start", out var autoStart)) SetAutoStart(autoStart.GetBoolean());
        if (Owner is MainWindow main) await main.ApplyPreferencesAsync(values);
    }

    private async Task QueueAsync(Dictionary<string, JsonElement> values)
    {
        if (_loading || _session == null || _closed) return;
        _session.Queue(values);
        await _session.FlushAsync();
        UpdateSaveStatus();
    }

    private void UpdateSaveStatus()
    {
        if (_closed || _session == null || _busy) return;
        var error = _session.Error ?? _errors.Values.FirstOrDefault();
        SaveStatus.Text = error ?? (_session.IsSaving ? "Saving…" : _session.HasPending || _textDirty.Count > 0 || _proxyDirty ? "Changes pending" : "Saved automatically");
        SaveStatus.Foreground = new SolidColorBrush(error == null ? Color.FromRgb(104, 114, 130) : Color.FromRgb(180, 35, 24));
        RetryButton.Visibility = _session.Error != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string key } box) await QueueAsync(new() { [key] = Json(box.IsChecked ?? false) });
    }
    private async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: string key } combo) await QueueAsync(new() { [key] = Json(GetSelectedComboBoxTag(combo)) });
    }
    private async void ProxyMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyAddressPanel == null) return;
        UpdateProxyVisibility();
        if (_loading) return;
        _proxyDirty = true;
        await CommitTextEditsAsync();
    }
    private void UpdateProxyVisibility() => ProxyAddressPanel.Visibility = GetSelectedComboBoxTag(ProxyModeCombo) == "Http" ? Visibility.Visible : Visibility.Collapsed;

    private void Path_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox { Tag: string key }) return;
        _textDirty.Add(key);
        _session?.RemovePending(key);
        _editTimer.Stop(); _editTimer.Start();
        UpdateSaveStatus();
    }
    private async void TextEdit_Completed(object sender, KeyboardFocusChangedEventArgs e) { if (!_loading) await CommitTextEditsAsync(); }
    private async void TextEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await CommitTextEditsAsync(); }
    }

    private async Task<bool> CommitTextEditsAsync()
    {
        _editTimer.Stop();
        if (_loading || _session == null) return false;
        var values = new Dictionary<string, JsonElement>();
        foreach (var box in new[] { NodePathBox, PythonPathBox })
        {
            var key = (string)box.Tag;
            if (!_textDirty.Contains(key)) continue;
            var value = box.Text.Trim();
            if (value.Length > 0 && ResolveExecutable(value) == null)
                SetFieldError(key, "Choose an existing executable or leave the path blank.");
            else { _errors.Remove(key); values[key] = Json(value); _textDirty.Remove(key); }
            UpdateRuntimeHint(box);
        }
        if (_proxyDirty || _textDirty.Contains("http_proxy"))
        {
            var mode = GetSelectedComboBoxTag(ProxyModeCombo);
            if (mode != "Http")
            {
                _errors.Remove("http_proxy"); _textDirty.Remove("http_proxy");
                values["proxy_mode"] = Json(mode); _proxyDirty = false;
            }
            else
            {
                var address = HttpProxyBox.Text.Trim();
                if (!address.Contains("://")) address = "http://" + address;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
                    SetFieldError("http_proxy", "Enter a valid HTTP or HTTPS proxy address.");
                else
                {
                    _errors.Remove("http_proxy"); _textDirty.Remove("http_proxy"); _proxyDirty = false;
                    values["proxy_mode"] = Json(mode); values["http_proxy"] = Json(address);
                }
            }
            ProxyHint.Text = _errors.GetValueOrDefault("http_proxy") ?? "Example: http://127.0.0.1:7890";
            ProxyHint.Foreground = FieldBrush("http_proxy");
        }
        if (values.Count > 0) _session.Queue(values);
        var success = await _session.FlushAsync();
        UpdateSaveStatus();
        return success && _errors.Count == 0;
    }

    private void SetFieldError(string key, string message) { _errors[key] = message; _session?.RemovePending(key); }
    private Brush FieldBrush(string key) => _errors.ContainsKey(key) ? new SolidColorBrush(Color.FromRgb(180, 35, 24)) : new SolidColorBrush(Color.FromRgb(104, 114, 130));
    private void UpdateRuntimeHint(TextBox box)
    {
        var key = (string)box.Tag;
        var hint = ReferenceEquals(box, NodePathBox) ? NodePathHint : PythonPathHint;
        var value = box.Text.Trim();
        var resolved = ResolveExecutable(value.Length > 0 ? value : ReferenceEquals(box, NodePathBox) ? "node" : "python");
        hint.Text = _errors.GetValueOrDefault(key) ?? (resolved == null ? "Not found on PATH. Browse to an executable to use this runtime." : (value.Length == 0 ? "Automatic: " : "Using: ") + resolved);
        hint.Foreground = FieldBrush(key);
    }
    private static string? ResolveExecutable(string value)
    {
        if (Path.IsPathRooted(value)) return File.Exists(value) && new[] { ".exe", ".cmd", ".bat" }.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase) ? value : null;
        if (value.IndexOfAny(new[] { '/', '\\', '"' }) >= 0) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var suffix in Path.HasExtension(value) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat" })
        {
            try { var candidate = Path.Combine(directory.Trim(' ', '"'), value + suffix); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }
    private async void BrowseRuntime_Click(object sender, RoutedEventArgs e)
    {
        var box = ((Button)sender).Tag?.ToString() == "Node" ? NodePathBox : PythonPathBox;
        var dialog = new OpenFileDialog { Filter = "Executables (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { box.Text = dialog.FileName; await CommitTextEditsAsync(); }
    }
    private async void ResetRuntime_Click(object sender, RoutedEventArgs e)
    {
        var box = ((Button)sender).Tag?.ToString() == "Node" ? NodePathBox : PythonPathBox;
        box.Text = ""; await CommitTextEditsAsync();
    }

    private async void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Pages == null || NavigationList.SelectedIndex < 0) return;
        CancelRecording();
        var index = NavigationList.SelectedIndex;
        for (var i = 0; i < Pages.Children.Count; i++) Pages.Children[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageNames[index]; PageDescription.Text = PageDescriptions[index]; PageScroll.ScrollToTop();
        if (!_loading) await CommitTextEditsAsync();
    }

    private async void Provider_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || sender is not ProviderSelectionItem item || e.PropertyName != nameof(item.IsSelected)) return;
        var items = item.IsOcr ? _ocrProviderItems : _providerItems;
        var error = item.IsOcr ? OcrServiceError : TranslationServiceError;
        if (!items.Any(p => p.IsSelected))
        {
            _loading = true; item.IsSelected = true; _loading = false;
            error.Text = "Keep at least one service enabled."; error.Visibility = Visibility.Visible;
            return;
        }
        error.Visibility = Visibility.Collapsed;
        await SaveProviderOrderAsync(item.IsOcr);
    }
    private Task SaveProviderOrderAsync(bool ocr)
    {
        var items = ocr ? _ocrProviderItems : _providerItems;
        return QueueAsync(new() {
            [ocr ? "enabled_ocr_providers" : "enabled_translation_providers"] = Json(items.Where(p => p.IsSelected).Select(p => p.Id).ToArray()),
            [ocr ? "ocr_provider_order" : "translation_provider_order"] = Json(items.Select(p => p.Id).ToArray())
        });
    }
    private void UpdateEmptyStates()
    {
        TranslationEmpty.Visibility = _providerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OcrEmpty.Visibility = _ocrProviderItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshServices_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null) return;
        CancelRecording();
        var ocr = ((Button)sender).Tag?.ToString() == "ocr";
        try
        {
            _loading = true;
            var items = ocr ? _ocrProviderItems : _providerItems;
            var state = _session.Saved.ToDictionary();
            state[ocr ? "enabled_ocr_providers" : "enabled_translation_providers"] = Json(items.Where(p => p.IsSelected).Select(p => p.Id).ToArray());
            state[ocr ? "ocr_provider_order" : "translation_provider_order"] = Json(items.Select(p => p.Id).ToArray());
            if (ocr) _ocrService.ReloadPlugins(); else _translationService.ReloadProviders();
            PopulateProviders(items, ocr, state);
            UpdateEmptyStates();
        }
        catch { SaveStatus.Text = "Could not refresh services. Check the plugin files and retry."; return; }
        finally { _loading = false; }
        await SaveProviderOrderAsync(ocr);
    }

    private void RecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        CancelRecording();
        _recordingHotkeyBox = ((Button)sender).Tag?.ToString() == "ocr" ? OcrHotkeyBox : TranslateHotkeyBox;
        _recordingOriginal = _recordingHotkeyBox.Text;
        if (Owner is MainWindow main) main.SuspendConfiguredHotkeys();
        _recordingHotkeyBox.Text = "Press a shortcut…";
        _recordingHotkeyBox.Focus();
    }
    private void CancelRecording()
    {
        if (_recordingHotkeyBox == null) return;
        _recordingHotkeyBox.Text = _recordingOriginal; _recordingHotkeyBox = null;
        if (Owner is MainWindow main) main.ResumeConfiguredHotkeys();
    }
    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _recordingHotkeyBox)) CancelRecording();
    }
    private async void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingHotkeyBox == null)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape) { CancelRecording(); return; }
        var hotkey = FormatHotkey(e);
        if (string.IsNullOrEmpty(hotkey)) return;
        var box = _recordingHotkeyBox;
        var other = ReferenceEquals(box, OcrHotkeyBox) ? TranslateHotkeyBox.Text : OcrHotkeyBox.Text;
        var hint = ReferenceEquals(box, OcrHotkeyBox) ? OcrHotkeyHint : TranslateHotkeyHint;
        if (!MainWindow.TryParseHotkey(hotkey, out var modifiers, out var key) ||
            (MainWindow.TryParseHotkey(other, out var om, out var ok) && modifiers == om && key == ok))
        {
            hint.Text = "Choose a supported shortcut that differs from the other action.";
            return;
        }
        CancelRecording();
        box.Text = hotkey;
        await QueueAsync(new() { [ReferenceEquals(box, OcrHotkeyBox) ? "ocr_hotkey" : "translate_hotkey"] = Json(hotkey) });
        hint.Text = _session?.Error ?? "Record a shortcut. Press Esc to cancel.";
    }

    private async void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !await CommitTextEditsAsync()) return;
        var dialog = new SaveFileDialog { Filter = "RabTrans backup (*.zip)|*.zip", FileName = "RabTransConfig.zip" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true, "Exporting backup…");
        try
        {
            var history = SyncHistoryCheckBox.IsChecked ?? false;
            await Task.Run(() => _configSyncService.CreateConfigPackage(dialog.FileName, history));
            BackupStatus.Text = "Backup exported.";
        }
        catch { BackupStatus.Text = "Could not export the backup. Check the destination and try again."; }
        finally { SetBusy(false); }
    }
    private async void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !await CommitTextEditsAsync()) return;
        var dialog = new OpenFileDialog { Filter = "RabTrans backup (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true || MessageBox.Show(this,
            "Replace local settings and plugins with this backup? A recovery copy of the current configuration will be saved first.",
            "Import backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true, "Restoring backup…");
        var recovery = Path.Combine(_storageService.AppDataPath, "backups", "before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip");
        var backupCreated = false;
        var previousRuntime = _session!.Saved.ToDictionary();
        var hadHistory = File.Exists(_storageService.HistoryPath);
        try
        {
            await Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(recovery)!); _configSyncService.CreateConfigPackage(recovery, true); });
            backupCreated = true;
            await Task.Run(() => _configSyncService.ImportConfigPackage(dialog.FileName));
            _translationService.ReloadProviders(); _ocrService.ReloadPlugins();
            if (Owner is MainWindow main) await main.ReloadConfigurationAsync();
            if (!await LoadSettingsAsync(false)) throw new InvalidOperationException("Invalid settings in backup.");
            await ApplyRuntimeAsync(_session!.Saved);
            BackupStatus.Text = "Backup restored. A recovery copy is available in the backups folder.";
        }
        catch
        {
            if (backupCreated)
            {
                try
                {
                    await Task.Run(() => {
                        _configSyncService.ImportConfigPackage(recovery);
                        if (!hadHistory && File.Exists(_storageService.HistoryPath)) File.Delete(_storageService.HistoryPath);
                    });
                    _translationService.ReloadProviders(); _ocrService.ReloadPlugins();
                    if (Owner is MainWindow main) await main.ReloadConfigurationAsync();
                    await ApplyRuntimeAsync(previousRuntime);
                    if (!await LoadSettingsAsync()) throw new InvalidOperationException("Could not reload recovery settings.");
                    BackupStatus.Text = "Import failed. The previous configuration was restored.";
                }
                catch { BackupStatus.Text = "Import failed. Restore the recovery ZIP in the backups folder before continuing."; }
            }
            else BackupStatus.Text = "Could not create a recovery backup. Nothing was imported.";
        }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy; Pages.IsEnabled = !busy && _session != null; NavigationList.IsEnabled = !busy;
        if (busy) SaveStatus.Text = message; else UpdateSaveStatus();
    }
    private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_storageService.AppDataPath) { UseShellExecute = true }); }
        catch { SaveStatus.Text = "Could not open the configuration folder."; }
    }
    private void OpenServiceConfig_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProviderSelectionItem item }) return;
        try
        {
            var path = item.IsOcr ? _ocrService.OpenProviderConfiguration(item.Id) : _translationService.OpenProviderConfiguration(item.Id);
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch { SaveStatus.Text = "Could not open config.json. Check file access and try again."; }
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await CommitTextEditsAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || _busy) return;
        _closing = true;
        CancelRecording();
        try
        {
            while (_session != null && !await CommitTextEditsAsync())
            {
                var choice = ChooseCloseAction();
                if (choice == "Retry") continue;
                if (choice != "Discard edits") return;
                if (!await _session.DiscardPendingAsync()) continue;
                break;
            }
            _allowClose = true;
            // A completed save may not yield: leave the current Closing event before closing again.
            _ = Dispatcher.BeginInvoke(Close);
        }
        finally { _closing = false; }
    }
    private string ChooseCloseAction()
    {
        var choice = "Keep editing";
        var dialog = new Window { Title = "Unsaved edits", Owner = this, SizeToContent = SizeToContent.Height, Width = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, FontFamily = FontFamily, FontSize = 13 };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Some edits could not be saved. Retry, or discard only those edits and close. Your saved settings will be kept.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var label in new[] { "Retry", "Discard edits", "Keep editing" })
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => { choice = label; dialog.Close(); }; buttons.Children.Add(button);
        }
        content.Children.Add(buttons); dialog.Content = content; dialog.ShowDialog(); return choice;
    }

    private void FitToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return;
        var scale = VisualTreeHelper.GetDpi(this);
        var width = (info.Work.Right - info.Work.Left) / scale.DpiScaleX - 16;
        var height = (info.Work.Bottom - info.Work.Top) / scale.DpiScaleY - 16;
        MinWidth = Math.Min(680, width); MinHeight = Math.Min(480, height);
        Width = Math.Min(Width, width); Height = Math.Min(Height, height);
        Left = Math.Clamp(Left, info.Work.Left / scale.DpiScaleX, info.Work.Right / scale.DpiScaleX - Width);
        Top = Math.Clamp(Top, info.Work.Top / scale.DpiScaleY, info.Work.Bottom / scale.DpiScaleY - Height);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRunKeyPath, false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(AutoStartValueName)?.ToString());
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRunKeyPath, true)
            ?? Registry.CurrentUser.CreateSubKey(AutoStartRunKeyPath, true);

        if (key == null)
        {
            throw new InvalidOperationException("Unable to open Windows startup registry key.");
        }

        if (enabled)
        {
            key.SetValue(AutoStartValueName, $"\"{GetExecutablePath()}\" --silent", RegistryValueKind.String);
            return;
        }

        key.DeleteValue(AutoStartValueName, false);
    }

    private static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Unable to resolve RabTrans executable path.");
        }

        return path;
    }

    private static string FormatHotkey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(GetKeyName(key));
        return string.Join("+", parts);
    }

    private static string GetKeyName(Key key)
    {
        return key switch
        {
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Space => "Space",
            Key.PrintScreen => "PrintScreen",
            _ => key.ToString().ToUpperInvariant().StartsWith("D", StringComparison.Ordinal) && key.ToString().Length == 2
                ? key.ToString()[1..]
                : key.ToString()
        };
    }

    private void ProviderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var handle = FindAncestor<Button>(e.OriginalSource as DependencyObject);
        if (handle?.Tag?.ToString() != "DragHandle")
        {
            _providerDragStartPoint = null;
            return;
        }

        if (sender is ListBox listBox)
        {
            _providerDragStartPoint = e.GetPosition(listBox);
            _dragSource = listBox;
            listBox.SelectedItem = handle.DataContext;
        }
    }

    private void ProviderListBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (_providerDragStartPoint == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (sender is not ListBox listBox)
        {
            return;
        }

        var currentPosition = e.GetPosition(listBox);
        if (Math.Abs(currentPosition.X - _providerDragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _providerDragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ProviderSelectionItem item)
        {
            DragDrop.DoDragDrop(listBox, item, DragDropEffects.Move);
        }

        ClearProviderDropIndicator();
        _providerDragStartPoint = null;
        _dragSource = null;
    }

    private void ProviderListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ReferenceEquals(sender, _dragSource) && e.Data.GetDataPresent(typeof(ProviderSelectionItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;

        if (e.Effects == DragDropEffects.Move)
        {
            var point = e.GetPosition(PageScroll);
            if (point.Y < 32) PageScroll.LineUp();
            else if (point.Y > PageScroll.ActualHeight - 32) PageScroll.LineDown();
        }
        if (e.Effects == DragDropEffects.Move &&
            FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { } targetContainer)
        {
            var position = e.GetPosition(targetContainer);
            SetProviderDropIndicator(targetContainer, position.Y > targetContainer.ActualHeight / 2);
        }
        else
        {
            ClearProviderDropIndicator();
        }

        e.Handled = true;
    }

    private void ProviderListBox_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBox { IsMouseOver: false })
        {
            ClearProviderDropIndicator();
        }
    }

    private async void ProviderListBox_Drop(object sender, DragEventArgs e)
    {
        if (!ReferenceEquals(sender, _dragSource) || !e.Data.GetDataPresent(typeof(ProviderSelectionItem)))
        {
            ClearProviderDropIndicator();
            return;
        }

        if (sender is not ListBox listBox)
        {
            ClearProviderDropIndicator();
            return;
        }

        var items = GetProviderItems(listBox);
        var sourceItem = (ProviderSelectionItem)e.Data.GetData(typeof(ProviderSelectionItem))!;
        var targetContainer = _providerDropContainer ?? FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetItem = targetContainer?.DataContext as ProviderSelectionItem;
        if (targetItem == null || ReferenceEquals(sourceItem, targetItem))
        {
            ClearProviderDropIndicator();
            return;
        }

        var sourceIndex = items.IndexOf(sourceItem);
        var targetIndex = items.IndexOf(targetItem);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            ClearProviderDropIndicator();
            return;
        }

        var insertIndex = targetIndex + (_providerDropAfter ? 1 : 0);
        if (sourceIndex < insertIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, items.Count - 1);
        items.Move(sourceIndex, insertIndex);
        listBox.SelectedItem = sourceItem;
        ClearProviderDropIndicator();
        await SaveProviderOrderAsync(ReferenceEquals(listBox, OcrProviderListBox));
    }

    private void SetProviderDropIndicator(ListBoxItem targetContainer, bool after)
    {
        if (!ReferenceEquals(_providerDropContainer, targetContainer))
        {
            ClearProviderDropIndicator();
        }

        _providerDropContainer = targetContainer;
        _providerDropAfter = after;
        targetContainer.BorderBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        targetContainer.BorderThickness = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
    }

    private void ClearProviderDropIndicator()
    {
        if (_providerDropContainer == null)
        {
            return;
        }

        _providerDropContainer.ClearValue(BorderBrushProperty);
        _providerDropContainer.ClearValue(BorderThicknessProperty);
        _providerDropContainer = null;
        _providerDropAfter = false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private ObservableCollection<ProviderSelectionItem> GetProviderItems(ListBox listBox)
    {
        return ReferenceEquals(listBox, OcrProviderListBox) ? _ocrProviderItems : _providerItems;
    }

    private async void ProviderListBox_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down) || sender is not ListBox list || list.SelectedItem is not ProviderSelectionItem selected) return;
        e.Handled = true;
        var items = GetProviderItems(list);
        var current = items.IndexOf(selected);
        var next = Math.Clamp(current + (key == Key.Up ? -1 : 1), 0, items.Count - 1);
        if (current != next) { items.Move(current, next); list.SelectedItem = selected; await SaveProviderOrderAsync(ReferenceEquals(list, OcrProviderListBox)); }
    }
    private void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                comboBox.SelectedItem = item;
                break;
            }
        }
    }

    private string GetSelectedComboBoxTag(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }
        return "";
    }
}

public class ProviderSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public bool CanConfigure { get; set; }
    public bool IsOcr { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
