using System.Windows;
using System.Windows.Controls;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 调整标签组（分类）的显示顺序：↑ ↓ 交换位置，立即写入 Categories.SortOrder。
/// 所有标签界面都按 <see cref="Database.GetTags"/> 的顺序显示，所以改完立刻生效。
/// </summary>
public partial class CategoryOrderWindow : Window
{
    private readonly Database _db;
    private readonly List<string> _order = new();

    public CategoryOrderWindow(Database db)
    {
        _db = db;
        InitializeComponent();
        WindowSizeStore.Restore(this, "categoryOrder");
        WindowSizeStore.Track(this, "categoryOrder");
        _order.AddRange(_db.GetCategories());
        Build();
        StatusText.Text = _order.Count <= 1 ? "只有一个分类，不需要调整。" : "点 ↑ ↓ 调整顺序。";
    }

    private void Build()
    {
        RowPanel.Children.Clear();
        for (var i = 0; i < _order.Count; i++)
        {
            var index = i;
            var name = _order[i];

            var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var up = new Button
            {
                Content = "↑",
                Width = 34,
                Padding = new Thickness(0, 2, 0, 2),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = index > 0,
                ToolTip = "上移",
            };
            var down = new Button
            {
                Content = "↓",
                Width = 34,
                Padding = new Thickness(0, 2, 0, 2),
                IsEnabled = index < _order.Count - 1,
                ToolTip = "下移",
            };
            up.Click += (_, _) => Move(index, -1);
            down.Click += (_, _) => Move(index, 1);
            actions.Children.Add(up);
            actions.Children.Add(down);

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            row.Children.Add(actions);
            row.Children.Add(label);
            RowPanel.Children.Add(row);
        }
    }

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _order.Count) return;
        (_order[index], _order[target]) = (_order[target], _order[index]);
        _db.SetCategoryOrder(_order);
        Build();
        StatusText.Text = "顺序已保存。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
