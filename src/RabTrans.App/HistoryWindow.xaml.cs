using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RabTrans.Core.Storage;

namespace RabTrans;

public partial class HistoryWindow : Window
{
    private readonly StorageService _storageService;
    private readonly List<HistoryDisplayItem> _allHistoryItems = new();
    private readonly ObservableCollection<HistoryDisplayItem> _historyItems = new();

    public HistoryWindow()
    {
        InitializeComponent();
        _storageService = App.GetService<StorageService>();
        HistoryListBox.ItemsSource = _historyItems;
        Loaded += async (_, _) => await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        _allHistoryItems.Clear();
        foreach (var item in await _storageService.GetHistoryAsync(200))
        {
            _allHistoryItems.Add(new HistoryDisplayItem
            {
                SourceText = item.SourceText,
                TranslatedText = item.TranslatedText,
                MetaText = $"{item.Provider}  {item.SourceLang} -> {item.TargetLang}  {item.Timestamp:g}"
            });
        }

        ApplySearchFilter();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadHistoryAsync();
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        await _storageService.ClearHistoryAsync();
        await LoadHistoryAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        var query = SearchBox.Text.Trim();
        var filteredItems = string.IsNullOrWhiteSpace(query)
            ? _allHistoryItems
            : _allHistoryItems
                .Where(item => ContainsIgnoreCase(item.SourceText, query) ||
                               ContainsIgnoreCase(item.TranslatedText, query) ||
                               ContainsIgnoreCase(item.MetaText, query));

        _historyItems.Clear();
        foreach (var item in filteredItems)
        {
            _historyItems.Add(item);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CopyHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryDisplayItem item })
        {
            CopyHistoryItem(item);
        }
    }

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is HistoryDisplayItem item)
        {
            CopyHistoryItem(item);
        }
    }

    private static void CopyHistoryItem(HistoryDisplayItem item)
    {
        Clipboard.SetText(item.CopyText);
    }

    private static bool ContainsIgnoreCase(string text, string query)
    {
        return text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}

public class HistoryDisplayItem
{
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public string MetaText { get; set; } = string.Empty;
    public string CopyText => $"{SourceText}{Environment.NewLine}{Environment.NewLine}{TranslatedText}{Environment.NewLine}{Environment.NewLine}{MetaText}";
}
