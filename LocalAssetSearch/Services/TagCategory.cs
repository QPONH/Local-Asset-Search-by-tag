namespace LocalAssetSearch.Services;

/// <summary>标签分类在下拉框里的公共约定，避免三个标签窗口各写一套。</summary>
public static class TagCategory
{
    public const string Unclassified = "未分类";

    /// <summary>把“未分类”统一成空字符串；其他值去首尾空格。</summary>
    public static string Normalize(string? text)
    {
        var value = (text ?? "").Trim();
        return string.Equals(value, Unclassified, StringComparison.Ordinal) ? "" : value;
    }

    /// <summary>按不区分大小写找到下拉框里对应的分类项；找不到就回“未分类”。</summary>
    public static string Choice(IEnumerable<string> choices, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var match = choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return Unclassified;
    }
}
