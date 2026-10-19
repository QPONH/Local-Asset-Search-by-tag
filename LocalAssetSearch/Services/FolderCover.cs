using System.IO;   // WPF 项目的隐式 using 不含 System.IO，必须显式引入

namespace LocalAssetSearch.Services;

/// <summary>
/// 决定一个文件夹用哪个文件当封面。
///
/// 单独放在这里而不是塞在窗口类里，是为了能直接对着真实目录验证排序规则。
/// </summary>
public static class FolderCover
{
    /// <summary>可被 WPF 直接解码的图片扩展名。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif" };

    /// <summary>
    /// 列出文件夹里的图片，按资源管理器的自然顺序（1.png &lt; 2.png &lt; 10.png）。
    /// 只看直接子文件，不递归。看图窗口用同一个列表，保证和封面是同一顺序。
    /// </summary>
    public static List<string> ListImages(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath)
                .Where(p => ImageExtensions.Contains(Path.GetExtension(p)))
                .OrderBy(p => Path.GetFileName(p) ?? "", ExplorerNameComparer.Instance)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 一次列出目录，同时给出封面文件和图片总数。
    /// 卡片既要封面又要数量，分两次调用会把同一个目录列两遍，这里合并掉。
    /// </summary>
    public static (string Cover, int Count) CoverAndCount(string folderPath)
    {
        var files = ListImages(folderPath);
        if (files.Count == 0) return ("", 0);

        var numberedZero = files.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "0");
        return (numberedZero ?? files[0], files.Count);
    }

    /// <summary>
    /// 只看直接子文件，不向下递归——素材目录按设计只有一层，递归既慢又会让子目录抢走封面。
    ///
    /// 顺序规则：
    ///   1. 主文件名恰为 "0" 的图片优先（0.jpg / 0.png）；
    ///   2. 否则按资源管理器的自然顺序取第一个，即 1.png &lt; 2.png &lt; 10.png。
    ///
    /// 旧实现用 OrdinalIgnoreCase 字符串比较，会得到 1 &lt; 10 &lt; 2，把第 10 张当成封面。
    /// </summary>
    public static string Find(string folderPath) => CoverAndCount(folderPath).Cover;
}
