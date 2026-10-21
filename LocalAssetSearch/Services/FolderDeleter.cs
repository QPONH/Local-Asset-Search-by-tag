using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace LocalAssetSearch.Services;

/// <summary>
/// 把选中的文件夹移到回收站（不直接永久删除），供「删除文件夹及索引」使用。
/// 父文件夹和子文件夹同时被选中时只删父文件夹，避免重复操作。
/// </summary>
public static class FolderDeleter
{
    public sealed record Result(int DeletedFolders, IReadOnlyList<string> DeletedPaths, IReadOnlyList<string> Errors);

    public static Result DeleteToRecycleBin(IEnumerable<string> paths)
    {
        var all = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var full = Path.GetFullPath(raw.Trim());
                if (!all.Contains(full, StringComparer.OrdinalIgnoreCase)) all.Add(full);
            }
            catch { /* 路径本身无效就跳过 */ }
        }

        // 先处理较短的父路径；父路径已经覆盖的子路径不再单独删。
        var roots = new List<string>();
        foreach (var path in all.OrderBy(p => p.Length))
            if (!roots.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase) || IsUnder(path, r)))
                roots.Add(path);

        var deletedRoots = new List<string>();
        var errors = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                // 磁盘上已经没有了，按已处理算，索引可以安全移除。
                deletedRoots.Add(root);
                continue;
            }

            try
            {
                FileSystem.DeleteDirectory(root, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                deletedRoots.Add(root);
            }
            catch (Exception ex)
            {
                errors.Add($"{root}\n  {ex.Message}");
            }
        }

        // 被删父目录覆盖到的所有选中路径都算完成，方便调用方按路径移除索引。
        var deletedPaths = all
            .Where(p => deletedRoots.Any(root => string.Equals(root, p, StringComparison.OrdinalIgnoreCase) || IsUnder(p, root)))
            .ToList();

        return new Result(deletedPaths.Count, deletedPaths, errors);
    }

    private static bool IsUnder(string path, string parent)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
