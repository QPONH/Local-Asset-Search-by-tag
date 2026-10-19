namespace LocalAssetSearch.Models;

/// <summary>
/// 一个标签的定义（不含它挂在哪些素材上）。分类用于把标签分组显示。
/// </summary>
public sealed class TagInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>分类名。空字符串表示未分类。</summary>
    public string Category { get; set; } = "";

    /// <summary>有多少个素材挂着这个标签。</summary>
    public int UsageCount { get; set; }

    /// <summary>界面上显示的分类名，空分类统一显示成「未分类」。</summary>
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? "未分类" : Category;

    public override string ToString() => Name;
}
