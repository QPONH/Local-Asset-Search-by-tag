using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 标签管理：改名、选分类、删除、新建，以及单独新增分类。
///
/// 所有改动先留在窗口里，右下角「保存」时一次性写入；关窗前有未保存修改会确认。
/// 分类现在有独立的 Categories 表，可以先建分类、暂时不挂标签。
/// </summary>
public partial class TagManagerWindow : Window
{
    private readonly Database _db;
    private readonly List<string> _categories = new();
    private readonly List<TagRow> _rows = new();
    private bool _building;
    private bool _dirty;
    private bool _saved;

    /// <summary>关窗后主窗口据此决定要不要刷新标签和结果。</summary>
    public bool Changed { get; private set; }

    private sealed class TagRow
    {
        public TagInfo? Original;
        public TextBox NameBox = null!;
        public ComboBox CategoryBox = null!;
        public Grid Container = null!;
        public Button DeleteButton = null!;
        public bool Deleted;
    }

    public TagManagerWindow(Database db)
    {
        _db = db;
        InitializeComponent();
        ReloadCategories();
        BuildRows();
        Closing += OnClosing;
    }

    private void ReloadCategories()
    {
        _categories.Clear();
        _categories.AddRange(_db.GetCategories());
        RefreshCategoryChoices();
    }

    private List<string> CategoryChoices()
    {
        var choices = new List<string> { TagCategory.Unclassified };
        choices.AddRange(_categories);
        return choices;
    }

    /// <summary>分类列表变化后，刷新「新增标签」和每一行的分类下拉框，保留当前输入。</summary>
    private void RefreshCategoryChoices()
    {
        _building = true;
        try
        {
            var choices = CategoryChoices();
            var newTagCurrent = NewTagCategory.SelectedItem as string;
            NewTagCategory.ItemsSource = choices;
            NewTagCategory.SelectedItem = TagCategory.Choice(choices, newTagCurrent);

            foreach (var row in _rows)
            {
                var current = row.CategoryBox.SelectedItem as string;
                row.CategoryBox.ItemsSource = new List<string>(choices);
                row.CategoryBox.SelectedItem = TagCategory.Choice(choices, current);
            }
        }
        finally { _building = false; }
    }

