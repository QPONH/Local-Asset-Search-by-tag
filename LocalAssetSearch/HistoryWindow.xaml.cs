using System.Windows;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

public sealed class HistoryRow
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string TimeText { get; init; } = "";
}

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        WindowSizeStore.Restore(this, "history");
        WindowSizeStore.Track(this, "history");
        ViewerHistory.Changed += OnHistoryChanged;
        Closed += (_, _) => ViewerHistory.Changed -= OnHistoryChanged;
        Reload();
    }

    private void OnHistoryChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Reload));
            return;
        }
        Reload();
    }

    private void Reload()
    {
        var opened = ViewerHistory.Opened.Select(ToRow).ToList();
        var closed = ViewerHistory.Closed.Select(ToRow).ToList();
        OpenList.ItemsSource = opened;
        CloseList.ItemsSource = closed;
        OpenTitle.Text = $"最近打开（{opened.Count}）";
        CloseTitle.Text = $"最近关闭（{closed.Count}）";
        OpenEmpty.Visibility = opened.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseEmpty.Visibility = closed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HistoryRow ToRow(ViewerHistoryEntry entry) => new()
    {
        Name = entry.Name,
        Path = entry.Path,
        TimeText = entry.Time.ToString("yyyy-MM-dd HH:mm:ss"),
    };

    private void ClearOpen_Click(object sender, RoutedEventArgs e) => ViewerHistory.ClearOpened();
    private void ClearClose_Click(object sender, RoutedEventArgs e) => ViewerHistory.ClearClosed();
}
