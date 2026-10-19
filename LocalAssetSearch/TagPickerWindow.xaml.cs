using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 给一批素材打标签的窗口。取代原来那个只能手打标签名的输入框：
/// 已有标签按分类列出来直接勾选，也能顺手新建。
///
/// 勾选是三态的——给多张素材同时打标签时，只有一部分带某标签就显示半选。
/// </summary>
public partial class TagPickerWindow : Window
{
    private readonly Database _db;
    private readonly List<AssetItem> _targets;
    private readonly Dictionary<long, CheckBox> _tagBoxes = new();
    private readonly Dictionary<long, string> _tagNameById = new();
    private readonly List<(FrameworkElement Element, string Haystack)> _filterable = new();
    private Dictionary<long, int> _countsByTag = new();
    private bool _suppress;

    /// <summary>关窗后主窗口据此决定要不要刷新结果。</summary>
    public bool Changed { get; private set; }

    public TagPickerWindow(Database db, List<AssetItem> targets)
    {
        _db = db;
        _targets = targets;
        InitializeComponent();

        HeaderText.Text = targets.Count == 1
            ? $"给「{targets[0].Name}」添加或移除标签。勾选即生效，无需另外保存。"
            : $"已选中 {targets.Count} 个素材，勾选标签即可批量添加或移除。标签框为半选（■）表示只有一部分素材带该标签。";

        ReloadCounts();
        BuildTagList();
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
        TagPanel.Children.Clear(); _tagBoxes.Clear(); _tagNameById.Clear(); _filterable.Clear();
        var tags = _db.GetTags();
        if (tags.Count == 0)
        {
            TagPanel.Children.Add(new TextBlock { Text = "还没有任何标签。在下面输入名称即可新建。", Foreground = ThemeManager.MutedBrush, TextWrapping = TextWrapping.Wrap });
            return;
        }

        string currentCategory = "";
        foreach (var tag in tags)
        {
            if (!string.Equals(tag.CategoryLabel, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                currentCategory = tag.CategoryLabel;
                var header = new TextBlock
                {
                    Text = currentCategory,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeManager.MutedBrush,
                    Margin = new Thickness(2, 10, 2, 2)
                };
                TagPanel.Children.Add(header);
                _filterable.Add((header, currentCategory));
            }

            var cb = new CheckBox { Content = tag.Name, Tag = tag.Id, IsThreeState = true, Margin = new Thickness(2, 4, 2, 4) };
            cb.Click += TagToggle_Click;
            TagPanel.Children.Add(cb);
            _tagBoxes[tag.Id] = cb;
            _tagNameById[tag.Id] = tag.Name;
            _filterable.Add((cb, currentCategory + " " + tag.Name));
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
            var category = NewTagCategory.Text.Trim();
            _db.EnsureTag(name, category.Length == 0 ? null : category);
            foreach (var target in _targets) _db.ApplyTag(target.Id, name);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "新建标签失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        NewTagName.Clear();
        Changed = true;
        ReloadCounts();
        BuildTagList();
        StatusText.Text = $"已把「{name}」加到 {_targets.Count} 个素材。";
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        foreach (var (element, haystack) in _filterable)
            element.Visibility = q.Length == 0 || haystack.Contains(q, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
