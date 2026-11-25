using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalAssetSearch.Models;

namespace LocalAssetSearch.Services;

/// <summary>
/// 标签按分类分组显示：每组一个可点击的标题（▶/▼ + 右侧横线）+ 内容容器。
///
/// 所有标签界面（主窗口左侧筛选、打标签窗口、管理文件夹标签、看图侧边栏）都用这里，
/// 保证折叠行为一致；作者组统一默认折叠。分类顺序由 <see cref="Database.GetTags"/>
/// （Categories.SortOrder）决定，调用方只需按顺序遍历。
/// </summary>
public static class TagGroupView
{
    /// <summary>按分类分组，保持传入顺序（<c>GetTags</c> 已经按分类顺序排好）。</summary>
    public static IEnumerable<(string Name, List<TagInfo> Tags)> Group(IEnumerable<TagInfo> tags)
    {
        var order = new List<string>();
        var map = new Dictionary<string, List<TagInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var name = string.IsNullOrWhiteSpace(tag.Category) ? TagCategory.Unclassified : tag.Category.Trim();
            if (!map.TryGetValue(name, out var list))
            {
                list = new List<TagInfo>();
                map[name] = list;
                order.Add(name);
            }
            list.Add(tag);
        }
        foreach (var name in order) yield return (name, map[name]);
    }

    /// <summary>作者组自动建的标签通常很长，默认折叠。</summary>
    public static bool IsAuthorGroup(string name)
        => string.Equals(name, TagCategory.Author, StringComparison.Ordinal);

    /// <summary>添加一个可折叠分组，返回句柄（需要程序控制展开/收起时用）。</summary>
    public static CollapsibleGroup AddCollapsible(Panel host, string title, bool collapsed, FrameworkElement content)
    {
        // 标题行：文字 + 向右延伸的横线（和原来 TagHeader 的观感一致），整行可点击折叠。
        var header = new Grid
        {
            Margin = new Thickness(2, 8, 2, 4),
            Cursor = Cursors.Hand,
            ToolTip = "点击展开 / 收起",
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        };
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 2, 0),
            Background = ThemeManager.Brush("Brush.Border"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 1);
        header.Children.Add(titleText);
        header.Children.Add(rule);

        var group = new CollapsibleGroup(title, titleText, header, content);
        group.SetCollapsed(collapsed);
        header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            group.SetCollapsed(!group.Collapsed);
        };

        host.Children.Add(header);
        host.Children.Add(content);
        return group;
    }
}

/// <summary>一个可折叠的标签分类组。</summary>
public sealed class CollapsibleGroup
{
    public string Title { get; }

    /// <summary>整行标题（含横线），需要隐藏整组时用。</summary>
    public FrameworkElement Header { get; }

    public FrameworkElement Content { get; }

    public bool Collapsed { get; private set; }

    private readonly TextBlock _titleText;

    internal CollapsibleGroup(string title, TextBlock titleText, FrameworkElement header, FrameworkElement content)
    {
        Title = title;
        _titleText = titleText;
        Header = header;
        Content = content;
    }

    public void SetCollapsed(bool collapsed)
    {
        Collapsed = collapsed;
        _titleText.Text = (collapsed ? "▶ " : "▼ ") + Title;
        Content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }
}
