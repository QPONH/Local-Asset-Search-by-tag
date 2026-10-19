using System.IO;

namespace LocalAssetSearch.Services;

/// <summary>
/// 极简启动日志。
///
/// 这个程序是 WinExe——没有控制台窗口。所以任何启动阶段的异常都表现为
/// 「双击了但什么都没发生」，用户拿不到任何线索。启动过程写日志，
/// 至少能知道走到了哪一步、为什么挂。
/// </summary>
public static class StartupLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    /// <summary>日志文件路径。优先放在 exe 旁边，不可写时退回 TEMP。</summary>
    public static string LogPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                // 真的试着写一下，别只看目录存不存在。
                var probe = Path.Combine(dir, ".las-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return Path.Combine(dir, "LocalAssetSearch.log");
            }
            catch { /* 换下一个候选目录 */ }
        }
        return Path.Combine(Path.GetTempPath(), "LocalAssetSearch.log");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志本身绝不能把程序拖垮 */ }
    }
}
