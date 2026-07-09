using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RabTrans.Core.Clipboard;
using RabTrans.Core.Hotkey;
using RabTrans.Core.OCR;
using RabTrans.Core.Plugins;
using RabTrans.Core.Screenshot;
using RabTrans.Core.Storage;
using RabTrans.Core.Translation;
using Serilog;

namespace RabTrans;

/// <summary>
/// Main window for RabTrans.
/// </summary>
public partial class MainWindow : Window
{
    private HotkeyService? _hotkeyService;
    private ClipboardMonitorService? _clipboardMonitor;
    private TranslationService? _translationService;
    private OcrService? _ocrService;
    private ScreenshotService? _screenshotService;
    private StorageService? _storageService;
    private List<string> _enabledProviders = new();
    private List<string> _enabledOcrProviders = new();
    private string _lastTranslationText = string.Empty;
    private int _ocrHotkeyId = -1;
    private int _translateHotkeyId = -1;
    private bool _isUpdatingLanguageSelection;
    private bool _isApplyingContentLayout;
    private bool _hideOnDeactivate;
    private bool _pinWindow;
    private bool _isOpeningChildWindow;
    private bool _isSettingSourceTextProgrammatically;
    private bool _targetLanguageOverriddenByUser;
    private string? _targetLanguageOverrideSourceLang;
    private int _translationRequestVersion;
    private Point? _lastWindowAnchorPoint;
    private readonly DispatcherTimer _widthLayoutTimer;

    private const int WM_HOTKEY = 0x0312;

    public MainWindow()
    {
        InitializeComponent();
        _widthLayoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _widthLayoutTimer.Tick += (_, _) =>
        {
            _widthLayoutTimer.Stop();
            UpdateContentLayout(adjustWidth: false, adjustHeight: false);
        };
        
        // Handle window messages for hotkeys and clipboard
        SourceInitialized += MainWindow_SourceInitialized;
        SizeChanged += MainWindow_SizeChanged;
        Deactivated += MainWindow_Deactivated;
        Closing += MainWindow_Closing;
    }

