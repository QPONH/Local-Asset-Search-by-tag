using System.IO;

namespace LocalAssetSearch.Services;

/// <summary>
/// 极简的键值设置文件（数据目录下的 settings.ini），一行一个 <c>key=value</c>。
/// 用来记住主题、排序方式这类界面偏好。
/// </summary>
public static class Settings
{
    public static string FilePath => Path.Combine(Database.ResolveDataDirectory(), "settings.ini");

    public static string Get(string key, string fallback = "")
    {
        try
        {
            if (!File.Exists(FilePath)) return fallback;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                if (line[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(i + 1)..].Trim();
            }
        }
        catch { /* 读不到就用默认值 */ }
        return fallback;
    }

    public static void Set(string key, string value)
    {
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var i = line.IndexOf('=');
                    if (i > 0) map[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
            map[key] = value;

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(FilePath, map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch { /* 记不住也不影响本次使用 */ }
    }
}
