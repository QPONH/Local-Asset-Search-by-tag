using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace LocalAssetSearch;

public partial class MainWindow : Window
{
    private readonly Database _db = new();
    private readonly ObservableCollection<AssetItem> _results = new();
    private readonly HashSet<long> _selectedTags = new();
    private readonly Dictionary<long, CheckBox> _tagChecks = new();
    private readonly Dictionary<string, long> _tagIdByName = new(StringComparer.OrdinalIgnoreCase);
    private int _searchVersion;
    private bool _loadingTags;
    private bool _initializing = true;
    private bool _autoRefreshOnStart;
    private bool _scanning;

    // 分页状态。加分页不只是为了好看：原来一次性把 2000 张卡片全实例化，还会为每张都生成缩略图。
    private int _page = 1;
    private int _pageSize = 60;
    private int _total;

    private static string ThumbDir => Path.Combine(Database.DataDirectory, "Thumbnails");

    public MainWindow()
    {
        InitializeComponent();
        // 标题带版本号：排查「改了怎么没生效」时，一眼就能确认跑的是哪个构建。
        Title = $"本地素材搜索 v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?"}";
        Directory.CreateDirectory(ThumbDir);
        Results.ItemsSource = _results;
        UpdateThemeButton();
        SortBox.SelectedIndex = ReadSavedSortIndex();
        LoadTags();
        _autoRefreshOnStart = Settings.Get("scan.refreshOnStart", "0") == "1";
        UpdateAutoRefreshButton();
        Loaded += MainWindow_Loaded;
        _initializing = false;
        Search();
    }

    private void UpdateAutoRefreshButton() =>
        AutoRefreshButton.Content = _autoRefreshOnStart ? "进入时刷新：开" : "进入时刷新：关";

    private void AutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        _autoRefreshOnStart = !_autoRefreshOnStart;
        UpdateAutoRefreshButton();
        Settings.Set("scan.refreshOnStart", _autoRefreshOnStart ? "1" : "0");
        StatusText.Text = _autoRefreshOnStart
            ? "已开启：下次打开工具会重新扫描上次选择的目录。"
            : "已关闭进入时自动刷新。";
    }

    /// <summary>
    /// 「进入时刷新」：用上次「添加/扫描目录」选中的目录跑一遍扫描。
    /// 放在 Loaded 里，保证窗口和数据库都已经就绪。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_autoRefreshOnStart) return;
        var root = Settings.Get("scan.lastRoot", "");
        if (!Directory.Exists(root)) return;
        await ScanRootAsync(root, auto: true);
    }

    private static int ReadSavedSortIndex()
        => int.TryParse(Settings.Get("sort", "0"), out var i) && i >= 0 && i <= 4 ? i : 0;

    /// <summary>当前排序方式，与 SortBox 的五个选项一一对应。</summary>
    private SortMode CurrentSort => SortBox.SelectedIndex switch
    {
        1 => SortMode.FavoriteOrderDesc,
        2 => SortMode.TitleAsc,
        3 => SortMode.TitleDesc,
        4 => SortMode.GlobalIndex,
        _ => SortMode.FavoriteOrder,
    };

    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        Settings.Set("sort", SortBox.SelectedIndex.ToString());
        await SearchFromFirstPageAsync();
    }

    private void UpdateThemeButton() =>
        ThemeButton.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";

    /// <summary>切换浅色 / 深色。标签栏里那些代码创建的控件颜色是建的时候取的，所以要重建一次。</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
        LoadTags();
    }

    private void ScrollResultsToTop()
    {
        ResultsScroll.ScrollToTop();
        // 换页后条目刚重建，布局还没走完，再补一次保证真的回到顶部。
        Dispatcher.BeginInvoke(new Action(() => ResultsScroll.ScrollToTop()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>刷新当前页时把滚动位置也恢复回来；打标签窗口关闭后不要跳回顶部。</summary>
    private void RestoreScroll(double offset)
    {
        ResultsScroll.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(new Action(() => ResultsScroll.ScrollToVerticalOffset(offset)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetBusy(bool busy) { IsEnabled = !busy; }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要索引的图片目录", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        Settings.Set("scan.lastRoot", dlg.FolderName);
        await ScanRootAsync(dlg.FolderName, auto: false);
    }

    /// <summary>添加/扫描和「进入时刷新」共用；auto 时失败只写状态栏，不弹启动错误框。</summary>
    private async Task ScanRootAsync(string root, bool auto)
    {
        if (_scanning) return;
        _scanning = true;
        SetBusy(true);
        StatusText.Text = auto ? "正在刷新上次选择的目录…" : "正在扫描目录…";
        try
        {
            var purged = await Task.Run(() => _db.AddRootAndScan(root, s => Dispatcher.Invoke(() => StatusText.Text = s)));
            LoadTags();
            await SearchFromFirstPageAsync();
            StatusText.Text = purged > 0
                ? $"{(auto ? "自动刷新" : "扫描")}完成，顺带清理了 {purged:N0} 个磁盘上已不存在的记录。"
                : $"{(auto ? "自动刷新" : "扫描")}完成。";
        }
        catch (Exception ex)
        {
            StatusText.Text = auto ? $"自动刷新失败：{ex.Message}" : "扫描失败。";
            if (!auto) System.Windows.MessageBox.Show(ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); _scanning = false; }
    }

    /// <summary>
    /// 重建左侧标签勾选栏，按分类分组显示。
    /// 会保留当前已勾选的标签——否则打完标签或点「刷新标签」就会把筛选条件清空。
    /// </summary>
    private void LoadTags()
    {
        var previously = new HashSet<long>(_selectedTags);
        _loadingTags = true;
        try
        {
            TagPanel.Children.Clear(); _tagChecks.Clear(); _tagIdByName.Clear(); _selectedTags.Clear();
            string currentCategory = "";
            UniformGrid? columnPanel = null;
            foreach (var tag in _db.GetTags())
            {
                if (!string.Equals(tag.CategoryLabel, currentCategory, StringComparison.OrdinalIgnoreCase))
                {
                    currentCategory = tag.CategoryLabel;
                    TagPanel.Children.Add(new TextBlock
                    {
                        Text = currentCategory,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeManager.MutedBrush,
                        Margin = new Thickness(2, 8, 2, 2)
                    });
                    // 每个分类下面固定两列；UniformGrid 按实际可用宽度分，滚动条也不会把它挤成一列。
                    columnPanel = new UniformGrid { Columns = 2 };
                    TagPanel.Children.Add(columnPanel);
                }

                _tagIdByName[tag.Name] = tag.Id;
                var label = new TextBlock
                {
                    Text = tag.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = tag.Name,
                    MaxWidth = 112,
                };
                var cb = new CheckBox
                {
                    Content = label,
                    Tag = tag.Id,
                    Margin = new Thickness(2, 3, 2, 3),
                    ToolTip = tag.Name,
                };
                cb.Checked += async (_, _) => { if (_loadingTags) return; _selectedTags.Add(tag.Id); await SearchFromFirstPageAsync(); };
                cb.Unchecked += async (_, _) => { if (_loadingTags) return; _selectedTags.Remove(tag.Id); await SearchFromFirstPageAsync(); };

                // 右键左侧标签也能直接编辑文字，不必打开标签管理。
                var menu = new ContextMenu();
                var edit = new MenuItem { Header = "编辑标签文字…", Tag = tag.Id };
                edit.Click += RenameFilterTag_Click;
                menu.Items.Add(edit);
                cb.ContextMenu = menu;

                columnPanel!.Children.Add(cb);
                _tagChecks[tag.Id] = cb;
                if (previously.Contains(tag.Id)) { _selectedTags.Add(tag.Id); cb.IsChecked = true; }
            }

            if (TagPanel.Children.Count == 0)
                TagPanel.Children.Add(new TextBlock { Text = "还没有标签。给文件夹打标签后会出现在这里。", Foreground = ThemeManager.MutedBrush, TextWrapping = TextWrapping.Wrap });
        }
        finally { _loadingTags = false; }
    }

    /// <summary>点卡片上的标签芯片 = 在左侧勾上该标签并立即重新筛选。</summary>
    private async void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        if (!_tagIdByName.TryGetValue(name, out var id)) { LoadTags(); if (!_tagIdByName.TryGetValue(name, out id)) return; }
        if (_selectedTags.Contains(id)) { await SearchFromFirstPageAsync(); return; }
        if (_tagChecks.TryGetValue(id, out var cb)) cb.IsChecked = true;   // 触发 Checked → 自行重新搜索
        else await SearchFromFirstPageAsync();
    }

    /// <summary>双击卡片上的标签芯片：编辑标签文字，不改变 TagId 和已有对应。</summary>
    private async void TagChip_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        if (!_tagIdByName.TryGetValue(name, out var id))
        {
            LoadTags();
            if (!_tagIdByName.TryGetValue(name, out id)) return;
        }
        e.Handled = true;
        await RenameTagTextAsync(id, name);
    }

    private async void RenameFilterTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: long id }) return;
        var tag = _db.GetTags().FirstOrDefault(t => t.Id == id);
        if (tag is null) { LoadTags(); return; }
        await RenameTagTextAsync(id, tag.Name);
    }

    private async Task RenameTagTextAsync(long tagId, string currentName)
    {
        var dlg = new TagRenameWindow(currentName) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var newName = dlg.TagName.Trim();
        if (newName.Length == 0 || string.Equals(newName, currentName, StringComparison.Ordinal)) return;

        try
        {
            _db.RenameTag(tagId, newName);
            LoadTags();
            await SearchAsync(resetScroll: false);
            StatusText.Text = $"标签文字已改为「{newName}」，已有标签对应保持不变。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "编辑标签文字失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool MatchAll => MatchBox.SelectedIndex == 0;
    private int TotalPages => Math.Max(1, (_total + _pageSize - 1) / _pageSize);

    private async void Search() => await SearchAsync();
    private Task SearchFromFirstPageAsync() { _page = 1; return SearchAsync(); }

    /// <summary>自检用：模拟打标签窗口关闭后的刷新，确认页码和滚动位置都不跳。</summary>
    internal Task RefreshKeepingPositionForTestAsync() => SearchAsync(resetScroll: false);

    private async Task SearchAsync(bool resetScroll = true)
    {
        var version = Interlocked.Increment(ref _searchVersion);
        var keyword = SearchBox?.Text ?? "";
        var tagIds = _selectedTags.ToArray();
        var matchAll = MatchAll;
        var sort = CurrentSort;
        var offset = (_page - 1) * _pageSize;
        var limit = _pageSize;
        var keepOffset = resetScroll ? 0 : ResultsScroll.VerticalOffset;
        try
        {
            var (items, total) = await Task.Run(() => _db.SearchPage(keyword, tagIds, matchAll, sort, offset, limit));
            if (version != Volatile.Read(ref _searchVersion)) return;

            // 页码可能越界（例如删光了最后一页），退到最后一页重查一次。
            if (items.Count == 0 && total > 0 && _page > 1)
            {
                _page = Math.Max(1, (total + _pageSize - 1) / _pageSize);
                await SearchAsync(resetScroll);
                return;
            }

            _total = total;
            StatusText.Text = "正在生成缩略图…";
            await Task.Run(() => Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, PrepareCard));
            if (version != Volatile.Read(ref _searchVersion)) return;

            foreach (var item in items) item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AssetItem.IsSelected)) UpdateSelectionInfo(); };
            _results.Clear(); foreach (var item in items) _results.Add(item);

            ResultCount.Text = $"搜索结果（共 {_total:N0} 项）";
            PageInfo.Text = $"第 {_page} / {TotalPages} 页";
            UpdateSelectionInfo();
            if (resetScroll) ScrollResultsToTop();
            else RestoreScroll(keepOffset);
            StatusText.Text = _total == 0 ? "没有符合条件的文件夹。" : "索引和标签保存在本机；原始文件夹不会被移动或修改。";
        }
        catch (Exception ex) { if (version == Volatile.Read(ref _searchVersion)) StatusText.Text = ex.Message; }
    }

    private void UpdateSelectionInfo() => SelectionInfo.Text = $"已选 {_results.Count(r => r.IsSelected):N0} 项";

    // ---------- 分页 ----------

    private async void FirstPage_Click(object sender, RoutedEventArgs e) { _page = 1; await SearchAsync(); }
    private async void PrevPage_Click(object sender, RoutedEventArgs e) { if (_page > 1) { _page--; await SearchAsync(); } }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { if (_page < TotalPages) { _page++; await SearchAsync(); } }
    private async void LastPage_Click(object sender, RoutedEventArgs e) { _page = TotalPages; await SearchAsync(); }

    private async void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PageSizeBox?.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Content?.ToString(), out var size) || size <= 0) return;
        _pageSize = size;
        _page = 1;
        await SearchAsync();
    }

    private async void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(JumpBox.Text.Trim(), out var page)) { StatusText.Text = "请输入页码数字。"; return; }
        _page = Math.Clamp(page, 1, TotalPages);
        await SearchAsync();
    }

    private async void JumpBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (!int.TryParse(JumpBox.Text.Trim(), out var page)) { StatusText.Text = "请输入页码数字。"; return; }
        _page = Math.Clamp(page, 1, TotalPages);
        await SearchAsync();
    }

    // ---------- 选择与「从工具中删除」----------

    private void SelectPage_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _results) item.IsSelected = true;
        UpdateSelectionInfo();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _results) item.IsSelected = false;
        UpdateSelectionInfo();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) { System.Windows.MessageBox.Show("请先勾选要移除的条目。", "没有选中项"); return; }

        var hasFolder = selected.Any(r => r.Kind == "folder");
        var msg = $"从工具中移除 {selected.Count} 个条目？\n\n" +
                  "· 只删除本工具的索引和标签关联\n" +
                  "· 磁盘上的原始文件不会被删除\n" +
                  "· 再次扫描到这个目录时会直接重新加入，不再需要手动恢复隐藏"
                  + (hasFolder ? "\n· 其中包含文件夹，它内部已索引的项目会一并移除" : "");
        if (System.Windows.MessageBox.Show(msg, "从工具中删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true);
            var ids = selected.Select(r => r.Id).ToArray();
            var n = await Task.Run(() => _db.RemoveFromLibrary(ids, hidePaths: false));
            StatusText.Text = $"已从工具中移除 {n} 个条目（磁盘文件未改动）。";
            LoadTags();
            await SearchAsync();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// 删除选中的文件夹（移到回收站），同时删除本工具的索引和标签关联。
    /// 和「从工具中删除」不同：这个会动磁盘上的文件夹。
    /// </summary>
    private async void DeleteFolders_Click(object sender, RoutedEventArgs e)
    {
        var selected = _results.Where(r => r.IsSelected && r.Kind == "folder").ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("请先勾选要删除的文件夹。", "没有选中文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", selected.Take(6).Select(r => "· " + r.Path));
        if (selected.Count > 6) preview += $"\n…等 {selected.Count} 个文件夹";
        var msg = $"把选中的 {selected.Count} 个文件夹移到回收站，并删除索引/标签关联？\n\n" +
                  preview + "\n\n" +
                  "· 文件夹会进入回收站，可以从回收站恢复\n" +
                  "· 本工具里的索引和标签关联会删除\n" +
                  "· 请确认这些文件夹不再需要留在原位置";
        if (System.Windows.MessageBox.Show(msg, "删除文件夹", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => FolderDeleter.DeleteToRecycleBin(selected.Select(r => r.Path)));
            var deleted = new HashSet<string>(result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
            var ids = selected.Where(r => deleted.Contains(r.Path)).Select(r => r.Id).ToArray();
            if (ids.Length > 0) _db.RemoveFromLibrary(ids, hidePaths: false);

            LoadTags();
            await SearchAsync(resetScroll: false);
            StatusText.Text = $"已把 {result.DeletedFolders} 个文件夹移到回收站，并删除对应索引。";

            if (result.Errors.Count > 0)
            {
                var detail = string.Join("\n\n", result.Errors.Take(8));
                if (result.Errors.Count > 8) detail += $"\n\n…另有 {result.Errors.Count - 8} 个失败";
                System.Windows.MessageBox.Show(
                    "以下文件夹没能移入回收站（可能被占用或权限不足），索引已保留：\n\n" + detail,
                    "部分删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "删除文件夹失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    // ---------- 导出清单 ----------

    private static string SortLabel(SortMode sort) => sort switch
    {
        SortMode.FavoriteOrderDesc => "结尾 {页数,位置} 倒序",
        SortMode.TitleAsc => "首字母 A→Z",
        SortMode.TitleDesc => "首字母 Z→A",
        SortMode.GlobalIndex => "识别文件 GlobalIndex",
        _ => "结尾 {页数,位置}",
    };

    private static string CsvField(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>把当前筛选结果导成清单。制表符分隔，可以直接拖进 Excel 分列。</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        List<AssetItem> items;
        try { items = _db.Search(SearchBox?.Text ?? "", _selectedTags.ToArray(), MatchAll, CurrentSort); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        if (items.Count == 0) { System.Windows.MessageBox.Show("当前筛选结果为空，没有可导出的内容。", "导出清单"); return; }

        var tagNames = _db.GetTags().Where(t => _selectedTags.Contains(t.Id)).Select(t => t.Name).ToList();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出文件夹清单",
            FileName = $"文件夹清单-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "文本文件 (*.txt)|*.txt|CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            var sep = csv ? "," : "\t";

            string Field(string s) => csv ? CsvField(s) : s.Replace('\t', ' ');

            var sb = new StringBuilder();
            sb.AppendLine("本地素材搜索 — 文件夹清单");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"排序方式：{SortLabel(CurrentSort)}");
            sb.AppendLine($"关键字：{(string.IsNullOrWhiteSpace(SearchBox?.Text) ? "(无)" : SearchBox.Text.Trim())}");
            sb.AppendLine($"标签筛选：{(tagNames.Count == 0 ? "(无)" : string.Join(MatchAll ? " 且 " : " 或 ", tagNames))}");
            sb.AppendLine($"数量：{items.Count:N0}");
            sb.AppendLine();
            sb.AppendLine(string.Join(sep, "序号", "文件夹名称", "标签", "完整路径"));

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var tags = item.Tags.Count == 0 ? "" : string.Join("、", item.Tags);
                sb.AppendLine(string.Join(sep, (i + 1).ToString(), Field(item.Name), Field(tags), Field(item.Path)));
            }

            // 带 BOM 的 UTF-8：否则中文在记事本 / Excel 里会乱码。
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            StatusText.Text = $"已导出 {items.Count:N0} 个文件夹到 {dlg.FileName}";

            if (System.Windows.MessageBox.Show(
                    $"已导出 {items.Count:N0} 条到：\n{dlg.FileName}\n\n是否打开所在文件夹？",
                    "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                RevealInExplorer(dlg.FileName);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>把标签和文件夹列表另存为 JSON 文档（不包含图片）。</summary>
    private async void BackupLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var doc = await Task.Run(() => LibraryArchive.Export(_db));
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "库另存为",
                FileName = $"LocalAssetSearch-库备份-{DateTime.Now:yyyyMMdd-HHmm}{LibraryArchive.FileExtension}",
                Filter = "LocalAssetSearch 库备份 (*.laslib.json)|*.laslib.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = LibraryArchive.FileExtension,
                AddExtension = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            LibraryArchive.Save(doc, dlg.FileName);
            StatusText.Text = $"库已另存为：{dlg.FileName}";
            System.Windows.MessageBox.Show(
                $"已保存：\n· 文件夹 {doc.Folders.Count:N0} 个\n· 标签 {doc.Tags.Count:N0} 个\n· 隐藏路径 {doc.HiddenPaths.Count:N0} 条\n\n{dlg.FileName}\n\n文档里只有索引和标签，不包含图片。",
                "库另存为完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "库另存为失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>从 JSON 文档恢复标签和文件夹列表；合并进当前库，不删除现有数据。</summary>
    private async void RestoreLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "读取库备份",
            Filter = "LocalAssetSearch 库备份 (*.laslib.json)|*.laslib.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        StatusText.Text = "正在读取库…";
        try
        {
            var doc = LibraryArchive.Load(dlg.FileName);
            var result = await Task.Run(() => LibraryArchive.Import(_db, doc));

            LoadTags();
            await SearchFromFirstPageAsync();
            StatusText.Text = $"库读取完成：文件夹新增 {result.FoldersAdded:N0}，更新 {result.FoldersUpdated:N0}。";
            System.Windows.MessageBox.Show(
                "读取完成：\n" +
                $"· 文件夹：新增 {result.FoldersAdded:N0}，更新 {result.FoldersUpdated:N0}\n" +
                $"· 标签：新增 {result.TagsAdded:N0}，已有 {result.TagsExisting:N0}\n" +
                $"· 标签关联：新增 {result.LinksAdded:N0}\n" +
                $"· 根目录：新增 {result.RootsAdded:N0}；隐藏路径：新增 {result.HiddenAdded:N0}\n" +
                (result.MissingFolders > 0
                    ? $"· 当前不存在的路径：{result.MissingFolders:N0} 个（记录已保留，接上磁盘后重扫即可）"
                    : "· 所有路径当前都存在。"),
                "读取库完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取库失败。";
            System.Windows.MessageBox.Show(ex.Message, "读取库失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    // ---------- 标签操作 ----------

    /// <summary>
    /// 标签窗口统一走这里：独立显示（不设 Owner、不用 ShowDialog，避免挡住翻页），
    /// 关闭时如果改过内容，就刷新当前页并保持滚动位置。
    /// </summary>
    private void ShowTagWindow(Window win, Func<bool> changed)
    {
        win.Closed += async (_, _) =>
        {
            if (!changed() || !IsLoaded) return;
            LoadTags();
            await SearchAsync(resetScroll: false);
        };
        win.Show();
    }

    private void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var win = new TagManagerWindow(_db);
        ShowTagWindow(win, () => win.Changed);
    }

    /// <summary>给单个素材打标签：独立窗口，关掉后刷新当前页并保持滚动位置。</summary>
    private void TagItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is null || !long.TryParse(b.Tag.ToString(), out var id)) return;
        var target = _results.FirstOrDefault(r => r.Id == id);
        if (target is null) return;
        TagOne(target);
    }

    /// <summary>打开标签选择窗口；刻意不设 Owner、也不用 ShowDialog，避免挡住主窗口的翻页。</summary>
    private void TagOne(AssetItem item)
    {
        var win = new TagPickerWindow(_db, new List<AssetItem> { item });
        ShowTagWindow(win, () => win.Changed);
    }

    private void FolderTags_Click(object sender, RoutedEventArgs e)
    {
        var folders = _db.GetFolders();
        if (folders.Count == 0) { System.Windows.MessageBox.Show("请先扫描一个目录。", "没有已索引目录"); return; }
        var win = new FolderTagWindow(_db, folders);
        ShowTagWindow(win, () => win.Changed);
    }

    private void RefreshTags_Click(object sender, RoutedEventArgs e) => LoadTags();

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear(); MatchBox.SelectedIndex = 0;
        _loadingTags = true;
        foreach (var child in TagPanel.Children.OfType<CheckBox>()) child.IsChecked = false;
        _selectedTags.Clear();
        _loadingTags = false;
        await SearchFromFirstPageAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchFromFirstPageAsync();
    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) await SearchFromFirstPageAsync(); }

    // ---------- 打开 ----------

    /// <summary>
    /// 在工具内置的看图窗口里打开这个文件夹的图片，停在封面那张（也就是「第一张」）。
    ///
    /// 为什么不用系统图片程序：新版「照片」应用传单个文件时经常不给左右翻页，
    /// 老的照片查看器组件太旧、部分系统已移除。自己写的看图窗口左右键翻页一定可用。
    /// 想用系统程序的话，右键菜单里仍然保留了「用 Windows 图片打开第一张」。
    /// </summary>
    private void OpenInAppViewer(string folderPath)
    {
        if (FolderCover.ListImages(folderPath).Count == 0)
        {
            StatusText.Text = "这个文件夹里没有可以显示的图片。";
            return;
        }

        // 每次点都开一个新窗口，所以可以多开。
        // 刻意**不设 Owner**：被拥有的窗口永远盖在拥有者上面，Alt+Tab 切回工具窗口时也会被它挡住；
        // 而且被拥有的窗口不进任务栏，多开之后不好找。不设 Owner 才有独立、可切换的多窗口。
        new ImageViewerWindow(folderPath).Show();
    }

    /// <summary>用 Windows 图片（系统默认关联）打开第一张。</summary>
    private void OpenWithWindowsPhotos(string folderPath)
    {
        var first = FolderCover.Find(folderPath);
        if (string.IsNullOrEmpty(first) || !File.Exists(first))
        {
            StatusText.Text = "这个文件夹里没有可以直接打开的图片。";
            return;
        }

        OpenPath(first);
        StatusText.Text = $"已用 Windows 图片打开 {Path.GetFileName(first)}";
    }

    /// <summary>点封面 = 在工具里看图。</summary>
    private void Cover_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string folder) OpenInAppViewer(folder);
    }

    /// <summary>点作品名称 = 在资源管理器里打开这个文件夹。</summary>
    private void Title_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string folder) RevealInExplorer(folder);
    }

    /// <summary>在资源管理器里定位这个路径：目录就打开它，文件就选中它。</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- 卡片右键菜单 ----------

    // 右键菜单不在可视树里，绑卡片的 DataContext 并不可靠。
    // 改成在菜单弹出前（ContextMenuOpening）把当前条目记下来，菜单项直接用这个字段。
    private AssetItem? _contextItem;

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _contextItem = (sender as FrameworkElement)?.DataContext as AssetItem;
    }

    private void CtxReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item) RevealInExplorer(item.Path);
    }

    private void CtxView_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item) OpenInAppViewer(item.Path);
    }

    private void CtxOpenFirstImage_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item) OpenWithWindowsPhotos(item.Path);
    }

    private void CtxTag_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item) TagOne(item);
    }

    private async void CtxRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item) await RemoveOneAsync(item);
    }

    /// <summary>从工具中移除单个文件夹（磁盘不动）。</summary>
    private async Task RemoveOneAsync(AssetItem item)
    {
        if (System.Windows.MessageBox.Show(
                $"从工具中移除「{item.Name}」？\n\n" +
                "· 只删除本工具的索引和标签关联\n" +
                "· 磁盘上的原始文件夹不会被删除\n" +
                "· 再次扫描到这个目录时会直接重新加入，不再需要手动恢复隐藏",
                "从工具中删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true);
            await Task.Run(() => _db.RemoveFromLibrary(new[] { item.Id }, hidePaths: false));
            StatusText.Text = $"已从工具中移除「{item.Name}」（磁盘目录未改动）。";
            LoadTags();
            await SearchAsync();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    // ---------- 缩略图 / 图片数量 ----------

    /// <summary>
    /// 卡片上要显示的两样东西一起准备：缩略图，以及文件夹里有几张图。
    /// 合并成一趟是因为两者都要列同一个目录，分开会把目录列两遍。
    /// </summary>
    private void PrepareCard(AssetItem item)
    {
        var sourcePath = item.Path;
        if (item.Kind == "folder")
        {
            var (cover, count) = FolderCover.CoverAndCount(item.Path);
            item.ImageCount = count;
            sourcePath = cover;
            if (string.IsNullOrEmpty(sourcePath)) { item.ThumbnailPath = ""; return; }
        }
        item.ThumbnailPath = GetThumbnail(item, sourcePath);
    }

    private string GetThumbnail(AssetItem item, string sourcePath)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath)) return "";
            if (!File.Exists(sourcePath)) return "";
            var fi = new FileInfo(sourcePath);
            var keyText = $"{sourcePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{item.Kind}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText))).ToLowerInvariant();
            var target = Path.Combine(ThumbDir, key + ".jpg");
            if (File.Exists(target)) return target;

            try
            {
                // 和看图窗口用同一个解码入口：坏 EXIF 的 JPEG 也能生成缩略图，不用退回 shell 缩略图。
                var frame = ImageLoader.Load(sourcePath);
                var scale = Math.Min(1.0, 480.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
                var tb = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                var enc = new JpegBitmapEncoder { QualityLevel = 82 }; enc.Frames.Add(BitmapFrame.Create(tb));
                using var outFs = File.Create(target); enc.Save(outFs); return target;
            }
            catch
            {
                // For formats not decoded by WPF, use Windows' registered shell thumbnail provider.
                return TrySaveShellThumbnail(sourcePath, target) ? target : "";
            }
        }
        catch
        {
            try { var maybe = Path.Combine(ThumbDir, "invalid_" + Guid.NewGuid().ToString("N") + ".tmp"); if (File.Exists(maybe)) File.Delete(maybe); } catch { }
            return "";
        }
    }

    private static bool TrySaveShellThumbnail(string sourcePath, string target)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, ref iid, out var factory);
            if (hr < 0 || factory is null) return false;

            try
            {
                hr = factory.GetImage(new SIZE(480, 480), SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_THUMBNAILONLY, out hBitmap);
                if (hr < 0 || hBitmap == IntPtr.Zero) return false;
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(target);
                encoder.Save(output);
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch { return false; }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory? shellItem);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int width, int height) { cx = width; cy = height; }
    }

    [Flags]
    private enum SIIGBF : uint
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show($"无法打开路径：{ex.Message}"); }
    }
}
