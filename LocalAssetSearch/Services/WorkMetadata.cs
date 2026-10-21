using System.IO;
using System.Text.Json;

namespace LocalAssetSearch.Services;

/// <summary>
/// 每个作品文件夹里的 <c>work-id.json</c>。它是下载/备份工具生成的识别文件，
/// 比目录名末尾那串数字可靠：目录名可能被改过，末尾的 <c>{319}</c> 也未必是收藏夹页码。
///
/// 只读取需要的字段；文件不存在、JSON 坏了或字段缺失都返回 null，调用方回退到目录名解析。
/// </summary>
public sealed record WorkMetadata(
    string? WorkId,
    string? Title,
    IReadOnlyList<string> Authors,
    int? FavoritePage,
    int? FavoritePosition,
    int? GlobalIndex,
    int? ImageCount,
    bool? InFavorites)
{
    public string AuthorsText => Authors.Count == 0 ? "" : string.Join("、", Authors);

    public static WorkMetadata? TryRead(string folderPath)
    {
        try
        {
            var file = Path.Combine(folderPath, "work-id.json");
            if (!File.Exists(file)) return null;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? GetString(string name)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => null,
                    };
                }
                return null;
            }

            int? GetInt(string name)
                => int.TryParse(GetString(name), out var value) ? value : null;

            bool? GetBool(string name)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                }
                return null;
            }

            var authors = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "Authors", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Array) break;
                foreach (var author in property.Value.EnumerateArray())
                {
                    var name = author.ValueKind == JsonValueKind.String ? author.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name)) authors.Add(name.Trim());
                }
                break;
            }

            var workId = GetString("WorkId")?.Trim();
            var title = GetString("Title")?.Trim();
            if (string.IsNullOrWhiteSpace(workId) && string.IsNullOrWhiteSpace(title)) return null;

            return new WorkMetadata(
                string.IsNullOrWhiteSpace(workId) ? null : workId,
                string.IsNullOrWhiteSpace(title) ? null : title,
                authors,
                GetInt("FavoritePage"),
                GetInt("FavoritePosition"),
                GetInt("GlobalIndex"),
                GetInt("ImageCount"),
                GetBool("InFavorites"));
        }
        catch
        {
            // 识别文件坏了不应该让扫描失败，回退目录名即可。
            return null;
        }
    }
}
