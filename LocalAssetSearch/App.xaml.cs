using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
                StartupLog.Write($"自检 关闭标签后保持位置: before={before:0} after={after:0}");
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

            // 自动翻页：预设间隔对不对，以及**默认必须是关着的**
            //（否则一打开就自己翻页、跑到最后一页自己关掉，等于没法看图）。
            Try("自动翻页预设与默认状态", () =>
            {
                var expect = new (int Idx, int Sec)[] { (1, 2), (2, 3), (3, 5), (4, 10) };
                foreach (var (idx, sec) in expect)
                    if (ImageViewerWindow.PresetSeconds(idx) != sec)
                        throw new InvalidOperationException($"预设 {idx} 应该是 {sec} 秒，实际 {ImageViewerWindow.PresetSeconds(idx)}");

                var w = new ImageViewerWindow(pics);
                try
                {
                    StartupLog.Write($"自检 自动翻页预设: 共 {w.IntervalBox.Items.Count} 项，默认选中索引 {w.IntervalBox.SelectedIndex}");
                    if (w.IntervalBox.Items.Count != 5) throw new InvalidOperationException($"预设应该是 5 项，实际 {w.IntervalBox.Items.Count}");
                    if (w.IntervalBox.SelectedIndex != 0) throw new InvalidOperationException("自动翻页默认必须是「不自动翻页」");
                }
                finally { w.Close(); }
            });

            // 预设不只是下拉框里的文字：选中后必须真的启动计时器，选回「不自动」必须停。
            Try("自动翻页预设会启动 / 停止计时器", () =>
            {
                var w = new ImageViewerWindow(pics);
                try
                {
                    if (w.Icon is null) throw new InvalidOperationException("看图窗口没有应用图标");
                    w.IntervalBox.SelectedIndex = 1;
                    StartupLog.Write($"自检 选中预设 1: enabled={w.AutoPageEnabled} interval={w.AutoPageInterval.TotalSeconds}s");
                    if (!w.AutoPageEnabled) throw new InvalidOperationException("选中「每 2 秒」后计时器没有启动");
                    if (Math.Abs(w.AutoPageInterval.TotalSeconds - 2) > 0.01)
                        throw new InvalidOperationException($"间隔应该是 2 秒，实际 {w.AutoPageInterval.TotalSeconds}");
                    w.IntervalBox.SelectedIndex = 0;
                    if (w.AutoPageEnabled) throw new InvalidOperationException("选回「不自动翻页」后计时器没有停止");
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
