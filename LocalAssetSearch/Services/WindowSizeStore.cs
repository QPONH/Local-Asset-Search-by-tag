using System.Globalization;
using System.Windows;

namespace LocalAssetSearch.Services;

/// <summary>记住每个窗口上一次的普通尺寸，下次打开恢复。</summary>
public static class WindowSizeStore
{
    public static void Restore(Window window, string key)
    {
        // 没有保存过状态时保留 XAML 默认值（看图窗口默认最大化）。
        var state = Settings.Get($"window.{key}.maximized", "");
        if (state == "1") window.WindowState = WindowState.Maximized;
        else if (state == "0") window.WindowState = WindowState.Normal;

        var width = Read($"window.{key}.width");
        var height = Read($"window.{key}.height");
        if (width is > 0) window.Width = Math.Max(window.MinWidth, width.Value);
        if (height is > 0) window.Height = Math.Max(window.MinHeight, height.Value);
    }

    public static void Track(Window window, string key)
    {
        window.Closed += (_, _) => Save(window, key);
    }

    private static void Save(Window window, string key)
    {
        Settings.Set($"window.{key}.maximized", window.WindowState == WindowState.Maximized ? "1" : "0");
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.RestoreBounds.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.RestoreBounds.Height;
        if (window.WindowState != WindowState.Normal || width <= 0 || height <= 0)
        {
            width = window.RestoreBounds.Width;
            height = window.RestoreBounds.Height;
        }
        if (width <= 0 || height <= 0) return;
        Settings.Set($"window.{key}.width", width.ToString("0.##", CultureInfo.InvariantCulture));
        Settings.Set($"window.{key}.height", height.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static double? Read(string key)
        => double.TryParse(Settings.Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
}