    public void InitializeServices()
    {
        try
        {
            _storageService = App.GetService<StorageService>();
            _translationService = App.GetService<TranslationService>();

            Log.Information("Services initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize services");
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        _hotkeyService = new HotkeyService(hwnd);

        _clipboardMonitor = new ClipboardMonitorService(hwnd);
        
        LoadSettings();

        Log.Information("Window initialized with hotkeys");
    }

    private void LoadSettings()
    {
        try
        {
            _hideOnDeactivate = _storageService?.GetAsync<bool>("hide_on_deactivate").Result ?? false;
            _pinWindow = _storageService?.GetAsync<bool>("pin_window").Result ?? false;
            Topmost = _pinWindow;
            UpdateWindowOptionButtons();
            PluginRuntimeOptions.NodeExecutablePath = _storageService?.GetAsync<string>("plugin_node_path").Result ?? string.Empty;
            _enabledProviders = _storageService?.GetAsync<List<string>>("enabled_translation_providers").Result ?? new List<string>();
            if (_enabledProviders.Count == 0 && _translationService != null)
            {
                _enabledProviders = _translationService.GetProviders().Take(1).ToList();
            }

            if (_ocrService != null)
            {
                _enabledOcrProviders = _storageService?.GetAsync<List<string>>("enabled_ocr_providers").Result ?? new List<string>();
                if (_enabledOcrProviders.Count == 0)
                {
                    _enabledOcrProviders = _ocrService.GetProviders().Take(1).ToList();
                }
            }

            var defaultSourceLang = _storageService?.GetAsync<string>("default_source_lang").Result ?? "auto";
            var defaultTargetLang = _storageService?.GetAsync<string>("default_target_lang").Result ?? "zh-CN";
            SetSelectedLanguage(SourceLanguageCombo, defaultSourceLang, allowAuto: true);
            SetSelectedLanguage(TargetLanguageCombo, defaultTargetLang, allowAuto: false);

            if (_enabledProviders.Count > 0)
            {
                _translationService?.SetProvider(_enabledProviders[0]);
            }

            RegisterConfiguredHotkeys();

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                _hotkeyService?.ProcessMessage(wParam);
                handled = true;
                break;

        }

        return IntPtr.Zero;
    }

    private async Task CaptureScreenshotAsync()
    {
        try
        {
            _screenshotService ??= App.GetService<ScreenshotService>();
            _ocrService ??= App.GetService<OcrService>();
            if (_enabledOcrProviders.Count == 0)
            {
                _enabledOcrProviders = _storageService?.GetAsync<List<string>>("enabled_ocr_providers").Result ?? new List<string>();
                if (_enabledOcrProviders.Count == 0)
                {
                    _enabledOcrProviders = _ocrService.GetProviders().Take(1).ToList();
                }
            }

            Hide();
            await Task.Delay(150);

            var selector = new ScreenshotSelectionWindow();
            var selected = selector.ShowDialog() == true ? selector.SelectedScreenRegion : null;

            if (selected == null)
            {
                Show();
                Activate();
                StatusText.Text = "OCR canceled";
                return;
            }

            var region = selected.Value;
            var screenshotStream = await _screenshotService.CaptureRegionAsync(
                (int)Math.Round(region.X),
                (int)Math.Round(region.Y),
                (int)Math.Round(region.Width),
                (int)Math.Round(region.Height));
            if (screenshotStream != null)
            {
                StatusText.Text = "Recognizing...";
                var ocrText = await _ocrService.RecognizeAsync(screenshotStream, _enabledOcrProviders);
                if (!string.IsNullOrEmpty(ocrText))
                {
                    SetSourceText(ocrText, resetTargetLanguageOverride: true);
                    ResizeToContent(ocrText, _lastTranslationText);
                    ShowNearCursor();
                    await TranslateAsync();
                }
                else
                {
                    ShowNearCursor();
                    StatusText.Text = "No text recognized by OCR plugin";
                }
            }

            ShowNearCursor();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshot capture failed");
            ShowNearCursor();
            var message = ex.Message.Length > 140 ? ex.Message[..140] + "..." : ex.Message;
            StatusText.Text = $"OCR error: {message}";
        }
    }

    public async Task StartOcrAsync()
    {
        await CaptureScreenshotAsync();
    }

    public void ShowInputTranslate()
    {
        ShowNearCursor();
    }

    public async Task TranslateSelectionAsync(uint triggerKey = 0)
    {
        var anchorPoint = TryGetCursorPoint();
        var selectedText = await TryReadSelectedTextAsync(triggerKey);

        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            SetSourceText(selectedText, resetTargetLanguageOverride: true);
            ResizeToContent(selectedText, _lastTranslationText);
            ShowNearCursor(anchorPoint);
            await TranslateAsync();
            return;
        }

        ShowNearCursor(anchorPoint);
    }

    public void ReloadConfiguration()
    {
        _translationService?.ReloadProviders();
        _ocrService?.ReloadPlugins();
        LoadSettings();
        StatusText.Text = "Configuration reloaded";
    }

    public void SuspendConfiguredHotkeys()
    {
        _hotkeyService?.UnregisterAll();
        _ocrHotkeyId = -1;
        _translateHotkeyId = -1;
    }

    public void ResumeConfiguredHotkeys()
    {
        RegisterConfiguredHotkeys();
    }

    public void ShowNearCursor()
    {
        ShowNearCursor(TryGetCursorPoint());
    }

