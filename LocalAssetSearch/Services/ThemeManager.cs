using System.IO;
using System.Windows;
using System.Windows.Media;

namespace LocalAssetSearch.Services;

/// <summary>
/// 浅色 / 深色主题切换。
///
/// 界面上所有颜色都写成 App.xaml 里那几个画刷的 DynamicResource 引用，
/// 所以这里只要替换资源，已经打开的窗口会立刻跟着变，不需要重建。
/// </summary>
public static class ThemeManager
{
    /// <summary>每个键对应「浅色值 / 深色值」。</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Brush.Window"] = ("#F5F6F8", "#1B1D21"),
        ["Brush.Surface"] = ("#FFFFFF", "#26282D"),
        ["Brush.Card"] = ("#FAFAFB", "#2E3138"),
        ["Brush.Soft"] = ("#EAECF0", "#3A3D44"),
        ["Brush.Border"] = ("#E4E7EC", "#3A3D44"),
        ["Brush.CardBorder"] = ("#EAECF0", "#3E424A"),
        ["Brush.Input"] = ("#FFFFFF", "#1F2126"),
        ["Brush.Text"] = ("#1D2939", "#E6E8EB"),
        ["Brush.Muted"] = ("#667085", "#9BA1AA"),
        ["Brush.ChipText"] = ("#475467", "#C6CBD3"),
    };

    public static bool IsDark { get; private set; }

    /// <summary>供界面取当前主题的文本色，用于代码里动态创建的控件（如标签分组标题）。</summary>
    public static Brush MutedBrush => Brush("Brush.Muted");

    public static Brush Brush(string key) =>
        Application.Current?.Resources[key] as Brush ?? Brushes.Gray;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var res = Application.Current?.Resources;
        if (res is null) return;
        foreach (var (key, (light, darkValue)) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(dark ? darkValue : light)!;
            res[key] = new SolidColorBrush(color);
        }
        Save();
    }

    public static void Toggle() => Apply(!IsDark);

    public static void LoadPreference()
    {
        var value = Settings.Get("theme");
        if (value.Length == 0)
        {
            // 兼容早期只写了一行 dark / light 的旧格式。
            try
            {
                if (File.Exists(Settings.FilePath))
                {
                    var raw = File.ReadAllText(Settings.FilePath).Trim();
                    if (!raw.Contains('=')) value = raw;
                }
            }
            catch { /* 读不到就当浅色 */ }
        }
        Apply(value.Equals("dark", StringComparison.OrdinalIgnoreCase));
    }

    private static void Save() => Settings.Set("theme", IsDark ? "dark" : "light");
}
