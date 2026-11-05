using System.IO;
using System.Text.Json;

namespace LocalAssetSearch.Services;

public sealed class ViewerHistoryEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Time { get; set; }
}

/// <summary>
/// 看图窗口的「最近打开 / 最近关闭」各保留 10 个文件夹，同一个文件夹只留最新一次。
/// 存成用户数据目录下的 viewer-history.json，不会写进素材目录。
/// </summary>
public static class ViewerHistory
{
    private const int MaxEntries = 10;
    private static readonly object Gate = new();

    private sealed class Data
    {
        public List<ViewerHistoryEntry> Opened { get; set; } = new();
        public List<ViewerHistoryEntry> Closed { get; set; } = new();
    }

    public static event Action? Changed;

    public static IReadOnlyList<ViewerHistoryEntry> Opened => Load().Opened;
    public static IReadOnlyList<ViewerHistoryEntry> Closed => Load().Closed;

    public static string FilePath => Path.Combine(Database.DataDirectory, "viewer-history.json");

    public static void AddOpen(string folderPath) => Add(folderPath, opened: true);
    public static void AddClose(string folderPath) => Add(folderPath, opened: false);

    private static void Add(string folderPath, bool opened)
    {
        var path = SafeFullPath(folderPath);
        lock (Gate)
        {
            var data = Load();
            var list = opened ? data.Opened : data.Closed;
            list.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new ViewerHistoryEntry
            {
                Path = path,
                Name = FolderName(path),
                Time = DateTime.Now,
            });
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            Save(data);
        }
        Changed?.Invoke();
    }

    public static void ClearOpened() => Clear(opened: true);
    public static void ClearClosed() => Clear(opened: false);

    private static void Clear(bool opened)
    {
        lock (Gate)
        {
            var data = Load();
            (opened ? data.Opened : data.Closed).Clear();
            Save(data);
        }
        Changed?.Invoke();
    }

    private static Data Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Data();
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
            data.Opened ??= new List<ViewerHistoryEntry>();
            data.Closed ??= new List<ViewerHistoryEntry>();
            return data;
        }
        catch
        {
            return new Data();
        }
    }

    private static void Save(Data data)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 历史记录失败不影响看图 */ }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
