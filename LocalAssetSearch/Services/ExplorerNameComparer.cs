using System.Runtime.InteropServices;

namespace LocalAssetSearch.Services;

/// <summary>
/// 按 Windows 资源管理器的规则比较文件名，也就是「自然排序」：
/// 1.png &lt; 2.png &lt; 10.png，而普通字符串排序会得到 1.png &lt; 10.png &lt; 2.png。
///
/// 直接调用 Shell 的 StrCmpLogicalW，所以排序结果与资源管理器里看到的一致，
/// 不需要自己写数字分段解析。该函数是纯比较，不接触文件系统，可安全用于后台线程。
/// </summary>
public sealed class ExplorerNameComparer : IComparer<string>
{
    public static readonly ExplorerNameComparer Instance = new();

    private ExplorerNameComparer() { }

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
