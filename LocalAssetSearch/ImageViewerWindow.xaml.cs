using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 内置看图窗口。
///
/// 为什么要自己写这一个：新版「照片」应用在只传一个文件时经常不给左右翻页
/// （微软问答里就有这类报告），老的「Windows 照片查看器」虽然能翻但组件太旧、
/// 部分系统已经移除。自己实现最省事也最可靠——左右方向键一定能翻同文件夹的图。
///
/// 图片列表用 <see cref="FolderCover.ListImages"/>，和封面取图是同一套顺序，
/// 所以打开时停在封面那张，也就是「第一张」。
///
/// 翻页方式：← → / PageUp·PageDown / 空格、底部按钮、**点窗口任意处**，以及**定时自动翻页**。
/// 到最后一页时：手动点击会关窗；自动翻页下等满一个间隔后自动关窗。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly List<string> _files;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _gifTimer = new();
    private int _index;
    private int _loadVersion;
    private int _lastPresetIndex = 2;      // 默认「每 3 秒」
    private bool _initializing = true;     // XAML 里设置 SelectedIndex 也会触发事件，先挡住
    private bool _timerAdvancing;

    // GIF 动画：WPF 的 Image 不会自己播放 GIF，这里按帧延迟在 UI 线程上逐帧换图。
    private List<BitmapSource>? _gifFrames;
    private List<int>? _gifDelays;
    private int _gifIndex;
    private int _gifFrameCount;

    // 自检用：确认预设真的启动了计时器、点击真的能推进 / 关闭。
    internal int CurrentIndex => _index;
    internal bool IsAtLastPage => AtLast;
    internal bool AutoPageEnabled => _timer.IsEnabled;
    internal TimeSpan AutoPageInterval => _timer.Interval;
    internal int GifFrameCountForTest => _gifFrameCount;
    internal int GifFrameIndexForTest => _gifIndex;
    internal bool GifAnimatingForTest => _gifTimer.IsEnabled;

    public ImageViewerWindow(string folderPath)
    {
        InitializeComponent();

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        Title = string.IsNullOrEmpty(name) ? "看图" : $"{name} — 看图";

        _files = FolderCover.ListImages(folderPath);
        // 从封面那张开始，也就是「第一张」。
        _index = Math.Max(0, _files.IndexOf(FolderCover.Find(folderPath)));

        _timer.Tick += Timer_Tick;
        _gifTimer.Tick += Gif_Tick;
        Closed += (_, _) => { _timer.Stop(); _gifTimer.Stop(); };
        Loaded += async (_, _) => await ShowCurrentAsync();
        _initializing = false;
    }

    // ---------- 显示 ----------

    private void UpdateInfo()
    {
        if (_files.Count == 0)
        {
            InfoText.Text = "这个文件夹里没有可以显示的图片。";
            return;
        }
        var line = $"{_index + 1} / {_files.Count}　{Path.GetFileName(_files[_index])}";
        if (_gifFrameCount > 1) line += $"　·　GIF 动画（{_gifFrameCount} 帧）";
        if (_timer.IsEnabled) line += $"　·　自动翻页（每 {_timer.Interval.TotalSeconds:0} 秒）";
        InfoText.Text = line;
    }

    private async Task ShowCurrentAsync()
    {
        StopGif();
        if (_files.Count == 0)
        {
            Picture.Source = null;
            UpdateInfo();
            return;
        }

        _index = Math.Clamp(_index, 0, _files.Count - 1);
        var path = _files[_index];
        UpdateInfo();

        // 连按方向键时后一次请求会作废前一次，避免解码慢的图把顺序打乱。
        var version = ++_loadVersion;
        try
        {
            if (IsGif(path))
            {
                var gif = await Task.Run(() => DecodeGif(path));
                if (version != _loadVersion) return;
                if (gif.Frames.Count == 0)
                {
                    Picture.Source = null;
                    InfoText.Text = $"无法显示 {Path.GetFileName(path)}：GIF 没有可解码的帧。";
                    return;
                }

                if (gif.Frames.Count > 1)
                {
                    _gifFrames = gif.Frames;
                    _gifDelays = gif.Delays;
                    _gifFrameCount = gif.Frames.Count;
                    _gifIndex = 0;
                    Picture.Source = _gifFrames[0];
                    ScheduleGifFrame();
                    UpdateInfo();
                    return;
                }

                Picture.Source = gif.Frames[0];
                return;
            }

            var bitmap = await Task.Run(() => Decode(path));
            if (version != _loadVersion) return;
            Picture.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            StopGif();
            Picture.Source = null;
            InfoText.Text = $"无法显示 {Path.GetFileName(path)}：{ex.Message}";
        }
    }

    /// <summary>后台线程解码。BitmapSource 必须 Freeze 之后才能交给界面线程。</summary>
    private static BitmapSource Decode(string path) => ImageLoader.Load(path);

    // ---------- GIF 动画 ----------

    private static bool IsGif(string path)
        => string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>自检用：把 GIF 拆成帧和每帧延迟（毫秒）。</summary>
    internal static (List<BitmapSource> Frames, List<int> Delays) DecodeGifForTest(string path) => DecodeGif(path);

    /// <summary>
    /// WIC 的 GIF 解码器会给出所有帧。这里一次性全部解出来并 Freeze，
    /// 之后 UI 线程只负责按延迟换 <see cref="Picture.Source"/>。
    /// </summary>
    private static (List<BitmapSource> Frames, List<int> Delays) DecodeGif(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<int>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            var copy = frame;
            copy.Freeze();
            frames.Add(copy);
            delays.Add(ReadGifDelay(copy));
        }
        return (frames, delays);
    }

    /// <summary>读 GIF 图形控制扩展里的 Delay（单位 1/100 秒）；没有就按 10fps。</summary>
    private static int ReadGifDelay(BitmapSource frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var raw = metadata.GetQuery("/grctlext/Delay");
                var hundredths = raw switch
                {
                    ushort u => u,
                    short s => s,
                    int i => i,
                    _ => 0,
                };
                if (hundredths > 0) return Math.Clamp(hundredths * 10, 20, 1000);
            }
        }
        catch { /* 有些 GIF 没有元数据，按默认帧率 */ }
        return 100;
    }

    private void ScheduleGifFrame()
    {
        if (_gifFrames is null || _gifFrames.Count < 2) return;
        var delay = _gifDelays is { Count: > 0 } ? _gifDelays[_gifIndex] : 100;
        _gifTimer.Stop();
        _gifTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay, 20, 1000));
        _gifTimer.Start();
    }

    private void Gif_Tick(object? sender, EventArgs e)
    {
        if (_gifFrames is null || _gifFrames.Count == 0) { _gifTimer.Stop(); return; }
        _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
        Picture.Source = _gifFrames[_gifIndex];
        ScheduleGifFrame();
    }

    private void StopGif()
    {
        _gifTimer.Stop();
        _gifFrames = null;
        _gifDelays = null;
        _gifFrameCount = 0;
        _gifIndex = 0;
    }

    private bool AtLast => _files.Count == 0 || _index >= _files.Count - 1;

    private async Task StepAsync(int delta)
    {
        if (_files.Count == 0) return;
        var next = _index + delta;
        if (next < 0 || next >= _files.Count) return;
        _index = next;
        await ShowCurrentAsync();
        // 手动翻页后把自动翻页的倒计时重新算起：否则刚点完下一张，
        // 旧计时器可能马上到点又翻一张，看起来像连跳两页。
        RestartAutoTimer();
    }

    // ---------- 自动翻页 ----------

    internal static int PresetSeconds(int index) => index switch { 1 => 2, 2 => 3, 3 => 5, 4 => 10, _ => 3 };

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (IntervalBox.SelectedIndex <= 0)
        {
            _timer.Stop();
            UpdateInfo();
            return;
        }

        _lastPresetIndex = IntervalBox.SelectedIndex;
        _timer.Interval = TimeSpan.FromSeconds(PresetSeconds(IntervalBox.SelectedIndex));
        _timer.Start();
        UpdateInfo();
    }

    /// <summary>A 键：在「不自动翻页」和上次选的间隔之间切换。</summary>
    private void ToggleAuto() => IntervalBox.SelectedIndex = _timer.IsEnabled ? 0 : _lastPresetIndex;

    /// <summary>每次真正翻页后重置倒计时；计时器没开时什么都不做。</summary>
    private void RestartAutoTimer()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _timer.Start();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_timerAdvancing) return;
        _timerAdvancing = true;
        try { await AutoAdvanceAsync(); }
        finally { _timerAdvancing = false; }
    }

    /// <summary>
    /// 自动翻一页。已经在最后一页时关窗——这是「自动翻页下到达最后一页自动关闭」的落点。
    /// 抽出来是为了自检能直接驱动它，不需要真等计时器。
    /// </summary>
    internal async Task AutoAdvanceAsync()
    {
        // 自动翻到最后一页：等满一个间隔后自动关窗，不留下一个静默停住的窗口。
        if (AtLast) { Close(); return; }
        await StepAsync(1);
    }

    /// <summary>自检专用：用很短的间隔启动，验证计时器真的会推进并在末页关窗。</summary>
    internal void StartAutoPageForTest(TimeSpan interval)
    {
        _timer.Interval = interval;
        _timer.Start();
        UpdateInfo();
    }

    // ---------- 点窗口任意处 ----------

    private async void Surface_Click(object sender, MouseButtonEventArgs e) => await ClickAdvanceAsync();

    /// <summary>点窗口任意处：普通页翻下一页，最后一页关闭。</summary>
    internal async Task ClickAdvanceAsync()
    {
        if (AtLast) { Close(); return; }
        await StepAsync(1);
    }

    // ---------- 键盘 ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                await StepAsync(-1); e.Handled = true; break;
            case Key.Right:
            case Key.PageDown:
                await StepAsync(1); e.Handled = true; break;
            // 空格：普通页翻下一页；已经在最后一页时直接关窗。
            case Key.Space when AtLast:
                Close(); e.Handled = true; break;
            case Key.Space:
                await StepAsync(1); e.Handled = true; break;
            case Key.Home:
                _index = 0; await ShowCurrentAsync(); e.Handled = true; break;
            case Key.End:
                _index = _files.Count - 1; await ShowCurrentAsync(); e.Handled = true; break;
            case Key.A when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0:
                ToggleAuto(); e.Handled = true; break;
            case Key.Escape:
                Close(); e.Handled = true; break;
            // Ctrl+W 关窗，和大多数看图/浏览器一致。
            case Key.W when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                Close(); e.Handled = true; break;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await StepAsync(1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
