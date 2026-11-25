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

    /// <summary>
    /// 目录名里的作者段。格式是 <c>作品名{作者a}{作者b}{收藏夹位置}</c>：
    /// 去掉开头的标题和**末尾那一段位置**（纯数字或逗号数字），中间所有 <c>{...}</c> 都是作者。
    /// 末尾不是位置的（比如只写了 <c>作品名{作者}</c>）就整段都当作者。
    /// </summary>
    public static List<string> AuthorsOf(string? folderName)
    {
        var authors = new List<string>();
        if (string.IsNullOrEmpty(folderName)) return authors;

        var segments = BracesOf(folderName);
        if (segments.Count > 0 && IsPosition(segments[^1])) segments.RemoveAt(segments.Count - 1);

        foreach (var segment in segments)
        {
            var author = segment.Trim();
            if (author.Length > 0 && !authors.Contains(author, StringComparer.OrdinalIgnoreCase))
                authors.Add(author);
        }
        return authors;
    }

    /// <summary>所有成对的 <c>{...}</c> 里的内容，按出现顺序（支持嵌套括号）。</summary>
    private static List<string> BracesOf(string name)
    {
        var list = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '{')
            {
                if (depth == 0) start = i + 1;
                depth++;
            }
            else if (name[i] == '}')
            {
                depth--;
                if (depth == 0) list.Add(name[start..i]);
            }
        }
        return list;
    }

    private static bool IsPosition(string segment)
        => Regex.IsMatch(segment, @"^\s*\d+\s*([,，]\s*\d+\s*)?$");
}
