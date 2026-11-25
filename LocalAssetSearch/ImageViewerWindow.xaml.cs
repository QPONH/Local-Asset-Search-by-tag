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
/// 三种显示方式：单页 / 双页 / 竖排。
/// 双页是把两张图放进同一个缩放容器里当一张看，放大时整体缩放，不会互相遮挡。
/// 竖排是所有图片上下排开向下滚动，自动翻页在这个模式下变成自动下滑。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private enum WheelMode { Page, Zoom, Interval }
    private enum ViewerLayout { Single, Double, Vertical }

    private readonly List<string> _files;
    private readonly string _folderPath;
    private bool _historyOpenRecorded;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _gifTimer = new();
    private readonly DispatcherTimer _scrollTimer = new();   // 竖排：自动下滑
    private readonly DispatcherTimer _zoomTimer = new();     // 单页/双页：平滑缩放
    private readonly DispatcherTimer _verticalZoomTimer = new(); // 竖排：平滑缩放
    private int _index;
    private int _loadVersion;
    private int _verticalVersion;
    private bool _initializing = true;     // XAML 里设置选中项/文字也会触发事件，先挡住
    private bool _timerAdvancing;
    private double _intervalSeconds = 3.0;

    private ViewerLayout _layout = ViewerLayout.Single;
    private ViewerLayout _layoutBeforeVertical = ViewerLayout.Single;
    private int _doubleStep = 2;
    private double _gap = 12;
    private double _scrollSpeed = 60;
    private double _verticalZoom = 1.0;
    private double _verticalZoomTarget = 1.0;

    // F11 全屏：藏边框 + 最大化；再按一次恢复成普通窗口。
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Maximized;

    // GIF 动画：WPF 的 Image 不会自己播放 GIF，这里按帧延迟在 UI 线程上逐帧换图。
    private List<BitmapSource>? _gifFrames;
    private List<int>? _gifDelays;
    private int _gifIndex;
    private int _gifFrameCount;

    // 滚轮模式：翻页 / 缩放 / 调自动翻页间隔；缩放后左键拖动平移。
    private WheelMode _wheelMode = WheelMode.Page;
    private double _zoomScale = 1.0;
    private double _zoomTarget = 1.0;
    private Point _zoomAnchor;
    private Point _zoomOffset;
    private bool _panning;
    private bool _panMoved;
    private bool _suppressClick;
    private Point _panStart;
    private Point _panOrigin;

    // 竖排模式：每张图一个 Image，全部按原始顺序上下排开。
    private readonly List<Image> _verticalImages = new();
    private readonly ScaleTransform _verticalScale = new(1, 1);

    // 侧边栏打标签：读库、写库都即时完成，不需要点「应用」。
    private readonly Database _db = new();
    private bool _tagSidebarVisible;
    private bool _tagSidebarLoading;

    // 自检用：确认计时器、翻页、GIF、滚轮和布局状态。
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
    internal bool DoublePageForTest => _layout == ViewerLayout.Double;
    internal bool VerticalModeForTest => _layout == ViewerLayout.Vertical;
    internal int DoubleStepForTest => _doubleStep;
    internal double ZoomScaleForTest => _zoomScale;
    internal double ZoomTargetForTest => _zoomTarget;
    internal double VerticalZoomForTest => _verticalZoom;
    internal bool TagSidebarVisibleForTest => _tagSidebarVisible;
    internal int VerticalImageCountForTest => _verticalImages.Count;
    internal double GapForTest => _gap;
    internal double ScrollSpeedForTest => _scrollSpeed;

    public ImageViewerWindow(string folderPath)
    {
        InitializeComponent();
        WindowSizeStore.Restore(this, "viewer");
        WindowSizeStore.Track(this, "viewer");
        // 打开一律最大化；全屏/还原时用 RestoreBounds 回到普通尺寸。
        WindowState = WindowState.Maximized;
        Viewport.SizeChanged += (_, _) => UpdatePageSizes();

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        Title = string.IsNullOrEmpty(name) ? "看图" : $"{name} — 看图";

        _folderPath = folderPath;
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

        _layout = Settings.Get("viewer.layout", "single") switch
        {
            "double" => ViewerLayout.Double,
            "vertical" => ViewerLayout.Vertical,
            _ => ViewerLayout.Single,
        };
        _doubleStep = Settings.Get("viewer.doubleStep", "2") == "1" ? 1 : 2;
        _gap = ReadNumber(Settings.Get("viewer.gap", "12"), 12, 0, 200);
        _scrollSpeed = ReadNumber(Settings.Get("viewer.scrollSpeed", "60"), 60, 5, 2000);
        _verticalZoom = ReadNumber(Settings.Get("viewer.verticalZoom", "1"), 1, 0.5, 5);
        _verticalZoomTarget = _verticalZoom;

        DoublePageCheck.IsChecked = _layout == ViewerLayout.Double;
        VerticalModeCheck.IsChecked = _layout == ViewerLayout.Vertical;
        DoubleStepBox.SelectedIndex = _doubleStep == 1 ? 0 : 1;
        GapBox.Text = _gap.ToString("0.#", CultureInfo.InvariantCulture);
        UpdatePagesLayout();

        _timer.Tick += Timer_Tick;
        _gifTimer.Tick += Gif_Tick;
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _scrollTimer.Tick += ScrollTimer_Tick;
        _zoomTimer.Interval = TimeSpan.FromMilliseconds(16);
        _zoomTimer.Tick += ZoomTimer_Tick;
        _verticalZoomTimer.Interval = TimeSpan.FromMilliseconds(16);
        _verticalZoomTimer.Tick += VerticalZoomTimer_Tick;

        Closed += (_, _) =>
        {
            _timer.Stop();
            _gifTimer.Stop();
            _scrollTimer.Stop();
            _zoomTimer.Stop();
            if (_historyOpenRecorded) ViewerHistory.AddClose(_folderPath);
        };
        Loaded += async (_, _) =>
        {
            if (!_historyOpenRecorded)
            {
                _historyOpenRecorded = true;
                ViewerHistory.AddOpen(_folderPath);
            }
            if (_layout == ViewerLayout.Vertical) BuildVerticalAsync();
            else await ShowCurrentAsync();
        };
        // 竖排专属：下拉速度用上方同一个输入框（单位切换成 px/s）。
        _initializing = false;
        UpdatePagesLayout();
        UpdateIntervalUi();
    }

    private static double ReadNumber(string raw, double fallback, double min, double max)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max)
            : fallback;

    // ---------- 布局：单页 / 双页 / 竖排 ----------

    private void UpdatePagesLayout()
    {
        var vertical = _layout == ViewerLayout.Vertical;
        var doublePage = _layout == ViewerLayout.Double;

        Viewport.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalViewer.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;

        // 双页时显示右图；两张图并排放在同一个 PageHost 里，缩放是整体施加的。
        PictureRight.Visibility = doublePage ? Visibility.Visible : Visibility.Collapsed;

        DoubleStepBox.IsEnabled = doublePage;
        // 「间隔」双页管水平、竖排管纵向，单页时没有意义。
        var showGap = vertical || doublePage;
        GapLabel.Visibility = showGap ? Visibility.Visible : Visibility.Collapsed;
        GapBox.Visibility = GapLabel.Visibility;

        // 侧边栏收起时才显示内容区左上角的「标签 ▸」把手。
        TagSidebarButton.Visibility = _tagSidebarVisible ? Visibility.Collapsed : Visibility.Visible;

        UpdatePageSizes();
        ApplyVerticalZoom();
        if (!doublePage) PictureRight.Source = null;
    }

    /// <summary>上方那个输入框：普通模式是「秒」，竖排模式变成「px/s」下滑速度。</summary>
    private void UpdateIntervalUi()
    {
        _initializing = true;
        if (_layout == ViewerLayout.Vertical)
        {
            IntervalBox.Text = _scrollSpeed.ToString("0.#", CultureInfo.InvariantCulture);
            IntervalUnit.Text = "px/s";
        }
        else
        {
            IntervalBox.Text = _intervalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            IntervalUnit.Text = "秒";
        }
        _initializing = false;
    }

    /// <summary>
    /// 单页/双页的尺寸约束：图片按内容宽高比缩放到视口内。
    /// 双页时按内容宽度并排，宽度上限按可用宽度分给两张图——间隔为 0 时左右真正贴上，
    /// 而不是各自在半格子里居中、中间留出一片空白。
    /// </summary>
    private void UpdatePageSizes()
    {
        var w = Viewport.ActualWidth;
        var h = Viewport.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var doublePage = _layout == ViewerLayout.Double;
        var available = Math.Max(0, w - 16);
        var each = doublePage ? Math.Max(0, (available - _gap) / 2) : available;
        var heightLimit = Math.Max(0, h - 16);

        // 固定高度 + 最大宽度：小图也会放大到可用高度（保持"比框小的图也放大"），
        // 超宽的图先撞到 MaxWidth，再按比例缩小，不会溢出。
        Picture.Height = heightLimit;
        Picture.MaxWidth = each;
        PictureRight.Height = heightLimit;
        PictureRight.MaxWidth = each;

        var half = doublePage ? _gap / 2 : 8;
        Picture.Margin = new Thickness(8, 8, doublePage ? half : 8, 8);
        PictureRight.Margin = new Thickness(doublePage ? half : 8, 8, 8, 8);
    }

    /// <summary>竖排整体放大：LayoutTransform 会改变布局尺寸，滚动条跟着变长。</summary>
    private void ApplyVerticalZoom()
    {
        _verticalScale.ScaleX = _verticalZoom;
        _verticalScale.ScaleY = _verticalZoom;
        VerticalPanel.LayoutTransform = _verticalScale;
    }

    /// <summary>自检用：直接设定竖排倍率（Ctrl+滚轮不好在自动化里模拟）。</summary>
    internal void SetVerticalZoomForTest(double zoom)
    {
        _verticalZoomTarget = Math.Clamp(zoom, 0.5, 5);
        _verticalZoom = _verticalZoomTarget;
        ApplyVerticalZoom();
    }

    private void ApplyLayout()
    {
        if (_layout == ViewerLayout.Vertical)
        {
            _timer.Stop();
            if (AutoPageCheck.IsChecked == true) _scrollTimer.Start();
        }
        else
        {
            _scrollTimer.Stop();
        }

        UpdatePagesLayout();
        UpdateIntervalUi();
        if (_layout == ViewerLayout.Vertical) BuildVerticalAsync();
        else _ = ShowCurrentAsync();
        UpdateInfo();
    }

    private void DoublePage_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (DoublePageCheck.IsChecked == true)
        {
            if (VerticalModeCheck.IsChecked == true)
            {
                _initializing = true;
                VerticalModeCheck.IsChecked = false;
                _initializing = false;
            }
            _layout = ViewerLayout.Double;
        }
        else if (VerticalModeCheck.IsChecked != true)
        {
            _layout = ViewerLayout.Single;
        }
        Settings.Set("viewer.layout", _layout == ViewerLayout.Double ? "double" : "single");
        ApplyLayout();
    }

    private void VerticalMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (VerticalModeCheck.IsChecked == true)
        {
            // 记住进来之前的模式，退出竖排时还原（双页进的就回双页）。
            if (_layout != ViewerLayout.Vertical) _layoutBeforeVertical = _layout;
            if (DoublePageCheck.IsChecked == true)
            {
                _initializing = true;
                DoublePageCheck.IsChecked = false;
                _initializing = false;
            }
            _layout = ViewerLayout.Vertical;
        }
        else
        {
            _layout = _layoutBeforeVertical == ViewerLayout.Vertical ? ViewerLayout.Single : _layoutBeforeVertical;
            _initializing = true;
            DoublePageCheck.IsChecked = _layout == ViewerLayout.Double;
            _initializing = false;
        }
        Settings.Set("viewer.layout", _layout switch
        {
            ViewerLayout.Vertical => "vertical",
            ViewerLayout.Double => "double",
            _ => "single",
        });
        ApplyLayout();
    }

    private void DoubleStep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _doubleStep = DoubleStepBox.SelectedIndex == 0 ? 1 : 2;
        Settings.Set("viewer.doubleStep", _doubleStep.ToString(CultureInfo.InvariantCulture));
        UpdateInfo();
    }

    private void Gap_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (!double.TryParse(GapBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
        _gap = Math.Clamp(v, 0, 200);
        Settings.Set("viewer.gap", _gap.ToString("0.#", CultureInfo.InvariantCulture));
        foreach (var img in _verticalImages) img.Margin = new Thickness(0, 0, 0, _gap);
        UpdatePageSizes();
    }

    // ---------- 竖排：把全部图片排成一列 ----------

    private async void BuildVerticalAsync()
    {
        var version = ++_verticalVersion;
        VerticalPanel.Children.Clear();
        _verticalImages.Clear();

        if (_files.Count == 0)
        {
            VerticalPanel.Children.Add(new TextBlock
            {
                Text = "这个文件夹里没有可以显示的图片。",
                Foreground = ThemeManager.MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0),
            });
            return;
        }

        foreach (var _ in _files)
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, _gap),
            };
            _verticalImages.Add(img);
            VerticalPanel.Children.Add(img);
        }

        // 一张一张解码，避免一次开几十个文件流把磁盘打满；离开这个模式后丢弃结果。
        for (var i = 0; i < _files.Count; i++)
        {
            var path = _files[i];
            try
            {
                if (IsGif(path))
                {
                    var gif = await Task.Run(() => DecodeGif(path));
                    if (version != _verticalVersion) return;
                    _verticalImages[i].Source = gif.Frames.Count > 0 ? gif.Frames[0] : null;
                }
                else
                {
                    var bitmap = await Task.Run(() => Decode(path));
                    if (version != _verticalVersion) return;
                    _verticalImages[i].Source = bitmap;
                }
            }
            catch
            {
                if (version != _verticalVersion) return;
            }
        }
    }

    private void VerticalViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+滚轮：平滑缩放整列（无极，和单页缩放同一套思路）。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            _verticalZoomTarget = Math.Clamp(_verticalZoomTarget * Math.Pow(1.08, e.Delta / 120.0), 0.5, 5);
            // 同样限制"欠账"：目标最多领先当前值 40%，松手后马上停住。
            _verticalZoomTarget = Math.Clamp(_verticalZoomTarget, _verticalZoom / 1.4, _verticalZoom * 1.4);
            _verticalZoomTarget = Math.Clamp(_verticalZoomTarget, 0.5, 5);
            if (!_verticalZoomTimer.IsEnabled) _verticalZoomTimer.Start();
            return;
        }

        // 竖排是滚动浏览，滚轮直接按 delta 线性滚（触控板小 delta 也连续跟随）。
        if (VerticalViewer.ScrollableHeight <= 0) return;
        e.Handled = true;
        var target = VerticalViewer.VerticalOffset - e.Delta * 0.9;
        VerticalViewer.ScrollToVerticalOffset(Math.Clamp(target, 0, VerticalViewer.ScrollableHeight));
    }

    private void VerticalZoomTimer_Tick(object? sender, EventArgs e)
    {
        var diff = _verticalZoomTarget - _verticalZoom;
        if (Math.Abs(diff) < 0.0008)
        {
            _verticalZoom = _verticalZoomTarget;
            ApplyVerticalZoom();
            _verticalZoomTimer.Stop();
            Settings.Set("viewer.verticalZoom", _verticalZoom.ToString("0.##", CultureInfo.InvariantCulture));
            return;
        }
        var step = Math.Clamp(diff * 0.6, -0.03, 0.03);
        if (Math.Abs(diff) < 0.002) step = diff;
        _verticalZoom += step;
        ApplyVerticalZoom();
    }

    private void ScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_layout != ViewerLayout.Vertical || AutoPageCheck.IsChecked != true)
        {
            _scrollTimer.Stop();
            return;
        }

        var next = VerticalViewer.VerticalOffset + _scrollSpeed * _scrollTimer.Interval.TotalSeconds;
        if (next >= VerticalViewer.ScrollableHeight)
        {
            VerticalViewer.ScrollToVerticalOffset(VerticalViewer.ScrollableHeight);
            // 滑到底就停下，不再静默地空转。
            _scrollTimer.Stop();
            _initializing = true;
            AutoPageCheck.IsChecked = false;
            _initializing = false;
            InfoText.Text = "已滑到底。";
            return;
        }
        VerticalViewer.ScrollToVerticalOffset(next);
    }

    // ---------- 标签侧边栏 ----------

    private void ToggleTagSidebar_Click(object sender, RoutedEventArgs e)
    {
        _tagSidebarVisible = !_tagSidebarVisible;
        TagSidebar.Visibility = _tagSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        TagSidebarButton.Visibility = _tagSidebarVisible ? Visibility.Collapsed : Visibility.Visible;
        if (_tagSidebarVisible) BuildTagSidebar();
    }

    /// <summary>侧边栏里的标签列表，按分类分组；勾选/取消即时写库，不用再点应用。</summary>
    private void BuildTagSidebar()
    {
        _tagSidebarLoading = true;
        try
        {
            TagSidebarPanel.Children.Clear();
            var assetId = _db.GetAssetIdByPath(_folderPath);
            if (assetId is null)
            {
                TagSidebarPanel.Children.Add(new TextBlock
                {
                    Text = "这个文件夹还没有被索引，先在主窗口扫描一次再来打标签。",
                    Foreground = ThemeManager.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            var current = _db.GetAssetTagNames(assetId.Value);
            var tags = _db.GetTags();
            if (tags.Count == 0)
            {
                TagSidebarPanel.Children.Add(new TextBlock
                {
                    Text = "还没有任何标签，先在主窗口的「标签管理」里新建。",
                    Foreground = ThemeManager.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (var group in tags.GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? TagCategory.Unclassified : t.Category))
            {
                var content = new StackPanel();
                foreach (var tag in group)
                {
                    var name = tag.Name;
                    var cb = new CheckBox
                    {
                        Content = name,
                        IsChecked = current.Contains(name),
                        Margin = new Thickness(4, 3, 4, 3),
                        ToolTip = name,
                    };
                    cb.Checked += (_, _) => ApplySidebarTag(assetId.Value, name, add: true);
                    cb.Unchecked += (_, _) => ApplySidebarTag(assetId.Value, name, add: false);
                    content.Children.Add(cb);
                }

                // 作者组是自动建的、通常很长，默认收起来；其他组默认展开。
                var collapsed = TagGroupView.IsAuthorGroup(group.Key);
                TagGroupView.AddCollapsible(TagSidebarPanel, group.Key, collapsed, content);
            }
        }
        finally { _tagSidebarLoading = false; }
    }

    private void ApplySidebarTag(long assetId, string tagName, bool add)
    {
        if (_tagSidebarLoading) return;
        try
        {
            _db.ApplyTag(assetId, tagName, remove: !add);
        }
        catch (Exception ex)
        {
            InfoText.Text = $"标签保存失败：{ex.Message}";
        }
    }

    // ---------- 显示 ----------

    private void UpdateInfo()
    {
        if (_files.Count == 0)
        {
            InfoText.Text = "这个文件夹里没有可以显示的图片。";
            return;
        }

        if (_layout == ViewerLayout.Vertical)
        {
            var vertical = $"{_files.Count} 张图片　·　竖排";
            if (AutoPageCheck.IsChecked == true) vertical += $"　·　自动下滑（{_scrollSpeed:0.#} px/秒）";
            InfoText.Text = vertical;
            return;
        }

        var hasRight = _layout == ViewerLayout.Double && _index + 1 < _files.Count;
        var line = hasRight
            ? $"{_index + 1}–{_index + 2} / {_files.Count}　{Path.GetFileName(_files[_index])}  |  {Path.GetFileName(_files[_index + 1])}"
            : $"{_index + 1} / {_files.Count}　{Path.GetFileName(_files[_index])}";
        if (_gifFrameCount > 1) line += $"　·　GIF 动画（{_gifFrameCount} 帧）";
        if (_timer.IsEnabled) line += $"　·　自动翻页（每 {_intervalSeconds:0.#} 秒）";
        InfoText.Text = line;
    }

    private async Task ShowCurrentAsync()
    {
        if (_layout == ViewerLayout.Vertical) return;
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
        var rightPath = _layout == ViewerLayout.Double && _index + 1 < _files.Count ? _files[_index + 1] : null;
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
        if (_layout != ViewerLayout.Double || path is null)
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

    private int PageStep => _layout == ViewerLayout.Double ? _doubleStep : 1;
    private bool AtLast => _files.Count == 0 || _index + PageStep >= _files.Count;

    private async Task StepAsync(int delta)
    {
        if (_files.Count == 0) return;
        if (_layout == ViewerLayout.Vertical)
        {
            ScrollVertical(delta > 0 ? 1 : -1);
            return;
        }

        var step = _layout == ViewerLayout.Double ? _doubleStep : 1;
        var next = _index + (delta < 0 ? -step : step);
        next = Math.Clamp(next, 0, _files.Count - 1);
        if (next == _index) return;
        _index = next;
        await ShowCurrentAsync();
        // 手动翻页后把自动翻页的倒计时重新算起：否则刚点完下一张，
        // 旧计时器可能马上到点又翻一张，看起来像连跳两页。
        RestartAutoTimer();
    }

    /// <summary>竖排模式下翻页 = 滚动一屏的 85%。</summary>
    private void ScrollVertical(int direction)
    {
        var amount = Math.Max(120, VerticalViewer.ViewportHeight * 0.85) * direction;
        var target = Math.Clamp(VerticalViewer.VerticalOffset + amount, 0, VerticalViewer.ScrollableHeight);
        VerticalViewer.ScrollToVerticalOffset(target);
    }

    // ---------- 自动翻页 / 自动下滑 ----------

    private void AutoPage_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (AutoPageCheck.IsChecked == true)
        {
            if (_layout == ViewerLayout.Vertical)
            {
                _timer.Stop();
                _scrollTimer.Start();
            }
            else
            {
                _scrollTimer.Stop();
                if (!TryReadInterval(out var seconds)) seconds = _intervalSeconds;
                SetInterval(seconds, updateText: true);
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            _scrollTimer.Stop();
        }
        UpdateInfo();
    }

    private void Interval_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;

        // 竖排模式下这个框是「下滑速度（px/s）」，不再是秒数。
        if (_layout == ViewerLayout.Vertical)
        {
            if (!double.TryParse(IntervalBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) return;
            _scrollSpeed = Math.Clamp(speed, 5, 2000);
            Settings.Set("viewer.scrollSpeed", _scrollSpeed.ToString("0.#", CultureInfo.InvariantCulture));
            UpdateInfo();
            return;
        }

        if (TryReadInterval(out var seconds)) SetInterval(seconds, updateText: false);
    }

    /// <summary>竖排模式下用上下方向键调下滑速度。</summary>
    private void AdjustScrollSpeed(double delta)
    {
        _scrollSpeed = Math.Clamp(_scrollSpeed + delta, 5, 2000);
        Settings.Set("viewer.scrollSpeed", _scrollSpeed.ToString("0.#", CultureInfo.InvariantCulture));
        _initializing = true;
        IntervalBox.Text = _scrollSpeed.ToString("0.#", CultureInfo.InvariantCulture);
        _initializing = false;
        UpdateInfo();
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
            _initializing = true;
            if (IntervalBox.Text != text) IntervalBox.Text = text;
            _initializing = false;
        }
        Settings.Set("viewer.interval", seconds.ToString("0.#", CultureInfo.InvariantCulture));
        UpdateInfo();
    }

    /// <summary>A 键：开 / 关自动翻页（竖排模式下是自动下滑）。</summary>
    private void ToggleAuto() => AutoPageCheck.IsChecked = !(_timer.IsEnabled || _scrollTimer.IsEnabled);

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
        if (_layout == ViewerLayout.Vertical)
        {
            ScrollVertical(1);
            if (VerticalViewer.VerticalOffset >= VerticalViewer.ScrollableHeight - 1) Close();
            return;
        }

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
        // 下方工具栏空白处、信息栏、竖排滚动视图的点击都不翻页；只有图片区域才响应。
        if (!Viewport.IsMouseOver) return;
        if (_layout == ViewerLayout.Vertical) return;

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

    // ---------- 滚轮：翻页 / 缩放 / 调速 ----------

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

        if (_layout == ViewerLayout.Vertical) return;

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

        // 线性放大，不按 120 取整：1 个 delta 单位 = 0.0015 倍（鼠标一格约 0.18 倍）。
        // 目标最多领先当前值 0.25 倍——不然连续滑动时目标会攒下一大堆"欠账"，松手后还要继续放大很久。
        _zoomAnchor = e.GetPosition(Viewport);
        _zoomTarget = Math.Clamp(_zoomTarget + e.Delta * 0.0015, 1.0, 6.0);
        _zoomTarget = Math.Clamp(_zoomTarget, _zoomScale - 0.25, _zoomScale + 0.25);
        _zoomTarget = Math.Clamp(_zoomTarget, 1.0, 6.0);
        if (_zoomTarget <= 1.0 && _zoomScale <= 1.0001)
        {
            ResetZoom();
            return;
        }
        EnsureZoomTimer();
    }

    private void EnsureZoomTimer()
    {
        if (!_zoomTimer.IsEnabled) _zoomTimer.Start();
    }

    /// <summary>平滑缩放：每帧把当前比例往目标推一段，同时让锚点（鼠标位置）始终不动。</summary>
    private void ZoomTimer_Tick(object? sender, EventArgs e)
    {
        var diff = _zoomTarget - _zoomScale;
        if (Math.Abs(diff) < 0.0008)
        {
            _zoomScale = _zoomTarget;
            if (_zoomScale <= 1.0005)
            {
                ResetZoom();
                return;
            }
            ApplyZoom();
            _zoomTimer.Stop();
            return;
        }

        // 追得快一点：差距大时每帧最多 3%，接近目标时按 60% 收敛，整体几帧就到，
        // 避免"后半段"拖得很慢。最后不到 0.002 时直接补齐，不来回抖。
        var step = Math.Clamp(diff * 0.6, -0.03, 0.03);
        if (Math.Abs(diff) < 0.002) step = diff;
        var newScale = _zoomScale + step;
        var localX = (_zoomAnchor.X - _zoomOffset.X) / _zoomScale;
        var localY = (_zoomAnchor.Y - _zoomOffset.Y) / _zoomScale;
        _zoomScale = newScale;
        _zoomOffset = new Point(_zoomAnchor.X - localX * newScale, _zoomAnchor.Y - localY * newScale);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        PictureScale.ScaleX = _zoomScale;
        PictureScale.ScaleY = _zoomScale;
        PictureTranslate.X = _zoomOffset.X;
        PictureTranslate.Y = _zoomOffset.Y;
        UpdateZoomCursor();
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
        ApplyZoom();
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
    /// 计算缩放后的平移量，保证 local（相对图片布局原点）处的画面点不动。
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

    private void ResetZoom()
    {
        _zoomTimer.Stop();
        _zoomScale = 1.0;
        _zoomTarget = 1.0;
        _zoomOffset = default;
        _panning = false;
        _panMoved = false;
        _suppressClick = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        ApplyZoom();
    }

    private void UpdateZoomCursor()
    {
        Viewport.Cursor = _wheelMode == WheelMode.Zoom && _zoomScale > 1.001 ? Cursors.SizeAll : Cursors.Arrow;
    }

    /// <summary>自检用：F11 全屏不好在自动化里按键，直接驱动。</summary>
    internal void ToggleFullScreenForTest() => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullScreen;
            _fullScreen = false;
        }
        UpdatePageSizes();
    }

    // ---------- 键盘 ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入间隔时，方向键/空格/字母都留给输入框；Esc、F11 和 Ctrl+W 仍可用。
        if (Keyboard.FocusedElement is TextBox &&
            e.Key != Key.Escape &&
            e.Key != Key.F11 &&
            !(e.Key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            return;

        // 竖排模式：上下键调自动下滑速度，PageUp/PageDown/空格滚动。
        if (_layout == ViewerLayout.Vertical)
        {
            switch (e.Key)
            {
                case Key.Up:
                    e.Handled = true; AdjustScrollSpeed(-10); return;
                case Key.Down:
                    e.Handled = true; AdjustScrollSpeed(+10); return;
                case Key.PageUp:
                    e.Handled = true; ScrollVertical(-1); return;
                case Key.PageDown:
                case Key.Space:
                    e.Handled = true; ScrollVertical(1); return;
                case Key.Home:
                    e.Handled = true; VerticalViewer.ScrollToTop(); return;
                case Key.End:
                    e.Handled = true; VerticalViewer.ScrollToBottom(); return;
            }
        }

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
                _index = _layout == ViewerLayout.Double ? Math.Max(0, _files.Count - _doubleStep) : _files.Count - 1;
                await ShowCurrentAsync();
                break;
            case Key.A when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0:
                e.Handled = true; ToggleAuto(); break;
            case Key.Escape:
                e.Handled = true; Close(); break;
            // F11 全屏 / 还原。
            case Key.F11:
                e.Handled = true; ToggleFullScreen(); break;
            // Ctrl+W 关窗，和大多数看图/浏览器一致。
            case Key.W when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true; Close(); break;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await StepAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await StepAsync(1);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
