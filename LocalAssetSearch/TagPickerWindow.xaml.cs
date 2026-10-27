using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 给一批素材打标签的窗口。已有标签按分类分组、横向排列，直接勾选；也能顺手新建。
///
/// 勾选是三态的——给多张素材同时打标签时，只有一部分带某标签就显示半选。
/// </summary>
public partial class TagPickerWindow : Window
{
    private readonly Database _db;
    private readonly List<AssetItem> _targets;
    private readonly Dictionary<long, CheckBox> _tagBoxes = new();
    private readonly Dictionary<long, string> _tagNameById = new();
    private readonly List<TagGroup> _groups = new();
    private Dictionary<long, int> _countsByTag = new();
    private bool _suppress;

    /// <summary>关窗后主窗口据此决定要不要刷新结果。</summary>
    public bool Changed { get; private set; }

    private sealed class TagGroup
    {
        public Grid Header = null!;
        public TextBlock HeaderText = null!;
        public WrapPanel Panel = null!;
        public List<(CheckBox Box, string Name)> Items { get; } = new();
    }

    public TagPickerWindow(Database db, List<AssetItem> targets)
    {
        _db = db;
        _targets = targets;
        InitializeComponent();
        WindowSizeStore.Restore(this, "tagPicker");
        WindowSizeStore.Track(this, "tagPicker");

        HeaderText.Text = targets.Count == 1
            ? $"给「{targets[0].Name}」添加或移除标签。勾选即生效，无需另外保存。"
            : $"已选中 {targets.Count} 个素材，勾选标签即可批量添加或移除。标签框为半选（■）表示只有一部分素材带该标签。";

        LoadCategories();
        ReloadCounts();
        BuildTagList();
    }

    private void LoadCategories()
    {
        var choices = new List<string> { TagCategory.Unclassified };
        choices.AddRange(_db.GetCategories());
        var current = NewTagCategory.SelectedItem as string;
        NewTagCategory.ItemsSource = choices;
        NewTagCategory.SelectedItem = TagCategory.Choice(choices, current);
    }

    /// <summary>统计每个标签在选中素材里各被多少个用到。</summary>
    private void ReloadCounts()
    {
        _countsByTag = new Dictionary<long, int>();
        var tagIdByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _db.GetTags()) tagIdByName[t.Name] = t.Id;

        var perAsset = _db.GetAllAssetTags();
        foreach (var target in _targets)
        {
            if (!perAsset.TryGetValue(target.Id, out var names)) continue;
            foreach (var name in names)
                if (tagIdByName.TryGetValue(name, out var id))
                    _countsByTag[id] = _countsByTag.TryGetValue(id, out var n) ? n + 1 : 1;
        }
    }

    private void BuildTagList()
    {
        TagPanel.Children.Clear(); _tagBoxes.Clear(); _tagNameById.Clear(); _groups.Clear();
        var tags = _db.GetTags();
        if (tags.Count == 0)
        {
            TagPanel.Children.Add(new TextBlock { Text = "还没有任何标签。在下面输入名称即可新建。", Foreground = ThemeManager.MutedBrush, TextWrapping = TextWrapping.Wrap });
            return;
        }

        string currentCategory = "";
        TagGroup? group = null;
        foreach (var tag in tags)
        {
            if (!string.Equals(tag.CategoryLabel, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                currentCategory = tag.CategoryLabel;
                var header = TagHeader.Create(currentCategory);
                TagPanel.Children.Add(header.Root);

                // 一般标签最多四个字，排得密一点：窗口宽度下能放六列。
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 110 };
                TagPanel.Children.Add(wrap);
                group = new TagGroup { Header = header.Root, HeaderText = header.Text, Panel = wrap };
                _groups.Add(group);
            }

            var label = new TextBlock
            {
                Text = tag.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = tag.Name,
                MaxWidth = 86,
            };
            var cb = new CheckBox
            {
                Content = label,
                Tag = tag.Id,
                IsThreeState = true,
                Margin = new Thickness(2, 2, 2, 2),
                Width = 106,
                ToolTip = tag.Name,
            };
            cb.Click += TagToggle_Click;
            group!.Panel.Children.Add(cb);
            group.Items.Add((cb, tag.Name));
            _tagBoxes[tag.Id] = cb;
            _tagNameById[tag.Id] = tag.Name;
        }
        RefreshStates();
    }

    private void RefreshStates()
    {
        _suppress = true;
        try
        {
            foreach (var (tagId, cb) in _tagBoxes)
            {
                int have = _countsByTag.TryGetValue(tagId, out var n) ? n : 0;
                cb.IsChecked = have == 0 ? false : have == _targets.Count ? true : null;
            }
        }
        finally { _suppress = false; }
    }

    private void TagToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not CheckBox cb || cb.Tag is not long tagId) return;

        var name = _tagNameById[tagId];
        int have = _countsByTag.TryGetValue(tagId, out var n) ? n : 0;
        // 全都带这个标签 → 这次点击理解为整体移除；否则一律添加。
        bool add = have < _targets.Count;
        foreach (var target in _targets) _db.ApplyTag(target.Id, name, remove: !add);

        Changed = true;
        ReloadCounts();
        RefreshStates();
        StatusText.Text = add
            ? $"已给 {_targets.Count} 个素材加上「{name}」。"
            : $"已从 {_targets.Count} 个素材移除「{name}」。";
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagName.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请先输入标签名称。"; return; }
        try
        {
            var category = TagCategory.Normalize(NewTagCategory.SelectedItem as string);
            _db.EnsureTag(name, category.Length == 0 ? null : category);
            foreach (var target in _targets) _db.ApplyTag(target.Id, name);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "新建标签失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        NewTagName.Clear();
        Changed = true;
        LoadCategories();
        ReloadCounts();
        BuildTagList();
        StatusText.Text = $"已把「{name}」加到 {_targets.Count} 个素材。";
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        foreach (var group in _groups)
        {
            var groupMatch = q.Length == 0 || group.HeaderText.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
            var visible = 0;
            foreach (var (box, name) in group.Items)
            {
                var show = q.Length == 0 || groupMatch || name.Contains(q, StringComparison.OrdinalIgnoreCase);
                box.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) visible++;
            }
            var showGroup = q.Length == 0 || visible > 0;
            group.Header.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
            group.Panel.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
