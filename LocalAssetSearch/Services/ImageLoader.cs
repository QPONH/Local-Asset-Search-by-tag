using System.IO;
using System.Windows.Media.Imaging;

namespace LocalAssetSearch.Services;

/// <summary>
/// 统一的静态图片解码入口。
///
/// 为什么不用 <see cref="BitmapDecoder.Create(Stream, BitmapCreateOptions, BitmapCacheOption)"/> 一条路走到黑：
/// 有些 JPEG 的 EXIF 属性类型不合法，WIC 的 JPEG 解码器会抛
/// <c>FileFormatException: 异常的属性类型或值</c>（内部 COM 0x88982F8E），
/// 但同一个文件用 <see cref="BitmapImage"/> 可以正常解出来（它会绕开那段元数据）。
/// 所以先走 BitmapImage，失败再退回 BitmapDecoder；两条都失败才报错。
/// </summary>
public static class ImageLoader
{
    public static BitmapSource Load(string path)
    {
        Exception? first = null;
        try
        {
            return LoadWithBitmapImage(path);
        }
        catch (Exception ex)
        {
            first = ex;
        }

        try
        {
            return LoadWithDecoder(path);
        }
        catch (Exception second)
        {
            throw new InvalidOperationException(
                $"无法解码图片：{second.Message}",
                new AggregateException(first ?? second, second));
        }
    }

    private static BitmapSource LoadWithBitmapImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = fs;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource LoadWithDecoder(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
