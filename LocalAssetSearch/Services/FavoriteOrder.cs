using System.Text.RegularExpressions;

namespace LocalAssetSearch.Services;

/// <summary>
/// 解析目录名末尾那段 <c>{收藏夹页码,页内序号}</c>。
///
/// AHRI8Backup 生成的目录名规则是：<c>标题{作者}{收藏夹页码,页内序号}</c>
/// 例如作品 <c>a</c>、作者 <c>b</c>、位于收藏夹第 1 页第 3 个 → <c>a{b}{1,3}</c>。
/// 有多个作者时是多个 <c>{作者}</c> 段，所以只认**末尾**那一组带逗号的数字。
/// </summary>
public static class FavoriteOrder
{
    /// <summary>末尾的 <c>{页数,位置}</c>。</summary>
    private static readonly Regex PairPattern = new(@"\{\s*(\d+)\s*,\s*(\d+)\s*\}\s*$", RegexOptions.Compiled);

    /// <summary>末尾只有一个数字的 <c>{数字}</c>，例如 <c>xxx{a}{2}</c>。这时页数按这个数字算，位置记 0。</summary>
    private static readonly Regex SinglePattern = new(@"\{\s*(\d+)\s*\}\s*$", RegexOptions.Compiled);

    /// <summary>解析不出就返回 null（这类目录排在解析得出的后面）。</summary>
    public static (int Page, int Position)? Parse(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;

        var pair = PairPattern.Match(folderName);
        if (pair.Success
            && int.TryParse(pair.Groups[1].Value, out var page)
            && int.TryParse(pair.Groups[2].Value, out var position))
            return (page, position);

        var single = SinglePattern.Match(folderName);
        if (single.Success && int.TryParse(single.Groups[1].Value, out var only))
            return (only, 0);

        return null;
    }

    /// <summary>
    /// 取出标题部分，也就是第一个 <c>{</c> 之前的内容——「首字母排序」比的是它，
    /// 而不是把后面的 <c>{作者}{页码,序号}</c> 一起算进去。
    /// </summary>
    public static string TitleOf(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return "";
        var i = folderName.IndexOf('{');
        return (i < 0 ? folderName : folderName[..i]).Trim();
    }
}
