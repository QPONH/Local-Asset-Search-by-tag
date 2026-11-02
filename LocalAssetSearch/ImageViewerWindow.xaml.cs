using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private enum WheelMode { Page, Zoom, Interval }

    private readonly List<string> _files;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _gifTimer = new();
    private int _index;
    private int _loadVersion;
    private bool _initializing = true;     // XAML 里设置选中项/文字也会触发事件，先挡住
    private bool _timerAdvancing;
    private double _intervalSeconds = 3.0;

    private bool _doublePage;
    private int _doubleStep = 2;

    // GIF 动画：WPF 的 Image 不会自己播放 GIF，这里按帧延迟在 UI 线程上逐帧换图。
    private List<BitmapSource>? _gifFrames;
    private List<int>? _gifDelays;
    private int _gifIndex;
    private int _gifFrameCount;

    // 滚轮模式：翻页 / 缩放 / 调自动翻页间隔；缩放后左键拖动平移。
    private WheelMode _wheelMode = WheelMode.Page;
    private double _zoomScale = 1.0;
    private Point _zoomOffset;
    private bool _panning;
    private bool _panMoved;
    private bool _suppressClick;
    private Point _panStart;
    private Point _panOrigin;

    // 自检用：确认计时器、翻页、GIF、滚轮和双页状态。
    internal int CurrentIndex => _index;
    internal bool IsAtLastPage => AtLast;
    internal bool AutoPageEnabled => _timer.IsEnabled;
    internal TimeSpan AutoPageInterval => _timer.Interval;
    internal double AutoPageSecondsForTest => _intervalSeconds;
    internal int GifFrameCountForTest => _gifFrameCount;
    internal int GifFrameIndexForTest => _gifIndex;
    internal bool GifAnimatingForTest => _gifTimer.IsEnabled;
    internal bool WheelZoomModeForTest => _wheelMode == WheelMode.Zoom;
    internal int WheelModeForTest => (int)_wheelMode;
    internal bool DoublePageForTest => _doublePage;
    internal int DoubleStepForTest => _doubleStep;
    internal double ZoomScaleForTest => _zoomScale;

    public ImageViewerWindow(string folderPath)
    {
        InitializeComponent();
        WindowSizeStore.Restore(this, "viewer");
        WindowSizeStore.Track(this, "viewer");

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        Title = string.IsNullOrEmpty(name) ? "看图" : $"{name} — 看图";

        _files = FolderCover.ListImages(folderPath);
        // 从封面那张开始，也就是「第一张」。
        _index = Math.Max(0, _files.IndexOf(FolderCover.Find(folderPath)));

        if (double.TryParse(Settings.Get("viewer.interval", "3"), NumberStyles.Float, CultureInfo.InvariantCulture, out var savedInterval))
            _intervalSeconds = Math.Clamp(Math.Round(savedInterval, 1), 0.1, 3600);
        IntervalBox.Text = _intervalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        _timer.Interval = TimeSpan.FromSeconds(_intervalSeconds);
        AutoPageCheck.IsChecked = false;

        // 滚轮模式是全局偏好，所有看图窗口共用。
        _wheelMode = Settings.Get("viewer.wheel", "page") switch
        {
            "zoom" => WheelMode.Zoom,
            "interval" => WheelMode.Interval,
            _ => WheelMode.Page,
        };
        WheelBox.SelectedIndex = (int)_wheelMode;

        _doublePage = Settings.Get("viewer.double", "0") == "1";
        _doubleStep = Settings.Get("viewer.doubleStep", "2") == "1" ? 1 : 2;
        DoublePageCheck.IsChecked = _doublePage;
        DoubleStepBox.SelectedIndex = _doubleStep == 1 ? 0 : 1;
        UpdatePagesLayout();

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
        var hasRight = _doublePage && _index + 1 < _files.Count;
        var line = hasRight
            ? $"{_index + 1}–{_index + 2} / {_files.Count}　{Path.GetFileName(_files[_index])}  |  {Path.GetFileName(_files[_index + 1])}"
            : $"{_index + 1} / {_files.Count}　{Path.GetFileName(_files[_index])}";
        if (_gifFrameCount > 1) line += $"　·　GIF 动画（{_gifFrameCount} 帧）";
        if (_timer.IsEnabled) line += $"　·　自动翻页（每 {_intervalSeconds:0.#} 秒）";
        InfoText.Text = line;
    }

    private async Task ShowCurrentAsync()
    {
        StopGif();
        ResetZoom();
        if (_files.Count == 0)
        {
            Picture.Source = null;
            PictureRight.Source = null;
            UpdateInfo();
            return;
        }

        _index = Math.Clamp(_index, 0, _files.Count - 1);
        var leftPath = _files[_index];
        var rightPath = _doublePage && _index + 1 < _files.Count ? _files[_index + 1] : null;
        UpdateInfo();

        // 连按方向键时后一次请求会作废前一次，避免解码慢的图把顺序打乱。
        var version = ++_loadVersion;
        await LoadLeftAsync(leftPath, version);
        if (version != _loadVersion) return;
        await LoadRightAsync(rightPath, version);
    }

    private async Task LoadLeftAsync(string path, int version)
    {
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

    /// <summary>右侧页暂时按静态图显示；左侧 GIF 在单页/双页下都继续动画。</summary>
    private async Task LoadRightAsync(string? path, int version)
    {
        if (!_doublePage || path is null)
        {
            PictureRight.Source = null;
            return;
        }
        try
        {
            var bitmap = await Task.Run(() => Decode(path));
            if (version == _loadVersion) PictureRight.Source = bitmap;
        }
        catch
        {
            if (version == _loadVersion) PictureRight.Source = null;
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

    private int PageStep => _doublePage ? _doubleStep : 1;
    private bool AtLast => _files.Count == 0 || _index + PageStep >= _files.Count;

    private async Task StepAsync(int delta)
    {
        if (_files.Count == 0) return;
        var step = _doublePage ? _doubleStep : 1;
        var next = _index + (delta < 0 ? -step : step);
        next = Math.Clamp(next, 0, _files.Count - 1);
        if (next == _index) return;
        _index = next;
        await ShowCurrentAsync();
        // 手动翻页后把自动翻页的倒计时重新算起：否则刚点完下一张，
        // 旧计时器可能马上到点又翻一张，看起来像连跳两页。
        RestartAutoTimer();
    }

    private void UpdatePagesLayout()
    {
        Grid.SetColumnSpan(Picture, _doublePage ? 1 : 2);
        PictureRight.Visibility = _doublePage ? Visibility.Visible : Visibility.Collapsed;
        DoubleStepBox.IsEnabled = _doublePage;
        if (!_doublePage) PictureRight.Source = null;
    }

    private async void DoublePage_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _doublePage = DoublePageCheck.IsChecked == true;
        Settings.Set("viewer.double", _doublePage ? "1" : "0");
        UpdatePagesLayout();
        await ShowCurrentAsync();
    }

    private void DoubleStep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _doubleStep = DoubleStepBox.SelectedIndex == 0 ? 1 : 2;
        Settings.Set("viewer.doubleStep", _doubleStep.ToString(CultureInfo.InvariantCulture));
        UpdateInfo();
    }

    // ---------- 自动翻页 ----------

    private void AutoPage_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (AutoPageCheck.IsChecked == true)
        {
            if (!TryReadInterval(out var seconds)) seconds = _intervalSeconds;
            SetInterval(seconds, updateText: true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
        UpdateInfo();
    }

    private void Interval_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (TryReadInterval(out var seconds)) SetInterval(seconds, updateText: false);
    }

    private bool TryReadInterval(out double seconds)
    {
        seconds = 0;
        var text = IntervalBox.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (value <= 0) return false;
        seconds = Math.Clamp(Math.Round(value, 1), 0.1, 3600);
        return true;
    }

    private void SetInterval(double seconds, bool updateText)
    {
        seconds = Math.Clamp(Math.Round(seconds, 1), 0.1, 3600);
        _intervalSeconds = seconds;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        if (updateText)
        {
            var text = seconds.ToString("0.#", CultureInfo.InvariantCulture);
            if (IntervalBox.Text != text) IntervalBox.Text = text;
        }
        Settings.Set("viewer.interval", seconds.ToString("0.#", CultureInfo.InvariantCulture));
        UpdateInfo();
    }

    /// <summary>A 键：开 / 关自动翻页。</summary>
    private void ToggleAuto() => AutoPageCheck.IsChecked = !_timer.IsEnabled;

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

    private async void Surface_Click(object sender, MouseButtonEventArgs e)
    {
        // 下方工具栏空白处、信息栏点击不翻页；只有图片区域才响应。
        if (!Viewport.IsMouseOver) return;

        // 缩放模式下拖动过画面时，这一次抬手不算点击翻页。
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }
        await ClickAdvanceAsync();
    }

    /// <summary>点窗口任意处：普通页翻下一页，最后一页关闭。</summary>
    internal async Task ClickAdvanceAsync()
    {
        if (AtLast) { Close(); return; }
        await StepAsync(1);
    }

    // ---------- 滚轮：翻页 / 缩放 ----------

    private void Wheel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _wheelMode = WheelBox.SelectedIndex switch
        {
            1 => WheelMode.Zoom,
            2 => WheelMode.Interval,
            _ => WheelMode.Page,
        };
        Settings.Set("viewer.wheel", _wheelMode switch
        {
            WheelMode.Zoom => "zoom",
            WheelMode.Interval => "interval",
            _ => "page",
        });
        if (_wheelMode != WheelMode.Zoom) ResetZoom();
        else UpdateZoomCursor();
    }

    private async void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_files.Count == 0) return;
        e.Handled = true;

        if (_wheelMode == WheelMode.Interval)
        {
            // 向上加快（间隔 -0.1 秒），向下减慢（+0.1 秒）。
            if (e.Delta > 0) SetInterval(_intervalSeconds - 0.1, updateText: true);
            else if (e.Delta < 0) SetInterval(_intervalSeconds + 0.1, updateText: true);
            return;
        }

        if (_wheelMode == WheelMode.Page)
        {
            if (e.Delta > 0) await StepAsync(-1);
            else if (e.Delta < 0) await StepAsync(1);
            return;
        }

        // 用 Viewport 坐标算锚点。不要用 e.GetPosition(Picture)：Picture 上的
        // RenderTransform 会让它返回已经受缩放/平移影响的位置，放大几次就会漂。
        var cursor = e.GetPosition(Viewport);
        var origin = VisualTreeHelper.GetOffset(Picture);
        var local = new Point(cursor.X - origin.X, cursor.Y - origin.Y);

        var factor = Math.Pow(1.15, e.Delta / 120.0);
        var newScale = Math.Clamp(_zoomScale * factor, 1.0, 6.0);
        if (newScale <= 1.001)
        {
            ResetZoom();
            return;
        }

        // TransformGroup 先缩放再平移，screen = local * scale + offset。
        // 鼠标下的画面点 = (local - offset) / oldScale，必须让它落在同一个屏幕位置。
        // 注意 offset 不能直接用 local 乘缩放差：offset 非 0 时（已经放大/平移过）
        // 那样算会把“缩小”算成一次平移，尤其鼠标在左右两侧时偏移会很大。
        _zoomOffset = ZoomOffsetAfter(local, _zoomOffset, _zoomScale, newScale);
        _zoomScale = newScale;
        UpdateZoomTransform();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 上一次拖动结束时有些情况下根 Grid 收不到 MouseUp，标志会留到下一次点击；
        // 每次按下先清掉，避免吞掉接下来的第一次翻页点击。
        _suppressClick = false;
        if (_wheelMode != WheelMode.Zoom || _zoomScale <= 1.001) return;
        _panning = true;
        _panMoved = false;
        _panStart = e.GetPosition(Viewport);
        _panOrigin = _zoomOffset;
        Viewport.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var current = e.GetPosition(Viewport);
        var dx = current.X - _panStart.X;
        var dy = current.Y - _panStart.Y;
        if (!_panMoved && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _panMoved = true;
        if (!_panMoved) return;

        _zoomOffset = new Point(_panOrigin.X + dx, _panOrigin.Y + dy);
        UpdateZoomTransform();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        var moved = _panMoved;
        _panning = false;
        _panMoved = false;
        Viewport.ReleaseMouseCapture();
        // 拖过画面就不再让点击事件翻页；没拖动则保留原来的「点任意处翻页」。
        if (moved)
        {
            _suppressClick = true;
            e.Handled = true;
        }
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _panning = false;
        _panMoved = false;
    }

    /// <summary>
    /// 计算缩放后的平移量，保证 local（相对 Picture 布局原点）处的画面点不动。
    /// screen = local * scale + offset，所以：
    ///   anchor = (local - oldOffset) / oldScale
    ///   newOffset = local - anchor * newScale
    /// </summary>
    internal static Point ZoomOffsetAfter(Point local, Point oldOffset, double oldScale, double newScale)
    {
        if (oldScale <= 0) return oldOffset;
        double ax = (local.X - oldOffset.X) / oldScale;
        double ay = (local.Y - oldOffset.Y) / oldScale;
        return new Point(local.X - ax * newScale, local.Y - ay * newScale);
    }

    private void UpdateZoomTransform()
    {
        PictureScale.ScaleX = _zoomScale;
        PictureScale.ScaleY = _zoomScale;
        PictureTranslate.X = _zoomOffset.X;
        PictureTranslate.Y = _zoomOffset.Y;
        PictureRightScale.ScaleX = _zoomScale;
        PictureRightScale.ScaleY = _zoomScale;
        PictureRightTranslate.X = _zoomOffset.X;
        PictureRightTranslate.Y = _zoomOffset.Y;
        UpdateZoomCursor();
    }

    private void ResetZoom()
    {
        _zoomScale = 1.0;
        _zoomOffset = default;
        _panning = false;
        _panMoved = false;
        _suppressClick = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        PictureScale.ScaleX = 1.0;
        PictureScale.ScaleY = 1.0;
        PictureTranslate.X = 0;
        PictureTranslate.Y = 0;
        PictureRightScale.ScaleX = 1.0;
        PictureRightScale.ScaleY = 1.0;
        PictureRightTranslate.X = 0;
        PictureRightTranslate.Y = 0;
        UpdateZoomCursor();
    }

    private void UpdateZoomCursor()
    {
        Viewport.Cursor = _wheelMode == WheelMode.Zoom && _zoomScale > 1.001 ? Cursors.SizeAll : Cursors.Arrow;
    }

    // ---------- 键盘 ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入间隔时，方向键/空格/字母都留给输入框；Esc 和 Ctrl+W 仍可关窗。
        if (Keyboard.FocusedElement is TextBox &&
            e.Key != Key.Escape &&
            !(e.Key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            return;

        // 先置 Handled 再 await：否则下拉框会在异步翻页期间收到方向键并改选项。
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                e.Handled = true; await StepAsync(-1); break;
            case Key.Right:
            case Key.PageDown:
                e.Handled = true; await StepAsync(1); break;
            // 空格：普通页翻下一页；已经在最后一页时直接关窗。
            case Key.Space when AtLast:
                e.Handled = true; Close(); break;
            case Key.Space:
                e.Handled = true; await StepAsync(1); break;
            case Key.Home:
                e.Handled = true; _index = 0; await ShowCurrentAsync(); break;
            case Key.End:
                e.Handled = true;
                _index = _doublePage ? Math.Max(0, _files.Count - _doubleStep) : _files.Count - 1;
                await ShowCurrentAsync();
                break;
            case Key.A when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0:
                e.Handled = true; ToggleAuto(); break;
            case Key.Escape:
                e.Handled = true; Close(); break;
            // Ctrl+W 关窗，和大多数看图/浏览器一致。
            case Key.W when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true; Close(); break;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await StepAsync(1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