    private void BuildRows()
    {
        _building = true;
        try
        {
            _rows.Clear();
            RowPanel.Children.Clear();
            foreach (var tag in _db.GetTags()) AddRow(tag);
            if (_rows.Count == 0)
            {
                RowPanel.Children.Add(new TextBlock
                {
                    Text = "还没有任何标签。在上面输入名称后点「加入列表」，再点「保存」。",
                    Foreground = ThemeManager.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        finally { _building = false; }
        StatusText.Text = "改动先留在窗口里，点右下角「保存」统一写入。";
    }

    private void AddRow(TagInfo? tag, string? draftName = null, string? draftCategory = null)
    {
        var row = new TagRow { Original = tag };

        var grid = new Grid { Margin = new Thickness(2, 3, 2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        row.NameBox = new TextBox
        {
            Text = tag?.Name ?? draftName ?? "",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var initialCategory = tag is null
            ? (string.IsNullOrWhiteSpace(draftCategory) ? TagCategory.Unclassified : draftCategory.Trim())
            : (string.IsNullOrWhiteSpace(tag.Category) ? TagCategory.Unclassified : tag.Category);
        var choices = CategoryChoices();
        row.CategoryBox = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = TagCategory.Choice(choices, initialCategory),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "选择分类；新分类先在顶部「新增分类」里加",
        };

        var usage = new TextBlock
        {
            Text = tag?.UsageCount.ToString("N0") ?? "新",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeManager.MutedBrush,
            Margin = new Thickness(2, 0, 0, 0),
        };

        row.DeleteButton = new Button
        {
            Content = "删除",
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0),
        };
        row.DeleteButton.Click += (_, _) => ToggleDelete(row);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(row.DeleteButton);

        Grid.SetColumn(row.NameBox, 0);
        Grid.SetColumn(row.CategoryBox, 1);
        Grid.SetColumn(usage, 2);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(row.NameBox);
        grid.Children.Add(row.CategoryBox);
        grid.Children.Add(usage);
        grid.Children.Add(actions);

        row.Container = grid;
        _rows.Add(row);
        RowPanel.Children.Add(grid);

        // 事件在初始值设置完之后挂，避免建行本身把窗口标成“有修改”。
        row.NameBox.TextChanged += (_, _) => MarkDirty();
        row.CategoryBox.SelectionChanged += (_, _) => MarkDirty();
        // 可编辑 ComboBox 里直接打字时，内部 TextBox 的 TextChanged 会冒泡到这里。
        row.CategoryBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => MarkDirty()));
    }

    private void MarkDirty()
    {
        if (_building || _saved) return;
        _dirty = true;
    }

    private void ToggleDelete(TagRow row)
    {
        if (row.Original is null)
        {
            // 还没保存的新标签，直接从列表里拿掉即可。
            _rows.Remove(row);
            RowPanel.Children.Remove(row.Container);
            MarkDirty();
            StatusText.Text = "已从列表移除这个新标签（尚未写入）。";
            return;
        }

        row.Deleted = !row.Deleted;
        row.Container.Opacity = row.Deleted ? 0.45 : 1.0;
        row.DeleteButton.Content = row.Deleted ? "撤销" : "删除";
        MarkDirty();
        StatusText.Text = row.Deleted
            ? $"已标记删除「{row.NameBox.Text.Trim()}」，点「保存」才真正删除。"
            : $"已撤销删除「{row.NameBox.Text.Trim()}」。";
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagName.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请先输入标签名称。"; return; }
        if (_rows.Any(r => !r.Deleted && string.Equals(r.NameBox.Text.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"列表里已经有「{name}」了。";
            return;
        }

        var category = TagCategory.Normalize(NewTagCategory.SelectedItem as string);
        AddRow(null, name, category);
        NewTagName.Clear();
        MarkDirty();
        StatusText.Text = $"已把「{name}」加入列表，点「保存」后写入。";
    }

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = NewCategoryName.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请先输入分类名称。"; return; }
        if (string.Equals(name, TagCategory.Unclassified, StringComparison.Ordinal)) { StatusText.Text = "「未分类」是保留名称。"; return; }
        if (_categories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"分类「{name}」已经存在。";
            return;
        }

        _categories.Add(name);
        _categories.Sort(StringComparer.CurrentCultureIgnoreCase);
        RefreshCategoryChoices();
        MarkDirty();
        NewCategoryName.Clear();
        StatusText.Text = $"已新增分类「{name}」到列表，点「保存」后生效。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var active = _rows.Where(r => !r.Deleted).ToList();
        var names = new List<string>();
        foreach (var row in active)
        {
            var name = row.NameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("标签名不能为空。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"标签名「{name}」重复了。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            names.Add(name);
        }

        var toDelete = _rows.Where(r => r.Deleted && r.Original is not null).ToList();
        if (toDelete.Count > 0)
        {
            var used = toDelete.Count(r => r.Original!.UsageCount > 0);
            var msg = used > 0
                ? $"有 {toDelete.Count} 个标签要删除，其中 {used} 个正被文件夹使用。\n\n删除后会解除这些关联，磁盘上的原始目录不受影响。继续？"
                : $"删除 {toDelete.Count} 个标签？\n\n磁盘上的原始目录不受影响。";
            if (MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        try
        {
            foreach (var category in _categories) _db.EnsureCategory(category);

            // 先删，腾出名字后再改名 / 新建。
            foreach (var row in toDelete) _db.DeleteTag(row.Original!.Id);

            foreach (var row in active)
            {
                var name = row.NameBox.Text.Trim();
                var category = TagCategory.Normalize(row.CategoryBox.SelectedItem as string);
                if (category.Length > 0) _db.EnsureCategory(category);

                if (row.Original is null)
                {
                    _db.EnsureTag(name, category.Length == 0 ? null : category);
                }
                else
                {
                    if (!string.Equals(name, row.Original.Name, StringComparison.Ordinal)) _db.RenameTag(row.Original.Id, name);
                    if (!string.Equals(category, row.Original.Category, StringComparison.Ordinal)) _db.SetTagCategory(row.Original.Id, category);
                }
            }

            Changed = _dirty;
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saved || !_dirty) return;
        if (MessageBox.Show("有未保存的修改，确定关闭？", "放弃修改", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
}