    private void ShowNearCursor(Point? anchorPoint)
    {
        if (anchorPoint is Point point)
        {
            _lastWindowAnchorPoint = point;
            PositionNearPoint(point);
        }

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private Point? TryGetCursorPoint()
    {
        if (!GetCursorPos(out var point))
        {
            return null;
        }

        var dipPoint = new Point(point.X, point.Y);
        if (PresentationSource.FromVisual(this) is HwndSource source &&
            source.CompositionTarget != null)
        {
            dipPoint = source.CompositionTarget.TransformFromDevice.Transform(dipPoint);
        }

        return dipPoint;
    }

    private void PositionNearPoint(Point point)
    {
        var workArea = SystemParameters.WorkArea;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        const double horizontalOffset = 18;
        const double verticalOffset = 18;
        const double edgePadding = 8;

        var nextLeft = point.X + horizontalOffset;
        if (nextLeft + windowWidth > workArea.Right - edgePadding)
        {
            nextLeft = point.X - windowWidth - horizontalOffset;
        }

        var nextTop = point.Y + verticalOffset;
        if (nextTop + windowHeight > workArea.Bottom - edgePadding)
        {
            nextTop = point.Y - windowHeight - verticalOffset;
        }

        Left = Math.Clamp(nextLeft, workArea.Left + edgePadding, workArea.Right - windowWidth - edgePadding);
        Top = Math.Clamp(nextTop, workArea.Top + edgePadding, workArea.Bottom - windowHeight - edgePadding);
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        await TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        var requestVersion = ++_translationRequestVersion;
        var sourceText = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return;
        }

        StatusText.Text = "Translating...";
        TranslateButton.IsEnabled = false;

        try
        {
            var sourceLang = GetSelectedLanguage(SourceLanguageCombo);

            // Auto-detect source language if needed
            if (sourceLang == "auto")
            {
                sourceLang = await _translationService!.DetectLanguageAsync(sourceText);
            }

            var targetLang = GetSelectedLanguage(TargetLanguageCombo);
            if (_targetLanguageOverriddenByUser)
            {
                if (string.IsNullOrWhiteSpace(_targetLanguageOverrideSourceLang))
                {
                    _targetLanguageOverrideSourceLang = sourceLang;
                }
                else if (!string.Equals(_targetLanguageOverrideSourceLang, sourceLang, StringComparison.OrdinalIgnoreCase))
                {
                    _targetLanguageOverriddenByUser = false;
                    _targetLanguageOverrideSourceLang = null;
                }
            }

            if (!_targetLanguageOverriddenByUser)
            {
                targetLang = GetSmartTargetLanguage(sourceLang);
                _isUpdatingLanguageSelection = true;
                try
                {
                    SetSelectedLanguage(TargetLanguageCombo, targetLang, allowAuto: false);
                }
                finally
                {
                    _isUpdatingLanguageSelection = false;
                }
            }

            var providers = _enabledProviders.Count > 0 ? _enabledProviders : null;
            var results = new List<TranslationResult>();
            var successfulCount = 0;
            ShowTranslationDisplayItems(results);

            await foreach (var result in _translationService!.TranslateWithProvidersAsCompletedAsync(sourceText, sourceLang, targetLang, providers))
            {
                if (requestVersion != _translationRequestVersion)
                {
                    return;
                }

                results.Add(result);
                ShowTranslationDisplayItems(results);

                if (result.Success)
                {
                    successfulCount++;
                    StatusText.Text = successfulCount == 1
                    ? "Translation complete"
                    : $"Translation complete ({successfulCount} services)";

                    if (_storageService != null)
                    {
                        await _storageService.AddHistoryAsync(new TranslationHistoryItem
                        {
                            SourceText = sourceText,
                            TranslatedText = result.TranslatedText,
                            SourceLang = sourceLang ?? "auto",
                            TargetLang = targetLang,
                            Provider = result.ProviderName,
                            Timestamp = DateTime.Now
                        });
                    }
                }
                else
                {
                    StatusText.Text = successfulCount > 0
                        ? $"Translation complete ({successfulCount} services), some failed"
                        : "Translation failed";
                }
            }

            if (requestVersion != _translationRequestVersion)
            {
                return;
            }

            if (successfulCount == 0)
            {
                StatusText.Text = "Translation failed";
            }
            else if (results.Any(result => !result.Success))
            {
                StatusText.Text = $"Translation complete ({successfulCount} services), some failed";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translation failed");
            StatusText.Text = "Translation error";
        }
        finally
        {
            if (requestVersion == _translationRequestVersion)
            {
                TranslateButton.IsEnabled = true;
            }
        }
    }

    private void ShowTranslationDisplayItems(IReadOnlyList<TranslationResult> results)
    {
        ShowTranslationDisplayItems(results.Select(ToDisplayItem).ToList());
    }

    private void ShowTranslationDisplayItems(IReadOnlyList<TranslationDisplayItem> items)
    {
        TranslationResultsList.ItemsSource = items;
        _lastTranslationText = string.Join(Environment.NewLine + Environment.NewLine, items.Select(item => $"[{item.ProviderName}]{Environment.NewLine}{item.Text}"));
        TranslatedTextBox.Text = _lastTranslationText;
        ResizeToContent(SourceTextBox.Text, _lastTranslationText);
        QueueResultScrollViewerLayoutSync();
    }

    private static TranslationDisplayItem ToDisplayItem(TranslationResult result)
    {
        return new TranslationDisplayItem
        {
            ProviderName = string.IsNullOrWhiteSpace(result.ProviderName) ? "provider" : result.ProviderName,
            Text = result.Success ? result.TranslatedText : result.ErrorMessage ?? "Failed",
            StatusText = result.Success ? "OK" : "Error",
            StatusBrush = result.Success ? Brushes.SeaGreen : Brushes.IndianRed
        };
    }

    private static string GetSmartTargetLanguage(string sourceLang)
    {
        return sourceLang.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || sourceLang.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || sourceLang.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "zh-CN";
    }

    private void ResizeToContent(string sourceText, string translatedText, bool adjustWidth = true)
    {
        UpdateContentLayout(adjustWidth, adjustHeight: true);
    }

    private void UpdateContentLayout(bool adjustWidth = true, bool adjustHeight = true)
    {
        if (WindowState == WindowState.Maximized || _isApplyingContentLayout)
        {
            return;
        }

        _isApplyingContentLayout = true;
        try
        {
            var workArea = SystemParameters.WorkArea;
            var targetWidth = ActualWidth > 0 ? ActualWidth : Width;
            if (adjustWidth)
            {
                var sourceText = SourceTextBox.Text;
                var translatedText = _lastTranslationText;
                var longestLineLength = new[] { sourceText, translatedText }
                    .SelectMany(text => (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    .DefaultIfEmpty(string.Empty)
                    .Max(line => line.Length);
                targetWidth = Math.Clamp(360 + longestLineLength * 2.8, MinWidth, Math.Min(560, workArea.Width - 32));
                Width = targetWidth;
            }

            var contentWidth = Math.Max(280, targetWidth - 58);
            var resultItems = GetResultItems();
            var resultCount = Math.Max(1, resultItems.Count);
            var resultLines = resultItems.Count == 0
                ? 1
                : resultItems.Sum(item => EstimateWrappedLineCount(item.Text, contentWidth));

            const double sourcePaneHeight = 128;
            const double resultHeaderHeight = 42;
            var chromeHeight = 30 + 48 + 28 + 34;
            SourcePaneRow.Height = new GridLength(sourcePaneHeight);

            var desiredResultHeight = resultHeaderHeight + resultLines * 18 + resultCount * 50;
            var maxWindowHeight = Math.Min(820, workArea.Height - 32);
            var maxResultHeight = Math.Min(560, maxWindowHeight - chromeHeight - sourcePaneHeight - 10);
            var resultHeight = Math.Clamp(desiredResultHeight, 150, Math.Max(150, maxResultHeight));
            ResultPaneRow.Height = new GridLength(1, GridUnitType.Star);

            if (adjustHeight)
            {
                Height = Math.Clamp(chromeHeight + sourcePaneHeight + 10 + resultHeight, MinHeight, maxWindowHeight);
            }

            QueueResultScrollViewerLayoutSync();
        }
        finally
        {
            _isApplyingContentLayout = false;
        }
    }

    private void QueueResultScrollViewerLayoutSync()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var visibleHeight = ResultScrollHost.ActualHeight;
            if (visibleHeight <= 0)
            {
                return;
            }

            ResultScrollViewer.Height = visibleHeight;
            ResultScrollViewer.InvalidateMeasure();
            ResultScrollViewer.UpdateLayout();
        }, DispatcherPriority.Loaded);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || !e.WidthChanged || _isApplyingContentLayout)
        {
            return;
        }

