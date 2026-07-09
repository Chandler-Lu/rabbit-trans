using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using RabTrans.Core.OCR;
using RabTrans.Core.Storage;
using RabTrans.Core.Translation;
using Serilog;

namespace RabTrans;

/// <summary>
/// Settings window for RabTrans.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly StorageService _storageService;
    private readonly TranslationService _translationService;
    private readonly OcrService _ocrService;
    private readonly ObservableCollection<ProviderSelectionItem> _providerItems = new();
    private readonly ObservableCollection<ProviderSelectionItem> _ocrProviderItems = new();
    private TextBox? _recordingHotkeyBox;
    private Point? _providerDragStartPoint;
    private ListBoxItem? _providerDropContainer;
    private bool _providerDropAfter;
    private bool _hotkeysSuspendedForRecording;
    private bool _settingsLoaded;
    private string _loadedSettingsSnapshot = string.Empty;
    private const string AutoStartValueName = "RabTrans";
    private const string AutoStartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string ConfigDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RabTrans");

    public SettingsWindow()
    {
        InitializeComponent();
        
        _storageService = App.GetService<StorageService>();
        _translationService = App.GetService<TranslationService>();
        _ocrService = App.GetService<OcrService>();

        VersionTextBlock.Text = $"Version {GetAppVersion()}";
        PopulateInterfaceLists();
        LoadSettings();
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        Closed += SettingsWindow_Closed;
    }

    private void PopulateInterfaceLists()
    {
        _providerItems.Clear();
        foreach (var provider in _translationService.GetProviders())
        {
            _providerItems.Add(new ProviderSelectionItem
            {
                Id = provider,
                DisplayName = provider
            });
        }

        ProviderListBox.ItemsSource = _providerItems;

        _ocrProviderItems.Clear();
        foreach (var provider in _ocrService.GetProviders())
        {
            _ocrProviderItems.Add(new ProviderSelectionItem
            {
                Id = provider,
                DisplayName = provider
            });
        }

        OcrProviderListBox.ItemsSource = _ocrProviderItems;
    }

    private async void LoadSettings()
    {
        try
        {
            // Load general settings
            AutoStartCheckBox.IsChecked = IsAutoStartEnabled() || (await _storageService.GetAsync<bool?>("auto_start") ?? false);
            MinimizeToTrayCheckBox.IsChecked = await _storageService.GetAsync<bool?>("minimize_to_tray") ?? true;
            NodePathBox.Text = await _storageService.GetAsync<string>("plugin_node_path") ?? "";

            // Load translation settings
            var providers = await _storageService.GetAsync<List<string>>("enabled_translation_providers") ?? new List<string>();
            if (providers.Count == 0 && _providerItems.Count > 0)
            {
                providers.Add(_providerItems[0].Id);
            }

            ReorderProviders(_providerItems, providers);

            foreach (var providerItem in _providerItems)
            {
                providerItem.IsSelected = providers.Contains(providerItem.Id);
            }

            var ocrProviders = await _storageService.GetAsync<List<string>>("enabled_ocr_providers") ?? new List<string>();
            if (ocrProviders.Count == 0 && _ocrProviderItems.Count > 0)
            {
                ocrProviders.Add(_ocrProviderItems[0].Id);
            }

            ReorderProviders(_ocrProviderItems, ocrProviders);

            foreach (var providerItem in _ocrProviderItems)
            {
                providerItem.IsSelected = ocrProviders.Contains(providerItem.Id);
            }

            var sourceLang = await _storageService.GetAsync<string>("default_source_lang") ?? "auto";
            SelectComboBoxItemByTag(DefaultSourceLangCombo, sourceLang);

            var targetLang = await _storageService.GetAsync<string>("default_target_lang") ?? "zh-CN";
            SelectComboBoxItemByTag(DefaultTargetLangCombo, targetLang);

            OcrHotkeyBox.Text = await _storageService.GetAsync<string>("ocr_hotkey") ?? "Alt+Shift+S";
            TranslateHotkeyBox.Text = await _storageService.GetAsync<string>("translate_hotkey") ?? "Alt+Shift+T";

            _loadedSettingsSnapshot = CreateSettingsSnapshot();
            _settingsLoaded = true;
            Log.Information("Settings loaded");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings");
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Save general settings
            var autoStart = AutoStartCheckBox.IsChecked ?? false;
            await _storageService.SetAsync("auto_start", autoStart);
            await _storageService.SetAsync("minimize_to_tray", MinimizeToTrayCheckBox.IsChecked ?? true);
            await _storageService.SetAsync("plugin_node_path", NodePathBox.Text.Trim());
            SetAutoStart(autoStart);

            // Save translation settings
            var selectedProviders = _providerItems.Where(item => item.IsSelected).Select(item => item.Id).ToList();
            if (selectedProviders.Count == 0 && _providerItems.Count > 0)
            {
                selectedProviders.Add(_providerItems[0].Id);
            }

            await _storageService.SetAsync("enabled_translation_providers", selectedProviders);

            var selectedOcrProviders = _ocrProviderItems.Where(item => item.IsSelected).Select(item => item.Id).ToList();
            if (selectedOcrProviders.Count == 0 && _ocrProviderItems.Count > 0)
            {
                selectedOcrProviders.Add(_ocrProviderItems[0].Id);
            }

            await _storageService.SetAsync("enabled_ocr_providers", selectedOcrProviders);
            await _storageService.SetAsync("default_source_lang", GetSelectedComboBoxTag(DefaultSourceLangCombo));
            await _storageService.SetAsync("default_target_lang", GetSelectedComboBoxTag(DefaultTargetLangCombo));
            await _storageService.SetAsync("ocr_hotkey", OcrHotkeyBox.Text.Trim());
            await _storageService.SetAsync("translate_hotkey", TranslateHotkeyBox.Text.Trim());

            _loadedSettingsSnapshot = CreateSettingsSnapshot();
            Log.Information("Settings saved");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        DialogResult = false;
        Close();
    }

    private void RecordOcrHotkey_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyRecording(OcrHotkeyBox);
    }

    private void RecordTranslateHotkey_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyRecording(TranslateHotkeyBox);
    }

    private void StartHotkeyRecording(TextBox targetBox)
    {
        SuspendOwnerHotkeysForRecording();
        _recordingHotkeyBox = targetBox;
        targetBox.Text = "Press keys...";
        targetBox.Focus();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingHotkeyBox == null)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (ConfirmDiscardUnsavedChanges())
                {
                    DialogResult = false;
                    Close();
                }
            }

            return;
        }

        e.Handled = true;
        var hotkey = FormatHotkey(e);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        _recordingHotkeyBox.Text = hotkey;
        _recordingHotkeyBox = null;
    }

    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!_settingsLoaded || !HasUnsavedChanges())
        {
            return true;
        }

        var result = MessageBox.Show(
            "You have unsaved changes. Close settings without saving?",
            "Unsaved changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private bool HasUnsavedChanges() => !string.Equals(
        _loadedSettingsSnapshot,
        CreateSettingsSnapshot(),
        StringComparison.Ordinal);

    private string CreateSettingsSnapshot()
    {
        return string.Join("\u001F", new[]
        {
            (AutoStartCheckBox.IsChecked ?? false).ToString(),
            (MinimizeToTrayCheckBox.IsChecked ?? true).ToString(),
            NodePathBox.Text.Trim(),
            string.Join(",", _providerItems.Select(item => $"{item.Id}:{item.IsSelected}")),
            string.Join(",", _ocrProviderItems.Select(item => $"{item.Id}:{item.IsSelected}")),
            GetSelectedComboBoxTag(DefaultSourceLangCombo),
            GetSelectedComboBoxTag(DefaultTargetLangCombo),
            OcrHotkeyBox.Text.Trim(),
            TranslateHotkeyBox.Text.Trim()
        });
    }

    private static string GetAppVersion()
    {
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(App).Assembly.GetName().Version?.ToString();
        }

        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        _recordingHotkeyBox = null;
        ResumeOwnerHotkeysAfterRecording();
    }

    private void SuspendOwnerHotkeysForRecording()
    {
        if (_hotkeysSuspendedForRecording)
        {
            return;
        }

        if (Owner is MainWindow mainWindow)
        {
            mainWindow.SuspendConfiguredHotkeys();
            _hotkeysSuspendedForRecording = true;
        }
    }

    private void ResumeOwnerHotkeysAfterRecording()
    {
        if (!_hotkeysSuspendedForRecording)
        {
            return;
        }

        if (Owner is MainWindow mainWindow)
        {
            mainWindow.ResumeConfiguredHotkeys();
        }

        _hotkeysSuspendedForRecording = false;
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

    private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open config folder");
            MessageBox.Show($"Failed to open config folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _translationService.ReloadProviders();
            _ocrService.ReloadPlugins();

            if (Owner is MainWindow mainWindow)
            {
                mainWindow.ReloadConfiguration();
            }

            PopulateInterfaceLists();
            LoadSettings();

            MessageBox.Show("Configuration reloaded.", "RabTrans", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reload configuration");
            MessageBox.Show($"Failed to reload configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProviderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
        {
            _providerDragStartPoint = null;
            return;
        }

        if (sender is ListBox listBox)
        {
            _providerDragStartPoint = e.GetPosition(listBox);
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
    }

    private void ProviderListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ProviderSelectionItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;

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

    private void ProviderListBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ProviderSelectionItem)))
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
    }

    private void SetProviderDropIndicator(ListBoxItem targetContainer, bool after)
    {
        if (!ReferenceEquals(_providerDropContainer, targetContainer))
        {
            ClearProviderDropIndicator();
        }

        _providerDropContainer = targetContainer;
        _providerDropAfter = after;
        targetContainer.BorderBrush = FindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
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

    private void ReorderProviders(ObservableCollection<ProviderSelectionItem> items, IReadOnlyList<string> orderedProviderIds)
    {
        var orderedItems = orderedProviderIds
            .Select(id => items.FirstOrDefault(item => item.Id == id))
            .Where(item => item != null)
            .Cast<ProviderSelectionItem>()
            .Concat(items.Where(item => !orderedProviderIds.Contains(item.Id)))
            .ToList();

        items.Clear();
        foreach (var item in orderedItems)
        {
            items.Add(item);
        }
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
