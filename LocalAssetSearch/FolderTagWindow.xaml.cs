using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 文件夹标签管理窗口。用来替换旧版那个只能单选、只能添加的模态框。
///
/// 交互模型：
///   - 左侧文件夹用复选框多选，不用 Ctrl / Shift；
///   - 每个文件夹下方直接列出它已有的标签；
///   - 右侧标签是三态复选框，反映「所选文件夹」的整体状态；
///   - 点标签 = 对所选的全部文件夹做一次批量添加或移除，改完立刻写库。
/// </summary>
public partial class FolderTagWindow : Window
{
    private readonly Database _db;
    private readonly List<AssetItem> _folders;
    private readonly List<CheckBox> _folderBoxes = new();
    private readonly Dictionary<long, CheckBox> _tagBoxes = new();
    private readonly Dictionary<long, string> _tagNameById = new();
    private readonly Dictionary<string, long> _tagIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, TextBlock> _folderTagTexts = new();
    private Dictionary<long, List<string>> _folderTags = new();
    private readonly Dictionary<long, HashSet<long>> _folderTagIds = new();
    private bool _suppress;

    /// <summary>窗口关闭后，主窗口据此决定要不要重新载入标签并刷新结果。</summary>
    public bool Changed { get; private set; }

    public FolderTagWindow(Database db, List<AssetItem> folders)
    {
        _db = db;
        _folders = folders;
        InitializeComponent();
        LoadCategories();
        BuildFolderList();
        ReloadTags();
        ReloadFolderTags();
        RefreshTagStates();
    }

    private void LoadCategories()
    {
        var choices = new List<string> { "未分类" };
        choices.AddRange(_db.GetCategories());
        var current = NewTagCategory.SelectedItem as string;
        NewTagCategory.ItemsSource = choices;
        NewTagCategory.SelectedItem = CategoryChoice(choices, current);
    }

    private static string CategoryChoice(IEnumerable<string> choices, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var match = choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return "未分类";
    }