        _widthLayoutTimer.Stop();
        _widthLayoutTimer.Start();
    }

    private List<TranslationDisplayItem> GetResultItems()
    {
        return TranslationResultsList.ItemsSource is IEnumerable<TranslationDisplayItem> items
            ? items.ToList()
            : new List<TranslationDisplayItem>();
    }

    private static int EstimateWrappedLineCount(string? text, double contentWidth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        var charsPerLine = Math.Max(18, (int)(contentWidth / 7.2));
        return text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)charsPerLine)));
    }

    private string GetSelectedLanguage(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            return lang;
        }
        return "en";
    }

    private void Language_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingLanguageSelection || SourceTextBox is null || _translationService is null)
        {
            return;
        }

        if (ReferenceEquals(sender, TargetLanguageCombo))
        {
            _targetLanguageOverriddenByUser = true;
            _targetLanguageOverrideSourceLang = null;
        }
        else if (ReferenceEquals(sender, SourceLanguageCombo))
        {
            _targetLanguageOverriddenByUser = false;
            _targetLanguageOverrideSourceLang = null;
        }

        // Auto-translate when language changes
        if (!string.IsNullOrWhiteSpace(SourceTextBox.Text))
        {
            _ = TranslateAsync();
        }
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        var sourceLang = GetSelectedLanguage(SourceLanguageCombo);
        var targetLang = GetSelectedLanguage(TargetLanguageCombo);

        SetSelectedLanguage(SourceLanguageCombo, targetLang, allowAuto: true);
        SetSelectedLanguage(TargetLanguageCombo, GetSmartTargetLanguage(targetLang), allowAuto: false);
        _targetLanguageOverriddenByUser = false;
        _targetLanguageOverrideSourceLang = null;

        // Swap text
        var sourceText = SourceTextBox.Text;
        var translatedText = _lastTranslationText;
        
        SetSourceText(translatedText, resetTargetLanguageOverride: false);
        TranslatedTextBox.Text = sourceText;
        TranslationResultsList.ItemsSource = null;
        _lastTranslationText = sourceText;
        ResizeToContent(translatedText, sourceText);
    }

    private void SourceText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isSettingSourceTextProgrammatically && _targetLanguageOverriddenByUser)
        {
            _targetLanguageOverrideSourceLang = null;
        }

        ResizeToContent(SourceTextBox.Text, _lastTranslationText);
    }

    private void SetSourceText(string text, bool resetTargetLanguageOverride)
    {
        if (resetTargetLanguageOverride)
        {
            _targetLanguageOverriddenByUser = false;
            _targetLanguageOverrideSourceLang = null;
        }

        _isSettingSourceTextProgrammatically = true;
        try
        {
            SourceTextBox.Text = text;
        }
        finally
        {
            _isSettingSourceTextProgrammatically = false;
        }
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureScreenshotAsync();
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        var clipboardText = _clipboardMonitor?.GetClipboardText();
        if (!string.IsNullOrEmpty(clipboardText))
        {
            SetSourceText(clipboardText, resetTargetLanguageOverride: true);
            ResizeToContent(clipboardText, _lastTranslationText);
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _isOpeningChildWindow = true;
        try
        {
            var historyWindow = new HistoryWindow
            {
                Owner = this
            };
            historyWindow.ShowDialog();
        }
        finally
        {
            _isOpeningChildWindow = false;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var translatedText = _lastTranslationText;
        if (!string.IsNullOrEmpty(translatedText))
        {
            _clipboardMonitor?.SetClipboardText(translatedText);
            StatusText.Text = "Copied to clipboard";
        }
    }

    private void CopySingleResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && !string.IsNullOrWhiteSpace(text))
        {
            _clipboardMonitor?.SetClipboardText(text);
            StatusText.Text = "Copied result";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Open settings window
        _isOpeningChildWindow = true;
        try
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
            {
                LoadSettings();
            }
        }
        finally
        {
            _isOpeningChildWindow = false;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "\uE739"; // Maximize icon
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "\uE923"; // Restore icon
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Minimize to tray instead of closing
        Hide();
    }

    private async void PinWindowButton_Click(object sender, RoutedEventArgs e)
    {
        _pinWindow = !_pinWindow;
        Topmost = _pinWindow;
        UpdateWindowOptionButtons();

        if (_storageService != null)
        {
            await _storageService.SetAsync("pin_window", _pinWindow);
        }
    }

    private async void HideOnDeactivateButton_Click(object sender, RoutedEventArgs e)
    {
        _hideOnDeactivate = !_hideOnDeactivate;
        UpdateWindowOptionButtons();

        if (_storageService != null)
        {
            await _storageService.SetAsync("hide_on_deactivate", _hideOnDeactivate);
        }
    }

    private void UpdateWindowOptionButtons()
    {
        if (PinWindowButton == null || HideOnDeactivateButton == null)
        {
            return;
        }

        PinWindowButton.Foreground = _pinWindow
            ? FindResource("PrimaryBrush") as Brush
            : FindResource("TextSecondaryBrush") as Brush;
        PinWindowButton.ToolTip = _pinWindow ? "Unpin window" : "Pin window";

        HideOnDeactivateButton.Foreground = _hideOnDeactivate
            ? FindResource("PrimaryBrush") as Brush
            : FindResource("TextSecondaryBrush") as Brush;
        HideOnDeactivateButton.ToolTip = _hideOnDeactivate
            ? "Disable hide when window loses focus"
            : "Hide when window loses focus";
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_hideOnDeactivate && !_pinWindow && !_isOpeningChildWindow && IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cleanup
        _hotkeyService?.Dispose();
        _clipboardMonitor?.Dispose();
        _screenshotService?.Dispose();
        _translationService?.Dispose();
        
        Log.Information("Main window closing");
    }

    private static void SetSelectedLanguage(ComboBox comboBox, string language, bool allowAuto)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (item.Tag is string tag && tag == language && (allowAuto || tag != "auto"))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void RegisterConfiguredHotkeys()
    {
        if (_hotkeyService == null || _storageService == null)
        {
            return;
        }

        _hotkeyService.UnregisterAll();
        _ocrHotkeyId = -1;
        _translateHotkeyId = -1;

        var ocrHotkey = _storageService.GetAsync<string>("ocr_hotkey").Result;
        if (string.IsNullOrWhiteSpace(ocrHotkey))
        {
            ocrHotkey = "Alt+Shift+S";
        }
        if (TryParseHotkey(ocrHotkey, out var ocrModifiers, out var ocrKey))
        {
            _ocrHotkeyId = _hotkeyService.RegisterHotkey(
                ocrModifiers,
                ocrKey,
                async () => await CaptureScreenshotAsync());
        }
        else
        {
            Log.Warning("Invalid OCR hotkey: {Hotkey}", ocrHotkey);
        }

        var translateHotkey = _storageService.GetAsync<string>("translate_hotkey").Result;
        if (string.IsNullOrWhiteSpace(translateHotkey))
        {
            translateHotkey = "Alt+Shift+T";
        }
        if (TryParseHotkey(translateHotkey, out var translateModifiers, out var translateKey))
        {
            _translateHotkeyId = _hotkeyService.RegisterHotkey(
                translateModifiers,
                translateKey,
                () => _ = TranslateSelectionAsync(translateKey));
        }
        else
        {
            Log.Warning("Invalid translate hotkey: {Hotkey}", translateHotkey);
        }
    }

    private static bool TryParseHotkey(string hotkey, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT":
                    modifiers |= HotkeyService.MOD_ALT;
                    continue;
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyService.MOD_CONTROL;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyService.MOD_SHIFT;
                    continue;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyService.MOD_WIN;
                    continue;
            }

            if (key != 0 || !TryParseVirtualKey(part, out key))
            {
                return false;
            }
        }

        return key != 0;
    }

    private static bool TryParseVirtualKey(string value, out uint key)
    {
        key = 0;
        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length == 1)
        {
            var ch = normalized[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = ch;
                return true;
            }
        }

        if (normalized.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(normalized[1..], out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            key = (uint)(0x70 + functionKey - 1);
            return true;
        }

        key = normalized switch
        {
            "ESC" or "ESCAPE" => HotkeyService.VK_ESCAPE,
            "PRINTSCREEN" or "PRTSC" or "PRTSCR" => HotkeyService.VK_SNAPSHOT,
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            _ => 0
        };

        return key != 0;
    }

    private async Task<string?> TryReadSelectedTextAsync(uint triggerKey = 0)
    {
        if (!await WaitForCopyHotkeyReleasedAsync(triggerKey))
        {
            return null;
        }

        var previousClipboardData = TryGetClipboardDataObject();
        var previousClipboardSequence = GetClipboardSequenceNumber();
        string? selectedText = null;

        try
        {
            SendEscape();
            await Task.Delay(35);
            SendCtrlC();

            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(35);
                if (GetClipboardSequenceNumber() == previousClipboardSequence)
                {
                    continue;
                }

                var clipboardText = _clipboardMonitor?.GetClipboardText();
                if (!string.IsNullOrWhiteSpace(clipboardText))
                {
                    selectedText = clipboardText;
                }

                break;
            }
        }
        finally
        {
            RestoreClipboardDataObject(previousClipboardData);
            await Task.Delay(80);
        }

        return selectedText;
    }

    private static IDataObject? TryGetClipboardDataObject()
    {
        try
        {
            return Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
    }

    private void RestoreClipboardDataObject(IDataObject? dataObject)
    {
        try
        {
            if (dataObject != null)
            {
                Clipboard.SetDataObject(dataObject, true);
            }
            else
            {
                _clipboardMonitor?.ClearClipboard();
            }
        }
        catch
        {
            _clipboardMonitor?.ClearClipboard();
        }
    }

    private static async Task<bool> WaitForCopyHotkeyReleasedAsync(uint triggerKey)
    {
        const int VK_SHIFT = 0x10;
        const int VK_CONTROL = 0x11;
        const int VK_MENU = 0x12;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        for (var i = 0; i < 20; i++)
        {
            if (!IsKeyDown(VK_SHIFT) &&
                !IsKeyDown(VK_CONTROL) &&
                !IsKeyDown(VK_MENU) &&
                !IsKeyDown(VK_LWIN) &&
                !IsKeyDown(VK_RWIN) &&
                (triggerKey == 0 || !IsKeyDown((int)triggerKey)))
            {
                await Task.Delay(60);
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static void SendCtrlC()
    {
        const byte VK_CONTROL = 0x11;
        const byte VK_C = 0x43;
        const uint KEYEVENTF_KEYUP = 0x0002;

        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, 0, UIntPtr.Zero);
        keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SendEscape()
    {
        const byte VK_ESCAPE = 0x1B;
        const uint KEYEVENTF_KEYUP = 0x0002;

        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}

public class TranslationDisplayItem
{
    public string ProviderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public Brush StatusBrush { get; set; } = Brushes.SeaGreen;
}
