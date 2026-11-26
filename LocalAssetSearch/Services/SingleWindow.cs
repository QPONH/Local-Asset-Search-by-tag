using System.Windows;

namespace LocalAssetSearch.Services;

/// <summary>
/// 同一个窗口只允许开一个：再次点开时把已经打开的那个提到最前，而不是再弹一个新的。
///
/// 用字符串 key 区分：标签管理 / 管理文件夹标签 / 历史记录各一个；
/// 看图器按作品路径各一个（同一个作品重复打开就切过去，不同作品仍然可以多开）。
/// </summary>
public static class SingleWindow
{
    private static readonly Dictionary<string, Window> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经有这个 key 的窗口就激活并返回 true；没有（或已关闭）返回 false。</summary>
    public static bool TryActivate(string key)
    {
        if (!Open.TryGetValue(key, out var window)) return false;
        if (!window.IsVisible)
        {
            Open.Remove(key);
            return false;
        }

        // 后台打开的看图器被压在 Z 序最底，Activate 不一定能把人拉到最前；
        // 临时切一次 Topmost 最稳（会闪过一下，但确实到最前）。
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
        return true;
    }

    public static void Track(string key, Window window)
    {
        Open[key] = window;
        window.Closed += (_, _) =>
        {
            if (Open.TryGetValue(key, out var current) && ReferenceEquals(current, window)) Open.Remove(key);
        };
    }
}
