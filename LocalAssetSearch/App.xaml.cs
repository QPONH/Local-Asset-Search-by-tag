using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "?";
        StartupLog.Write($"==== 启动 v{version} ====");
        StartupLog.Write($"exe      : {Environment.ProcessPath}");
        StartupLog.Write($"数据目录 : {Database.DefaultDataDirectory}");
        StartupLog.Write($"日志     : {StartupLog.LogPath}");

        // 兜底：任何没被捕获的异常都要留下痕迹，而不是让进程悄悄消失。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, "后台线程未处理异常");

        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, "界面线程未处理异常");
            args.Handled = true;
        };

        base.OnStartup(e);

        RegisterGlobalInputBehavior();

        // 主题要在建窗口之前定下来，否则会先闪一下浅色。
        ThemeManager.LoadPreference();

        // 隐藏的自检开关：只构造各个窗口、不显示。
        // 自动化环境里没法真的点界面，这是唯一能验证「每个窗口的 XAML 都能加载」的手段。
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // 自检要等消息循环转起来才能跑：主窗口构造函数里的搜索是异步的，
            // 只有它跑完、条目进了列表，卡片的 DataTemplate 才会真正被实例化出来。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(new Action(async () => Shutdown(await RunSelfTestAsync())),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 不用 App.xaml 的 StartupUri：那样窗口构造失败会直接冲出 Run()，
        // 连一句错误提示都留不下。这里手动建窗口才接得住异常。
        try
        {
            var win = new MainWindow();
            win.Show();
            StartupLog.Write($"主窗口已显示，启动完成。实际数据目录：{Database.DataDirectory}");

            // 数据目录被回退过就必须让用户知道：他看到的会是一个空库。
            if (Database.DataDirectoryNotice is { } notice)
            {
                StartupLog.Write("数据目录已回退：" + notice.Replace("\r", " ").Replace("\n", " "));
                MessageBox.Show(notice, "本地素材搜索 — 数据目录已更改", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Report(ex, "启动失败");
            Shutdown(1);
        }
    }

    /// <summary>
    /// 下拉框的选项不允许用方向键改；点击输入框以外的空白处时，
    /// 取消文字选中并让输入框失焦（文本内容保留，绑定/TextChanged 会保存）。
    /// </summary>
    private static void RegisterGlobalInputBehavior()
    {
        EventManager.RegisterClassHandler(typeof(ComboBox), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(ComboBox_PreviewKeyDown));
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(Window_PreviewMouseDown));
    }

    private static void ComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var blocked = e.Key is Key.Up or Key.Down ||
                      (!combo.IsEditable && (e.Key is Key.Left or Key.Right));
        if (blocked) e.Handled = true;
    }

    private static void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<TextBoxBase>(source) is not null) return;
        if (Keyboard.FocusedElement is not TextBox textBox) return;

        textBox.Select(0, 0);
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// 依次构造每个窗口并让主窗口真正布局一次，返回失败个数（0 表示全部通过）。
    ///
    /// 用临时数据目录并造几个文件夹，这样卡片模板（含右键菜单）会被真的实例化——
    /// 只构造窗口不显示的话，DataTemplate 根本不会跑，等于没验证。
    /// </summary>
    private static async Task<int> RunSelfTestAsync()
    {
        StartupLog.Write("---- 自检模式 ----");
        int failures = 0;
        var temp = Path.Combine(Path.GetTempPath(), "las-selftest-" + Guid.NewGuid().ToString("N"));

        void Try(string name, Action action)
        {
            try
            {
                action();
                StartupLog.Write($"自检 {name}: 通过");
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 {name}: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 数据目录必须“不随 exe 走”：否则删掉/重新下载 exe 后标签和索引就没了。
        Try("数据目录不跟随 exe", () =>
        {
            var dir = Database.ResolveDataDirectory();
            StartupLog.Write($"自检 数据目录: {dir}");
            if (dir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("数据目录不能放在 exe 同目录：删除 exe 会丢标签");
        });

        try
        {
            Directory.CreateDirectory(temp);
            Database.OverrideDataDirectory(temp);

            var seed = Path.Combine(temp, "示例库");
            Directory.CreateDirectory(Path.Combine(seed, "甲{作者}{1,3}"));
            Directory.CreateDirectory(Path.Combine(seed, "乙{作者}{1,2}"));
            // 多造一些文件夹，主窗口超过一页，才能验证「刷新后不跳回顶部」。
            for (int i = 0; i < 70; i++) Directory.CreateDirectory(Path.Combine(seed, $"批量{i:00}"));

            // 造一个带真图片的文件夹，看图窗口才有东西可加载。
            var pics = Path.Combine(seed, "有图{a}{1,1}");
            Directory.CreateDirectory(pics);
            for (int i = 1; i <= 3; i++) WriteTinyPng(Path.Combine(pics, $"{i}.png"), 40, 60, (byte)(i * 60), 90, 170);

            // 2 帧、每帧 100ms 的 8×8 动画 GIF（base64 直接写出来，避免测试依赖外部文件）。
            var gifPath = Path.Combine(temp, "动画.gif");
            File.WriteAllBytes(gifPath, Convert.FromBase64String(
                "R0lGODlhCAAIAIEAANweHgAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAACAAIAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBCgABACwAAAAACAAIAIEetFAAAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAA7"));

            Database? db = null;
            var sample = new List<AssetItem>();
            Try("打开数据库", () => db = new Database());
            if (db is null) { StartupLog.Write("自检中止：数据库打不开"); return failures + 1; }
            StartupLog.Write($"自检 数据目录: {Database.DataDirectory}");

            Try("读取标签", () => db!.GetTags());
            Try("扫描示例目录", () => db!.AddRootAndScan(seed));
            Try("读取素材", () => sample = db!.Search("", Array.Empty<long>(), true));
            StartupLog.Write($"自检 素材样本数: {sample.Count}");

            // 随机排序：同种子结果稳定（分页不会重复/漏），换种子顺序会变。
            Try("随机排序分页稳定且可重新洗牌", () =>
            {
                var page1 = db!.SearchPage("", Array.Empty<long>(), true, SortMode.Random, 0, 10, 12345).Items;
                var page1Again = db!.SearchPage("", Array.Empty<long>(), true, SortMode.Random, 0, 10, 12345).Items;
                var page2 = db!.SearchPage("", Array.Empty<long>(), true, SortMode.Random, 10, 10, 12345).Items;
                var shuffled = db!.SearchPage("", Array.Empty<long>(), true, SortMode.Random, 0, 10, 999).Items;

                if (!page1.Select(x => x.Id).SequenceEqual(page1Again.Select(x => x.Id)))
                    throw new InvalidOperationException("同一个随机种子两次查询顺序不一致");
                if (page1.Count > 1 && page1.Select(x => x.Id).SequenceEqual(shuffled.Select(x => x.Id)))
                    throw new InvalidOperationException("换种子后顺序没有变化");
                var ids = page1.Concat(page2).Select(x => x.Id).ToList();
                if (ids.Count != ids.Distinct().Count())
                    throw new InvalidOperationException("随机排序翻页出现重复条目");

                StartupLog.Write($"自检 随机排序: 第一页 {page1.Count} 条、第二页 {page2.Count} 条，换种子顺序已变");
            });

            // GIF：扩展名列表要认它，WIC 解码要能拆出两帧。
            Try("GIF 动图解码", () =>
            {
                var files = FolderCover.ListImages(temp);
                if (!files.Any(p => p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("ListImages 没有把 .gif 当图片");

                var decoded = ImageViewerWindow.DecodeGifForTest(gifPath);
                StartupLog.Write($"自检 GIF: {decoded.Frames.Count} 帧，延迟 {string.Join(",", decoded.Delays)}ms");
                if (decoded.Frames.Count != 2) throw new InvalidOperationException($"GIF 应该有 2 帧，实际 {decoded.Frames.Count}");
                if (decoded.Frames[0].PixelWidth != 8) throw new InvalidOperationException("GIF 帧尺寸不对");
            });

            // 真打开一个只有 GIF 的文件夹，确认计时器真的在换帧。
            try
            {
                var gifFolder = Path.Combine(seed, "动画{a}{1,1}");
                Directory.CreateDirectory(gifFolder);
                File.Copy(gifPath, Path.Combine(gifFolder, "1.gif"), true);

                var w = new ImageViewerWindow(gifFolder)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 480,
                    Height = 360,
                };
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    if (w.GifFrameCountForTest != 2) throw new InvalidOperationException($"GIF 帧数应该是 2，实际 {w.GifFrameCountForTest}");
                    if (!w.GifAnimatingForTest) throw new InvalidOperationException("GIF 计时器没有启动");
                    var start = w.GifFrameIndexForTest;
                    for (var i = 0; i < 20 && w.GifFrameIndexForTest == start; i++) await Task.Delay(50);
                    if (w.GifFrameIndexForTest == start) throw new InvalidOperationException("GIF 画面没有换帧");
                    StartupLog.Write($"自检 GIF 播放: 帧数={w.GifFrameCountForTest} 当前帧={w.GifFrameIndexForTest} 计时器={w.GifAnimatingForTest}");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 GIF 播放: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 库备份：导出 → 写 JSON → 读回 → 合并导入，路径/标签/关联都应在。
            Try("库备份导出与读取", () =>
            {
                db!.EnsureTag("自检标签", "自检分类");
                var target = sample.FirstOrDefault(x => x.Kind == "folder");
                if (target is not null) db!.ApplyTag(target.Id, "自检标签");

                var doc = LibraryArchive.Export(db!);
                if (doc.Folders.Count == 0) throw new InvalidOperationException("导出的文档里没有文件夹");
                if (doc.Tags.All(t => t.Name != "自检标签")) throw new InvalidOperationException("导出的文档里没有自检标签");

                var file = Path.Combine(temp, "selftest.laslib.json");
                LibraryArchive.Save(doc, file);
                var loaded = LibraryArchive.Load(file);
                var result = LibraryArchive.Import(db!, loaded);

                StartupLog.Write($"自检 库备份: 文件夹={loaded.Folders.Count} 标签={loaded.Tags.Count} " +
                                 $"新增={result.FoldersAdded} 更新={result.FoldersUpdated} 关联新增={result.LinksAdded}");
                if (result.FoldersAdded != 0) throw new InvalidOperationException("同一份文档导入同一库不应新增文件夹");
                if (result.FoldersUpdated != loaded.Folders.Count) throw new InvalidOperationException("更新数量与文档不一致");
                if (result.TagsAdded != 0) throw new InvalidOperationException("同一份文档导入同一库不应新增标签");
                if (result.LinksAdded != 0) throw new InvalidOperationException("同一份文档重复导入不应新增关联");
            });

            // 标签管理：改一行名称/分类后点右下角「保存」，确认一次性写入且窗口关闭。
            try
            {
                var tag = db!.GetTags().First();
                db.EnsureCategory("统一保存分类");
                var w = new TagManagerWindow(db)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                };
                try
                {
                    w.Show();
                    var row = w.RowPanel.Children.OfType<Grid>().First();
                    var nameBox = row.Children.OfType<TextBox>().First();
                    var categoryBox = row.Children.OfType<ComboBox>().First();
                    var newName = tag.Name + "改";
                    nameBox.Text = newName;
                    categoryBox.SelectedItem = "统一保存分类";
                    w.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    var saved = db.GetTags().FirstOrDefault(t => t.Id == tag.Id);
                    if (saved is null || saved.Name != newName || saved.Category != "统一保存分类")
                        throw new InvalidOperationException("统一保存没有写入改名/分类");
                    if (w.IsVisible) throw new InvalidOperationException("保存后窗口没有关闭");
                    StartupLog.Write($"自检 标签管理统一保存: {saved.Name} / {saved.Category}");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 标签管理统一保存: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 造几个同分类标签，专门验证左侧筛选是两列而不是一列。
            db!.EnsureTag("风景", "布局测试");
            db!.EnsureTag("人像", "布局测试");
            db!.EnsureTag("城市", "布局测试");

            // 主窗口：显示出来 + 等异步搜索完成 + 强制布局，卡片模板才会实例化。
            try
            {
                var win = new MainWindow();
                win.Show();
                await Task.Delay(2000);
                win.UpdateLayout();
                StartupLog.Write("自检 MainWindow: 通过（已显示并完成布局，卡片模板已实例化）");

                // 刷新后保持位置：先滚到中间，再按「关闭打标签窗口」的刷新路径重查一次。
                var scroll = win.ResultsScroll;
                scroll.ScrollToVerticalOffset(300);
                win.UpdateLayout();
                var before = scroll.VerticalOffset;
                await win.RefreshKeepingPositionForTestAsync();
                await Task.Delay(300);
                win.UpdateLayout();
                var after = scroll.VerticalOffset;
                StartupLog.Write($"自检 关闭标签后保持位置: before={before:0} after={after:0} extent={scroll.ExtentHeight:0} scrollable={scroll.ScrollableHeight:0} viewport={scroll.ViewportHeight:0}");
                if (before <= 0) throw new InvalidOperationException("结果区没有滚动，测试前提不成立");
                if (Math.Abs(after - before) > 2) throw new InvalidOperationException($"刷新后滚动位置变了：{before:0} → {after:0}");

                // 真去渲染出来的界面树里找「图片数量」那一行，而且要求它真的有尺寸、真的可见
                // ——元素存在不等于看得见，被压成 0 高或者被盖住都会「没看到显示」。
                var countBlocks = Descendants<TextBlock>(win)
                    .Where(t => t.Text.EndsWith("张图片", StringComparison.Ordinal) || t.Text == "无图片")
                    .ToList();
                StartupLog.Write("自检 数量行: " + string.Join("、", countBlocks.Select(t => $"{t.Text}({t.ActualWidth:0}×{t.ActualHeight:0} 可见={t.IsVisible})")));
                if (countBlocks.Count == 0) throw new InvalidOperationException("卡片上没有渲染出图片数量那一行");
                if (countBlocks.Any(t => !t.IsVisible || t.ActualWidth <= 0 || t.ActualHeight <= 0))
                    throw new InvalidOperationException("有数量行不可见或被压成了 0 尺寸");

                // 左侧标签筛选必须是两列：找那个装了 3 个以上标签的 UniformGrid。
                var tagGrid = Descendants<UniformGrid>(win).FirstOrDefault(g => g.Columns == 2 && g.Children.Count >= 3);
                if (tagGrid is null)
                    throw new InvalidOperationException("标签筛选没有按两列布局");
                StartupLog.Write($"自检 标签筛选两列: 列={tagGrid.Columns} 标签={tagGrid.Children.Count} 宽={tagGrid.ActualWidth:0}");

                // 卡片高度：封面 372 + 数量 22 + 标签区（至少 52）+ 标题 58，加上内外边距约 520。
                var card = Descendants<Border>(win).FirstOrDefault(b => Math.Abs(b.Width - 280) < 0.5 && b.ActualHeight > 200);
                if (card is not null)
                    StartupLog.Write($"自检 卡片高度: {card.ActualHeight:0}（标签区应至少 52，卡片约 520）");

                // 滚轮 / 触控板灵敏度：每个 delta 只走 0.7px，再由 60fps 定时器缓缓追上。
                // 这里连发 12 次 -10（合计 120 delta），期望平滑滚到 84px，而不是原来的一整屏。
                scroll.ScrollToTop();
                win.UpdateLayout();
                for (var i = 0; i < 12; i++)
                {
                    var tick = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -10)
                    {
                        RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    };
                    scroll.RaiseEvent(tick);
                }
                await Task.Delay(500);
                win.UpdateLayout();
                var wheelOffset = scroll.VerticalOffset;
                StartupLog.Write($"自检 列表平滑滚动: {wheelOffset:0.0}px（12×-10 delta，期望 84）");
                if (wheelOffset < 60) throw new InvalidOperationException($"滚轮没有滚动：{wheelOffset:0.0}px");
                if (wheelOffset > 110) throw new InvalidOperationException($"滚轮步长仍然太大：{wheelOffset:0.0}px");

                win.Close();
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 MainWindow: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 这几类控件 WPF 默认用系统主题模板，深色下背景不会跟着变，
            // 而文字已经被全局前景色染成浅色 —— 一旦漏掉某个隐式样式就是浅底浅字。这里守住。
            Try("深色适配所需的隐式样式都在", () =>
            {
                var need = new[]
                {
                    typeof(ContextMenu), typeof(MenuItem), typeof(Separator), typeof(ToolTip),
                    typeof(ComboBox), typeof(ComboBoxItem), typeof(TextBox), typeof(Button),
                    typeof(ScrollBar),
                };
                var missing = need.Where(t => !Application.Current.Resources.Contains(t)).Select(t => t.Name).ToList();
                if (missing.Count > 0) throw new InvalidOperationException("缺少隐式样式：" + string.Join("、", missing));
            });

            // 调色板本身也要能读：背景色和文字色的对比度太低，换了模板也看不清。
            Try("深浅两套配色的文字对比度", () =>
            {
                var wasDark = ThemeManager.IsDark;
                try
                {
                    foreach (var dark in new[] { false, true })
                    {
                        ThemeManager.Apply(dark);
                        var ratio = Contrast(ThemeManager.Brush("Brush.Surface"), ThemeManager.Brush("Brush.Text"));
                        var label = dark ? "深色" : "浅色";
                        StartupLog.Write($"自检 {label} Surface/Text 对比度: {ratio:0.0}:1");
                        if (ratio < 4.5) throw new InvalidOperationException($"{label}下对比度只有 {ratio:0.0}:1");
                    }
                }
                finally { ThemeManager.Apply(wasDark); }
            });

            // 标签芯片能不能放三行：卡片标签区 70px（可用内容 67px），用真实样式实测行高。
            Try("标签芯片三行高度", () =>
            {
                var chip = new Button
                {
                    Content = "四个字标签",
                    FontSize = 10,
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 3, 2),
                };
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var row = chip.DesiredSize.Height + chip.Margin.Bottom;
                var need = row * 3;
                StartupLog.Write($"自检 标签芯片行高: {row:0.0}px，三行={need:0.0}px（标签区最多 67px）");
                if (need > 67) throw new InvalidOperationException($"三行标签放不下：{need:0.0}px > 67px");

                // 标题区缩到 40px，两行作品名加内边距后还要放得下，不能再压。
                var title = new TextBlock
                {
                    Text = "很长很长的作品名称，需要两行才能完整显示，这里再补一些字凑满两行",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                };
                title.Measure(new Size(266, double.PositiveInfinity));
                var titleNeed = title.DesiredSize.Height + 6;
                StartupLog.Write($"自检 作品名两行高度: {title.DesiredSize.Height:0.0}px，加内边距={titleNeed:0.0}px（标题区 40px）");
                if (titleNeed > 40) throw new InvalidOperationException($"两行作品名放不下：{titleNeed:0.0}px > 40px");
            });

            // 卡片上要显示「N 张图片」，验证一次列目录能同时给出封面和数量。
            Try("封面与图片数量", () =>
            {
                var (cover, count) = FolderCover.CoverAndCount(pics);
                StartupLog.Write($"自检 封面={Path.GetFileName(cover)} 图片数量={count}");
                if (count != 3) throw new InvalidOperationException($"图片数量应该是 3，实际 {count}");
                if (Path.GetFileName(cover) != "1.png") throw new InvalidOperationException($"封面应该是 1.png，实际 {Path.GetFileName(cover)}");
            });

            // 看图窗口：验证 XAML 能加载、图片列表能列出来、窗口能正常构造。
            Try("ImageViewerWindow", () =>
            {
                var files = FolderCover.ListImages(pics);
                StartupLog.Write($"自检 看图窗口列出图片: {files.Count} 张");
                if (files.Count != 3) throw new InvalidOperationException($"应该列出 3 张，实际 {files.Count}");
                new ImageViewerWindow(pics).Close();
            });

            // 多开 + 不用 Owner：设了 Owner 的窗口永远盖在拥有者上面，Alt+Tab 切回工具窗口会被挡住，
            // 而且不进任务栏。这里把它钉住，免得以后有人「顺手」加回去。
            Try("看图窗口可多开且不设 Owner", () =>
            {
                var a = new ImageViewerWindow(pics);
                var b = new ImageViewerWindow(pics);
                try
                {
                    if (a.Owner is not null || b.Owner is not null)
                        throw new InvalidOperationException("看图窗口不应设置 Owner，否则会盖住主窗口、Alt+Tab 切不回去");
                    if (ReferenceEquals(a, b)) throw new InvalidOperationException("两次打开应该是两个独立窗口");
                }
                finally { a.Close(); b.Close(); }
            });

            // 自动翻页默认必须关着，否则一打开就自己翻页、跑到最后一页自己关掉。
            Try("自动翻页默认状态", () =>
            {
                var w = new ImageViewerWindow(pics);
                try
                {
                    if (w.AutoPageCheck.IsChecked == true) throw new InvalidOperationException("自动翻页默认必须关闭");
                    if (w.AutoPageEnabled) throw new InvalidOperationException("自动翻页默认不能启动计时器");
                    if (string.IsNullOrWhiteSpace(w.IntervalBox.Text)) throw new InvalidOperationException("间隔输入框没有默认值");
                    StartupLog.Write($"自检 自动翻页默认: interval={w.IntervalBox.Text}s");
                }
                finally { w.Close(); }
            });

            // 数字间隔：输入 2.5 秒后打开开关必须真的按 2.5 秒启动，关闭必须停。
            Try("自动翻页数字间隔启动 / 停止", () =>
            {
                var w = new ImageViewerWindow(pics);
                try
                {
                    if (w.Icon is null) throw new InvalidOperationException("看图窗口没有应用图标");
                    w.IntervalBox.Text = "2.5";
                    w.AutoPageCheck.IsChecked = true;
                    StartupLog.Write($"自检 自动翻页 2.5 秒: enabled={w.AutoPageEnabled} interval={w.AutoPageInterval.TotalSeconds}s");
                    if (!w.AutoPageEnabled) throw new InvalidOperationException("打开自动翻页后计时器没有启动");
                    if (Math.Abs(w.AutoPageInterval.TotalSeconds - 2.5) > 0.01)
                        throw new InvalidOperationException($"间隔应该是 2.5 秒，实际 {w.AutoPageInterval.TotalSeconds}");
                    w.AutoPageCheck.IsChecked = false;
                    if (w.AutoPageEnabled) throw new InvalidOperationException("关闭自动翻页后计时器没有停止");
                }
                finally { w.Close(); }
            });

            // 真跑一次 DispatcherTimer：3 张图、250ms 间隔，应在最后一页自动关窗。
            // 窗口移到屏幕外、不抢焦点，只在日志里留结果。
            try
            {
                var w = new ImageViewerWindow(pics)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 480,
                    Height = 360,
                };
                var closed = false;
                w.Closed += (_, _) => closed = true;
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    w.StartAutoPageForTest(TimeSpan.FromMilliseconds(250));
                    for (var i = 0; i < 40 && !closed; i++) await Task.Delay(100);
                    StartupLog.Write($"自检 自动翻页实跑: closed={closed} index={w.CurrentIndex}");
                    if (!closed) throw new InvalidOperationException("自动翻页到最后一页后没有自动关闭");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 自动翻页实跑: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 点窗口任意处：前两下翻页，第三下（最后一页）关闭。
            try
            {
                var w = new ImageViewerWindow(pics)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 480,
                    Height = 360,
                };
                var closed = false;
                w.Closed += (_, _) => closed = true;
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    await w.ClickAdvanceAsync();
                    if (w.CurrentIndex != 1) throw new InvalidOperationException($"第一次点击后应该在第 2 张，实际索引 {w.CurrentIndex}");
                    await w.ClickAdvanceAsync();
                    if (w.CurrentIndex != 2) throw new InvalidOperationException($"第二次点击后应该在第 3 张，实际索引 {w.CurrentIndex}");
                    await w.ClickAdvanceAsync();
                    await Task.Delay(100);
                    if (!closed) throw new InvalidOperationException("最后一页点击后没有关闭窗口");
                    StartupLog.Write("自检 点击任意处翻页 / 末页关闭: 通过");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 点击任意处翻页 / 末页关闭: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 空格：普通页翻下一页，最后一页直接关窗。
            try
            {
                var w = new ImageViewerWindow(pics)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 480,
                    Height = 360,
                };
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    await w.ClickAdvanceAsync();
                    await w.ClickAdvanceAsync();
                    if (!w.IsAtLastPage) throw new InvalidOperationException("没有停在最后一页");

                    var source = PresentationSource.FromVisual(w);
                    if (source is null) throw new InvalidOperationException("窗口没有 PresentationSource");
                    var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    w.RaiseEvent(keyEvent);
                    await Task.Delay(100);
                    if (w.IsVisible) throw new InvalidOperationException("最后一页按空格没有关闭窗口");
                    StartupLog.Write("自检 空格末页关闭: 通过");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 空格末页关闭: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 滚轮模式：切到缩放，模拟滚轮，确认比例变大；切回翻页后复位。
            try
            {
                var w = new ImageViewerWindow(pics)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 480,
                    Height = 360,
                };
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    w.WheelBox.SelectedIndex = 1;
                    if (!w.WheelZoomModeForTest) throw new InvalidOperationException("滚轮没有切到缩放模式");

                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                    };
                    w.Viewport.RaiseEvent(wheel);
                    await Task.Delay(50);
                    StartupLog.Write($"自检 滚轮缩放: scale={w.ZoomScaleForTest:0.00}");
                    if (w.ZoomScaleForTest <= 1.01) throw new InvalidOperationException("滚轮放大没有生效");

                    // 已有 offset 时（放大后再缩小）锚点必须仍然在同一屏幕位置。
                    var oldOffset = new Point(-80, -40);
                    var cursorLocal = new Point(300, 200);
                    var newOffset = ImageViewerWindow.ZoomOffsetAfter(cursorLocal, oldOffset, 2.0, 1.5);
                    var screenX = (cursorLocal.X - oldOffset.X) / 2.0 * 1.5 + newOffset.X;
                    var screenY = (cursorLocal.Y - oldOffset.Y) / 2.0 * 1.5 + newOffset.Y;
                    if (Math.Abs(screenX - cursorLocal.X) > 0.001 || Math.Abs(screenY - cursorLocal.Y) > 0.001)
                        throw new InvalidOperationException($"有平移量时缩小锚点漂移：{screenX:0.###},{screenY:0.###}");

                    w.WheelBox.SelectedIndex = 2;
                    var beforeInterval = w.AutoPageSecondsForTest;
                    var intervalWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                    };
                    w.Viewport.RaiseEvent(intervalWheel);
                    await Task.Delay(50);
                    StartupLog.Write($"自检 滚轮调速: {beforeInterval:0.0}s -> {w.AutoPageSecondsForTest:0.0}s");
                    if (Math.Abs(w.AutoPageSecondsForTest - beforeInterval - 0.1) > 0.001)
                        throw new InvalidOperationException("滚轮向下没有把自动翻页间隔增加 0.1 秒");

                    w.WheelBox.SelectedIndex = 0;
                    if (w.ZoomScaleForTest > 1.001) throw new InvalidOperationException("切回翻页后缩放没有复位");
                    StartupLog.Write("自检 滚轮翻页/缩放/调速切换: 通过");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 滚轮翻页/缩放切换: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 历史记录：打开/关闭各保留 10 个，并且只保留最新一条同名路径。
            Try("历史记录最多 10 个", () =>
            {
                ViewerHistory.ClearOpened();
                ViewerHistory.ClearClosed();
                try
                {
                    for (var i = 0; i < 12; i++)
                        ViewerHistory.AddOpen($@"C:\tmp\history-{i}");
                    if (ViewerHistory.Opened.Count != 10)
                        throw new InvalidOperationException($"打开记录应为 10 个，实际 {ViewerHistory.Opened.Count}");
                    if (ViewerHistory.Opened[0].Name != "history-11")
                        throw new InvalidOperationException("最新打开记录没有排在最前");

                    ViewerHistory.AddClose(@"C:\tmp\history-0");
                    ViewerHistory.AddClose(@"C:\tmp\history-0");
                    if (ViewerHistory.Closed.Count != 1)
                        throw new InvalidOperationException("同一个文件夹关闭记录应该只保留一条");

                    var win = new HistoryWindow
                    {
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000,
                    };
                    win.Show();
                    win.Close();
                }
                finally
                {
                    ViewerHistory.ClearOpened();
                    ViewerHistory.ClearClosed();
                }
            });

            // 双页显示：左右两页都在，步长可在「翻一张 / 翻两张」之间切换。
            try
            {
                var w = new ImageViewerWindow(pics)
                {
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                    Width = 640,
                    Height = 420,
                };
                try
                {
                    w.Show();
                    await Task.Delay(300);
                    w.DoublePageCheck.IsChecked = true;
                    await Task.Delay(300);
                    if (!w.DoublePageForTest) throw new InvalidOperationException("双页开关没有打开");
                    if (w.PictureRight.Source is null) throw new InvalidOperationException("双页右侧没有加载第二张图");
                    if (w.PictureRight.Visibility != Visibility.Visible) throw new InvalidOperationException("双页右侧图片没有显示");
                    w.DoubleStepBox.SelectedIndex = 0;
                    if (w.DoubleStepForTest != 1) throw new InvalidOperationException("双页步长没有切到翻一张");
                    w.DoubleStepBox.SelectedIndex = 1;
                    if (w.DoubleStepForTest != 2) throw new InvalidOperationException("双页步长没有切到翻两张");
                    w.DoublePageCheck.IsChecked = false;
                    await Task.Delay(200);
                    if (w.DoublePageForTest || w.PictureRight.Visibility != Visibility.Collapsed)
                        throw new InvalidOperationException("关闭双页后右侧没有收起");
                    StartupLog.Write("自检 双页显示/步长: 通过");
                }
                finally { if (w.IsVisible) w.Close(); }
            }
            catch (Exception ex)
            {
                failures++;
                StartupLog.Write($"自检 双页显示/步长: 失败 -> {ex.GetType().Name}: {ex.Message}");
            }

            // 三个标签窗口都必须是独立顶层窗口：不设 Owner，否则会挡住主窗口，翻页按钮点不到。
            Try("TagManagerWindow", () =>
            {
                var w = new TagManagerWindow(db!);
                try
                {
                    if (w.Owner is not null) throw new InvalidOperationException("标签管理窗口不应设置 Owner");
                    if (!w.ShowInTaskbar) throw new InvalidOperationException("标签管理窗口应出现在任务栏");
                }
                finally { w.Close(); }
            });
            Try("TagPickerWindow", () =>
            {
                var w = new TagPickerWindow(db!, sample.Take(2).ToList());
                try
                {
                    if (w.Owner is not null) throw new InvalidOperationException("打标签窗口不应设置 Owner");
                    if (!w.ShowInTaskbar) throw new InvalidOperationException("打标签窗口应出现在任务栏");
                }
                finally { w.Close(); }
            });
            Try("FolderTagWindow", () =>
            {
                var w = new FolderTagWindow(db!, db!.GetFolders());
                try
                {
                    if (w.Owner is not null) throw new InvalidOperationException("管理文件夹标签窗口不应设置 Owner");
                    if (!w.ShowInTaskbar) throw new InvalidOperationException("管理文件夹标签窗口应出现在任务栏");
                }
                finally { w.Close(); }
            });
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 临时目录清理失败不影响结论 */ }
        }

        StartupLog.Write(failures == 0 ? "自检全部通过" : $"自检失败 {failures} 项");
        return failures;
    }

    /// <summary>遍历可视树，用来核对界面里到底渲染出了什么。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>写一张纯色小 PNG，给自检当图片素材用。</summary>
    private static void WriteTinyPng(string path, int width, int height, byte b, byte g, byte r)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>WCAG 相对亮度，用来算两种颜色的对比度。</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>对比度，1:1 到 21:1。取不到纯色画刷时按最高分放过。</summary>
    private static double Contrast(Brush a, Brush b)
    {
        if (a is not SolidColorBrush sa || b is not SolidColorBrush sb) return 21;
        var la = Luminance(sa.Color);
        var lb = Luminance(sb.Color);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static void Report(Exception? ex, string stage)
    {
        StartupLog.Write($"{stage}: {ex?.ToString() ?? "(没有异常对象)"}");

        var hint = ex switch
        {
            Microsoft.Data.Sqlite.SqliteException se
                when se.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase)
                  || se.Message.Contains("unable to open", StringComparison.OrdinalIgnoreCase) =>
                "\n\n数据库无法写入，请依次检查：\n" +
                $"  1. {Database.DefaultDataDirectory} 是否被设为只读；\n" +
                "  2. 杀毒软件的「受控文件夹访问」是否拦住了本程序；\n" +
                "  3. 若仍不行，直接删掉该目录下的 assets.db 再运行\n" +
                "     （只会丢失索引和标签，原始素材文件不受影响）。",
            _ => ""
        };

        try
        {
            var detail = ex is null ? "" : $"\n\n{ex.GetType().Name}: {ex.Message}";
            var choice = MessageBox.Show(
                $"程序无法启动（{stage}）。{detail}{hint}\n\n完整信息已写入：\n{StartupLog.LogPath}\n\n是否打开日志所在文件夹？",
                "本地素材搜索", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (choice == MessageBoxResult.Yes) RevealLog();
        }
        catch { /* 连弹窗都失败就只剩日志了 */ }
    }

    private static void RevealLog()
    {
        try
        {
            var args = File.Exists(StartupLog.LogPath)
                ? $"/select,\"{StartupLog.LogPath}\""
                : $"\"{Path.GetDirectoryName(StartupLog.LogPath)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch { }
    }
}
