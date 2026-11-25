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

    // 自动应用：改动停下 600ms 后自动写库，不用点「保存」。开关存在 settings.ini，全局生效。
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer = new();
    private bool _autoApply;

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
        WindowSizeStore.Restore(this, "tagManager");
        WindowSizeStore.Track(this, "tagManager");
        _autoApply = Settings.Get("tags.autoApply", "1") == "1";
        ReloadCategories();
        BuildRows();

        _building = true;
        AutoApplyCheck.IsChecked = _autoApply;
        _building = false;
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            ApplyChanges(closeAfter: false, interactive: false);
        };

        Closing += OnClosing;
    }

    private void AutoApply_Changed(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        _autoApply = AutoApplyCheck.IsChecked == true;
        Settings.Set("tags.autoApply", _autoApply ? "1" : "0");
        if (_autoApply && _dirty)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
        else
        {
            _autoSaveTimer.Stop();
        }
        StatusText.Text = _autoApply
            ? "已开启自动应用：改动停顿片刻后自动写入。"
            : "已关闭自动应用：点「保存」或关窗时写入。";
    }

    private void CategoryOrder_Click(object sender, RoutedEventArgs e)
    {
        var w = new CategoryOrderWindow(_db) { Owner = this };
        w.ShowDialog();
        ReloadCategories();
        RefreshCategoryChoices();
        StatusText.Text = "分类顺序已更新。";
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
        StatusText.Text = _autoApply
            ? "修改会自动写入；点「保存」立即写入并关闭。"
            : "改动先留在窗口里，点右下角「保存」统一写入。";
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
        if (!_autoApply) return;
        // 连续输入时只保留最后一次，停顿 600ms 再写库。
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
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

    private void Save_Click(object sender, RoutedEventArgs e) => ApplyChanges(closeAfter: true, interactive: true);

    /// <summary>
    /// 把窗口里的改动写进数据库。interactive 时校验失败弹对话框；
    /// 自动应用（定时器 / 关窗刷新）时只在状态栏提示，不打断输入。
    /// </summary>
    private bool ApplyChanges(bool closeAfter, bool interactive)
    {
        var active = _rows.Where(r => !r.Deleted).ToList();
        var names = new List<string>();
        foreach (var row in active)
        {
            var name = row.NameBox.Text.Trim();
            if (name.Length == 0)
            {
                if (interactive) MessageBox.Show("标签名不能为空。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                else StatusText.Text = "有标签名为空，自动保存暂停；补齐后会自动重试。";
                return false;
            }
            if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (interactive) MessageBox.Show($"标签名「{name}」重复了。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                else StatusText.Text = $"标签名「{name}」重复，自动保存暂停；改好后会自动重试。";
                return false;
            }
            names.Add(name);
        }

        var toDelete = _rows.Where(r => r.Deleted && r.Original is not null).ToList();
        if (interactive && toDelete.Count > 0)
        {
            var used = toDelete.Count(r => r.Original!.UsageCount > 0);
            var msg = used > 0
                ? $"有 {toDelete.Count} 个标签要删除，其中 {used} 个正被文件夹使用。\n\n删除后会解除这些关联，磁盘上的原始目录不受影响。继续？"
                : $"删除 {toDelete.Count} 个标签？\n\n磁盘上的原始目录不受影响。";
            if (MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
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
                    var id = _db.EnsureTag(name, category.Length == 0 ? null : category);
                    row.Original = new TagInfo { Id = id, Name = name, Category = category };
                }
                else
                {
                    if (!string.Equals(name, row.Original.Name, StringComparison.Ordinal))
                    {
                        _db.RenameTag(row.Original.Id, name);
                        row.Original.Name = name;
                    }
                    if (!string.Equals(category, row.Original.Category, StringComparison.Ordinal))
                    {
                        _db.SetTagCategory(row.Original.Id, category);
                        row.Original.Category = category;
                    }
                }
            }

            // 已经落库的删除行从列表里拿掉，避免下一次保存重复删。
            foreach (var row in toDelete)
            {
                _rows.Remove(row);
                RowPanel.Children.Remove(row.Container);
            }

            _dirty = false;
            Changed = true;
            _autoSaveTimer.Stop();

            if (closeAfter)
            {
                _saved = true;
                Close();
                return true;
            }

            StatusText.Text = $"已自动写入（{DateTime.Now:HH:mm:ss}）。";
            return true;
        }
        catch (Exception ex)
        {
            if (interactive) MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            else StatusText.Text = $"自动保存失败：{ex.Message}";
            return false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saved) return;
        _autoSaveTimer.Stop();
        if (_autoApply)
        {
            // 关窗前把还没到点的改动落库；真的写不进去才询问，避免悄悄丢修改。
            if (!_dirty) return;
            if (!ApplyChanges(closeAfter: false, interactive: false))
            {
                if (MessageBox.Show("有修改没能自动写入，仍要关闭并放弃吗？", "自动保存失败",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    e.Cancel = true;
            }
            return;
        }
        if (!_dirty) return;
        if (MessageBox.Show("有未保存的修改，确定关闭？", "放弃修改", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            e.Cancel = true;
    }
}