    private void BuildFolderList()
    {
        FolderPanel.Children.Clear(); _folderBoxes.Clear(); _folderTagTexts.Clear();
        foreach (var f in _folders)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = f.Path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Path });
            var tagText = new TextBlock { FontSize = 11, Foreground = ThemeManager.MutedBrush, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            stack.Children.Add(tagText);

            var cb = new CheckBox { Content = stack, Tag = f, Margin = new Thickness(2, 5, 2, 5) };
            cb.Checked += (_, _) => { if (!_suppress) RefreshTagStates(); };
            cb.Unchecked += (_, _) => { if (!_suppress) RefreshTagStates(); };

            _folderBoxes.Add(cb);
            _folderTagTexts[f.Id] = tagText;
            FolderPanel.Children.Add(cb);
        }
    }

    private void ReloadTags()
    {
        var tags = _db.GetTags();
        _tagBoxes.Clear(); _tagNameById.Clear(); _tagIdByName.Clear();
        TagTogglePanel.Children.Clear();
        string currentCategory = "";
        WrapPanel? columnPanel = null;
        foreach (var tag in tags)
        {
            // 按分类分组，组内横向排列，和主界面左侧标签栏保持一致。
            if (!string.Equals(tag.CategoryLabel, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                currentCategory = tag.CategoryLabel;
                TagTogglePanel.Children.Add(new TextBlock
                {
                    Text = currentCategory,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeManager.MutedBrush,
                    Margin = new Thickness(2, 6, 2, 2)
                });
                // 四个字的标签排三列，右侧栏不浪费横向空间。
                columnPanel = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 100 };
                TagTogglePanel.Children.Add(columnPanel);
            }

            _tagNameById[tag.Id] = tag.Name;
            _tagIdByName[tag.Name] = tag.Id;
            var label = new TextBlock
            {
                Text = tag.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = tag.Name,
                MaxWidth = 76,
            };
            var cb = new CheckBox
            {
                Content = label,
                Tag = tag.Id,
                IsThreeState = true,
                Margin = new Thickness(2, 2, 2, 2),
                Width = 96,
                ToolTip = tag.Name,
            };
            cb.Click += TagToggle_Click;   // 用 Click 而不是 Checked/Unchecked：状态由 RefreshTagStates 统一决定
            _tagBoxes[tag.Id] = cb;
            columnPanel!.Children.Add(cb);
        }
        if (tags.Count == 0)
            TagTogglePanel.Children.Add(new TextBlock { Text = "还没有任何标签。在下面输入名称即可新建。", Foreground = ThemeManager.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 0) });
    }

    private void ReloadFolderTags()
    {
        _folderTags = _db.GetAllAssetTags();
        _folderTagIds.Clear();
        foreach (var (assetId, names) in _folderTags)
        {
            var set = new HashSet<long>();
            foreach (var n in names) if (_tagIdByName.TryGetValue(n, out var tagId)) set.Add(tagId);
            _folderTagIds[assetId] = set;
        }
    }

    private bool HasTag(long folderId, long tagId) => _folderTagIds.TryGetValue(folderId, out var set) && set.Contains(tagId);

    private List<AssetItem> SelectedFolders() =>
        _folderBoxes.Where(cb => cb.IsChecked == true).Select(cb => (AssetItem)cb.Tag).ToList();

    /// <summary>按当前所选文件夹重算每个标签的勾选状态，并刷新文件夹行下方的标签文字。</summary>
    private void RefreshTagStates()
    {
        var selected = SelectedFolders();
        _suppress = true;
        try
        {
            foreach (var (tagId, cb) in _tagBoxes)
            {
                if (selected.Count == 0) { cb.IsChecked = false; cb.IsEnabled = false; continue; }
                cb.IsEnabled = true;
                int have = selected.Count(f => HasTag(f.Id, tagId));
                cb.IsChecked = have == 0 ? false : have == selected.Count ? true : null;
            }
        }
        finally { _suppress = false; }

        foreach (var (folderId, text) in _folderTagTexts)
        {
            var names = _folderTags.TryGetValue(folderId, out var list) ? list : null;
            text.Text = names is null || names.Count == 0 ? "（无标签）" : string.Join("、", names);
        }
        SelectionInfo.Text = selected.Count == 0 ? "未选择文件夹" : $"已选 {selected.Count} 个文件夹";
    }

    private void TagToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not CheckBox cb || cb.Tag is not long tagId) return;

        var selected = SelectedFolders();
        if (selected.Count == 0) { RefreshTagStates(); StatusText.Text = "请先在左侧勾选文件夹。"; return; }

        var name = _tagNameById[tagId];
        // 全部都有 → 这次点击理解为「全部移除」；否则一律「全部添加」。
        bool add = !selected.All(f => HasTag(f.Id, tagId));
        foreach (var f in selected) _db.ApplyTag(f.Id, name, remove: !add);

        Changed = true;
        ReloadFolderTags();
        RefreshTagStates();
        StatusText.Text = add
            ? $"已给 {selected.Count} 个文件夹加上「{name}」。"
            : $"已从 {selected.Count} 个文件夹移除「{name}」。";
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请先输入标签名称。"; return; }
        var selected = SelectedFolders();
        if (selected.Count == 0) { StatusText.Text = "请先在左侧勾选文件夹。"; return; }
        try
        {
            var category = NormalizeCategory(NewTagCategory.SelectedItem as string);
            _db.EnsureTag(name, category.Length == 0 ? null : category);
            foreach (var f in selected) _db.ApplyTag(f.Id, name);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "新建标签失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        NewTagBox.Clear();
        Changed = true;
        LoadCategories();
        ReloadTags();
        ReloadFolderTags();
        RefreshTagStates();
        StatusText.Text = $"已把「{name}」加到 {selected.Count} 个文件夹。";
    }

    private static string NormalizeCategory(string? text)
    {
        var value = (text ?? "").Trim();
        return string.Equals(value, "未分类", StringComparison.Ordinal) ? "" : value;
    }

    private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FolderFilter.Text.Trim();
        foreach (var cb in _folderBoxes)
        {
            var f = (AssetItem)cb.Tag;
            cb.Visibility = q.Length == 0 || f.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        foreach (var cb in _folderBoxes.Where(c => c.Visibility == Visibility.Visible)) cb.IsChecked = true;
        _suppress = false;
        RefreshTagStates();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        foreach (var cb in _folderBoxes) cb.IsChecked = false;
        _suppress = false;
        RefreshTagStates();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
