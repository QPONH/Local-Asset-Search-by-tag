using LocalAssetSearch.Models;
using LocalAssetSearch.Services;
using Microsoft.Data.Sqlite;

namespace Verify;

/// <summary>
/// 针对本项目的回归测试。全部在临时目录里跑，不碰真实素材库
/// (%LOCALAPPDATA%\LocalAssetSearch\assets.db)。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        // 诊断模式：verify --migrate <数据目录>，对指定的真实库跑一次迁移并打印结果。
        if (args.Length >= 2 && args[0] == "--migrate")
        {
            var target = args[1];
            Console.WriteLine($"对 {target} 执行迁移…");
            try
            {
                var db = new Database(target);
                using var c = db.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Assets'";
                Console.WriteLine("迁移后 DDL: " + (cmd.ExecuteScalar()?.ToString() ?? "(无)").Replace("\n", " ").Replace("\r", ""));
                Console.WriteLine($"文件夹数: {db.GetFolders().Count}");
                Console.WriteLine("迁移成功");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"迁移失败: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        var temp = Path.Combine(@"C:\work", "las-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Console.WriteLine($"临时目录：{temp}");
        try
        {
            SortChecks();
            CoverChecks(temp);
            FavoriteOrderChecks();
            SortOrderChecks(temp);
            TitleSortChecks(temp);
            DatabaseChecks(temp);
            MigrationChecks(temp);
            StaleCleanupChecks(temp);
            FeatureChecks(temp);
            ArchiveChecks(temp);
            CategoryChecks(temp);
            WorkMetadataChecks(temp);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 临时文件清理失败不影响结论 */ }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "全部通过" : $"{_failures} 项失败");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length == 0 ? "" : $"   ({detail})")}");
        if (!ok) _failures++;
    }

    private static void SortChecks()
    {
        Console.WriteLine();
        Console.WriteLine("== 文件名自然排序 ==");
        var names = new[] { "10.png", "2.png", "1.png", "20.png", "1.jpg" };
        var expected = new[] { "1.jpg", "1.png", "2.png", "10.png", "20.png" };
        var natural = names.OrderBy(n => n, ExplorerNameComparer.Instance).ToArray();
        Check(natural.SequenceEqual(expected), "自然序 1 < 2 < 10 < 20", string.Join(" < ", natural));
        Console.WriteLine($"         旧实现的字符串序作对照：{string.Join(" < ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}");
    }

    private static void CoverChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 文件夹封面取图 ==");

        var a = Path.Combine(root, "作品A");
        Directory.CreateDirectory(a);
        foreach (var n in new[] { "1.png", "2.png", "10.png" }) File.WriteAllText(Path.Combine(a, n), "");
        Check(Path.GetFileName(FolderCover.Find(a)) == "1.png", "作品A 取 1.png 而不是 10.png", Path.GetFileName(FolderCover.Find(a)));

        var b = Path.Combine(root, "作品B");
        Directory.CreateDirectory(b);
        foreach (var n in new[] { "5.png", "0.png" }) File.WriteAllText(Path.Combine(b, n), "");
        Check(Path.GetFileName(FolderCover.Find(b)) == "0.png", "作品B 主文件名 0 优先", Path.GetFileName(FolderCover.Find(b)));

        var c = Path.Combine(root, "含PDF");
        Directory.CreateDirectory(c);
        File.WriteAllText(Path.Combine(c, "2.png"), "");
        File.WriteAllText(Path.Combine(c, "1.pdf"), "");
        Check(Path.GetFileName(FolderCover.Find(c)) == "2.png", "含PDF：PDF 不参与封面，取图片 2.png", Path.GetFileName(FolderCover.Find(c)));

        var onlyPdf = Path.Combine(root, "只有PDF");
        Directory.CreateDirectory(onlyPdf);
        File.WriteAllText(Path.Combine(onlyPdf, "1.pdf"), "");
        Check(FolderCover.Find(onlyPdf).Length == 0, "只有PDF：不当作封面，返回空", $"\"{FolderCover.Find(onlyPdf)}\"");

        var d = Path.Combine(root, "无图");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "readme.txt"), "");
        Check(FolderCover.Find(d).Length == 0, "无图：返回空", $"\"{FolderCover.Find(d)}\"");

        var e = Path.Combine(root, "只有子目录");
        Directory.CreateDirectory(Path.Combine(e, "sub"));
        File.WriteAllText(Path.Combine(e, "sub", "1.png"), "");
        Check(FolderCover.Find(e).Length == 0, "不递归：子目录里的图不当封面", $"\"{FolderCover.Find(e)}\"");

        Check(FolderCover.Find(Path.Combine(root, "不存在的目录")).Length == 0, "目录不存在：安全返回空");
    }

    private static void FavoriteOrderChecks()
    {
        Console.WriteLine();
        Console.WriteLine("== 收藏夹排序号解析 ==");
        Check(FavoriteOrder.Parse("a{b}{1,3}") == (1, 3), "a{b}{1,3} → 第 1 页第 3 个");
        Check(FavoriteOrder.Parse("某作品{甘霖组}{金世兰}{1,3}") == (1, 3), "多作者时只取末尾那一组");
        Check(FavoriteOrder.Parse("a{b}{12,340}") == (12, 340), "多位数字");
        Check(FavoriteOrder.Parse("a{b}{ 1 , 3 }") == (1, 3), "允许空格");
        Check(FavoriteOrder.Parse("a{b}{1,3}x") is null, "后面还有内容就不算（必须落在结尾）");
        Check(FavoriteOrder.Parse("普通文件夹") is null, "没有该后缀返回 null");
        Check(FavoriteOrder.Parse("xxx{a}{2}") == (2, 0), "只写一个数字 xxx{a}{2} → (2,0)");
        Check(FavoriteOrder.Parse("xxx{a}{ 2 }") == (2, 0), "单数字也允许空格");
        Check(FavoriteOrder.Parse("") is null, "空名字返回 null");
    }

    private static void SortOrderChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 按收藏夹页码 / 页内序号排序 ==");

        var scanRoot = Path.Combine(root, "排序库");
        foreach (var n in new[] { "甲{作者}{1,3}", "乙{作者}{1,2}", "丙{作者}{2,1}", "丁{作者}{1,10}", "无后缀文件夹" })
            Directory.CreateDirectory(Path.Combine(scanRoot, n));

        var db = new Database(Path.Combine(root, "db-sort"));
        db.AddRootAndScan(scanRoot);

        var names = db.Search("", Array.Empty<long>(), true).Select(i => i.Name).ToArray();
        // 先页码后序号升序；{1,10} 排在 {1,3} 之后，说明比的是数值而不是字符串；
        // 解析不出页码的（根目录自己、无后缀文件夹）排在最后，它们之间按名称。
        var expected = new[] { "乙{作者}{1,2}", "甲{作者}{1,3}", "丁{作者}{1,10}", "丙{作者}{2,1}", "排序库", "无后缀文件夹" };
        Check(names.SequenceEqual(expected), "升序排列，无页码的排最后", string.Join(" | ", names));

        var paged = db.SearchPage("", Array.Empty<long>(), true, SortMode.FavoriteOrder, 0, 3);
        Check(paged.Items.Select(i => i.Name).SequenceEqual(new[] { "乙{作者}{1,2}", "甲{作者}{1,3}", "丁{作者}{1,10}" }),
              "分页也遵循同一排序（第一页 3 条）", string.Join(" | ", paged.Items.Select(i => i.Name)));

        // 倒序：页码大的在前，同一页里序号大的在前；解析不出的仍然排最后。
        var desc = db.Search("", Array.Empty<long>(), true, SortMode.FavoriteOrderDesc).Select(i => i.Name).ToArray();
        var expectedDesc = new[] { "丙{作者}{2,1}", "丁{作者}{1,10}", "甲{作者}{1,3}", "乙{作者}{1,2}", "无后缀文件夹", "排序库" };
        Check(desc.SequenceEqual(expectedDesc), "倒序：页码、序号大的在前", string.Join(" | ", desc));

        // 用户给的例子：xxx{a}{2} 要排在 xxx{a}{1} 前面。
        var exampleRoot = Path.Combine(root, "例子库");
        Directory.CreateDirectory(Path.Combine(exampleRoot, "xxx{a}{1}"));
        Directory.CreateDirectory(Path.Combine(exampleRoot, "xxx{a}{2}"));
        var exampleDb = new Database(Path.Combine(root, "db-example"));
        exampleDb.AddRootAndScan(exampleRoot);
        var example = exampleDb.Search("", Array.Empty<long>(), true, SortMode.FavoriteOrderDesc)
            .Select(i => i.Name).Where(n => n != "例子库").ToArray();
        Check(example.SequenceEqual(new[] { "xxx{a}{2}", "xxx{a}{1}" }),
              "xxx{a}{2} 排在 xxx{a}{1} 前面", string.Join(" | ", example));
    }

    private static void TitleSortChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 首字母正序 / 倒序 ==");

        var scanRoot = Path.Combine(root, "字母库");
        foreach (var n in new[] { "banana{a}{1,2}", "Apple{a}{2,5}", "cherry{a}{1,9}" })
            Directory.CreateDirectory(Path.Combine(scanRoot, n));

        var db = new Database(Path.Combine(root, "db-title"));
        db.AddRootAndScan(scanRoot);

        // 排除根目录自己（它的标题是「字母库」），只比较三个作品。
        string[] Titles(SortMode mode) => db.Search("", Array.Empty<long>(), true, mode)
            .Select(i => i.Name).Where(n => n != "字母库").ToArray();

        var asc = Titles(SortMode.TitleAsc);
        var desc = Titles(SortMode.TitleDesc);
        var fav = Titles(SortMode.FavoriteOrder);

        Check(asc.SequenceEqual(new[] { "Apple{a}{2,5}", "banana{a}{1,2}", "cherry{a}{1,9}" }),
              "首字母 A→Z（不分大小写，只看标题部分）", string.Join(" | ", asc));
        Check(desc.SequenceEqual(new[] { "cherry{a}{1,9}", "banana{a}{1,2}", "Apple{a}{2,5}" }),
              "首字母 Z→A", string.Join(" | ", desc));
        Check(fav.SequenceEqual(new[] { "banana{a}{1,2}", "cherry{a}{1,9}", "Apple{a}{2,5}" }),
              "切到收藏夹顺序时结果确实不同", string.Join(" | ", fav));

        Check(FavoriteOrder.TitleOf("Apple{a}{2,5}") == "Apple", "标题取第一个 { 之前的内容");
        Check(FavoriteOrder.TitleOf("没有大括号") == "没有大括号", "没有大括号时标题就是整名");
    }

    private static void DatabaseChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 只索引文件夹 / 标签筛选 ==");

        var scanRoot = Path.Combine(root, "素材库");
        Directory.CreateDirectory(Path.Combine(scanRoot, "风景"));
        Directory.CreateDirectory(Path.Combine(scanRoot, "风景", "子目录"));
        Directory.CreateDirectory(Path.Combine(scanRoot, "城市"));
        File.WriteAllText(Path.Combine(scanRoot, "风景", "1.png"), "");
        File.WriteAllText(Path.Combine(scanRoot, "风景", "9.pdf"), "");

        var db = new Database(Path.Combine(root, "db"));
        db.AddRootAndScan(scanRoot);

        var all = db.Search("", Array.Empty<long>(), true);
        Check(all.All(i => i.Kind == "folder"), "结果里只有文件夹", $"{all.Count} 条");
        Check(all.All(i => !i.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        && !i.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)),
              "图片和 PDF 都不进索引");
        Check(all.Count == 4, "共 4 个文件夹（根 + 风景 + 风景\\子目录 + 城市）", $"{all.Count}");
        Check(db.Search("1.png", Array.Empty<long>(), true).Count == 0, "按图片名搜不到");

        var scenery = db.GetFolders().FirstOrDefault(f => f.Name == "风景");
        Check(scenery is not null, "能取到「风景」文件夹");
        if (scenery is null) return;

        db.EnsureTag("自然");
        db.ApplyTag(scenery.Id, "自然");
        var naturalId = db.GetTags().First(t => t.Name == "自然").Id;

        var hits = db.Search("", new[] { naturalId }, true);
        Check(hits.Any(i => i.Name == "风景"), "标签筛出文件夹本身");
        Check(hits.Any(i => i.Name == "子目录"), "父文件夹标签也匹配它下面的子文件夹");
        Check(hits.Count == 2, "共命中 2 个文件夹", $"{hits.Count}");

        var sceneryHit = hits.First(i => i.Name == "风景");
        Check(sceneryHit.Tags.Contains("自然"), "卡片带着自己的标签（芯片显示用）", string.Join("、", sceneryHit.Tags));
        Check(hits.First(i => i.Name == "子目录").Tags.Count == 0, "继承来的标签不会冒充成子文件夹自己的标签");

        var city = db.GetFolders().First(f => f.Name == "城市");
        db.EnsureTag("都市");
        db.ApplyTag(city.Id, "都市");
        var urbanId = db.GetTags().First(t => t.Name == "都市").Id;

        Check(db.Search("", new[] { naturalId, urbanId }, false).Count == 3, "OR：两标签合计命中 3 个文件夹");
        Check(db.Search("", new[] { naturalId, urbanId }, true).Count == 0, "AND：没有文件夹同时带两个标签");

        Check(db.Search("城市", Array.Empty<long>(), true).Any(i => i.Name == "城市"), "关键字搜索仍然可用");
        Check(db.Search("城市", Array.Empty<long>(), true).All(i => i.Kind == "folder"), "关键字结果也只有文件夹");
    }

    /// <summary>
    /// 用一个老库（Kind 里含 pdf）验证迁移：pdf 记录被清掉，
    /// 文件夹及其标签保留，CHECK 约束收紧后不再接受 pdf。
    /// </summary>
    private static void MigrationChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 旧库迁移（移除 pdf）==");

        var dir = Path.Combine(root, "migrate-db");
        Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "assets.db") }.ToString();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE Roots(Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE);
CREATE TABLE Assets(
 Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE, ParentPath TEXT NOT NULL, Name TEXT NOT NULL,
 Kind TEXT NOT NULL CHECK(Kind IN ('folder','image','pdf')), Size INTEGER NOT NULL DEFAULT 0,
 ModifiedUtc TEXT NOT NULL, ExistsNow INTEGER NOT NULL DEFAULT 1);
CREATE TABLE Tags(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, Color TEXT NOT NULL DEFAULT '#64748b');
CREATE TABLE AssetTags(AssetId INTEGER NOT NULL, TagId INTEGER NOT NULL, PRIMARY KEY(AssetId,TagId));
CREATE TABLE Groups(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE GroupAssets(GroupId INTEGER NOT NULL, AssetId INTEGER NOT NULL, PRIMARY KEY(GroupId,AssetId));
INSERT INTO Assets(Id,Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow) VALUES
 (1,'C:\x\风景','C:\x','风景','folder',0,'2026-01-01T00:00:00.0000000Z',1),
 (2,'C:\x\风景\1.png','C:\x\风景','1.png','image',10,'2026-01-01T00:00:00.0000000Z',1),
 (3,'C:\x\风景\文档.pdf','C:\x\风景','文档.pdf','pdf',20,'2026-01-01T00:00:00.0000000Z',1);
INSERT INTO Tags(Id,Name) VALUES (1,'风景标签'),(2,'PDF标签');
INSERT INTO AssetTags(AssetId,TagId) VALUES (1,1),(2,1),(3,1),(3,2);";
            cmd.ExecuteNonQuery();
        }

        var db = new Database(dir);   // 构造即触发迁移

        Check(db.GetFolders().Any(f => f.Name == "风景"), "迁移后文件夹记录保留");

        var sceneryTagId = db.GetTags().First(t => t.Name == "风景标签").Id;
        var hits = db.Search("", new[] { sceneryTagId }, false);
        Check(hits.All(i => !i.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)),
              "迁移后 .pdf 记录已清除", $"{hits.Count} 条结果");
        Check(hits.Any(i => i.Name == "风景"), "迁移后文件夹标签关联保留");
        Check(hits.All(i => i.Kind == "folder"), "图片记录也不再出现在结果里（只显示文件夹）");

        using var conn = db.Open();
        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Assets'";
            var sql = ddl.ExecuteScalar()?.ToString() ?? "";
            Check(!sql.Contains("'pdf'", StringComparison.OrdinalIgnoreCase) && !sql.Contains("'video'", StringComparison.OrdinalIgnoreCase),
                  "迁移后 Assets 的 CHECK 只剩 folder / image");
        }

        bool rejected = false;
        try
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = @"INSERT INTO Assets(Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow) VALUES('C:\x\y.pdf','C:\x','y.pdf','pdf',1,'2026-01-01T00:00:00.0000000Z',1)";
            ins.ExecuteNonQuery();
        }
        catch (SqliteException) { rejected = true; }
        Check(rejected, "新约束会拒绝再写入 pdf 类型");
    }

    /// <summary>重扫同一个目录时，磁盘上已不存在的文件夹会被真正删掉（含标签关联）。</summary>
    private static void StaleCleanupChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 重扫时清理已不存在的文件夹 ==");

        var scanRoot = Path.Combine(root, "清理库");
        var keep = Path.Combine(scanRoot, "保留{a}{1,1}");
        var gone = Path.Combine(scanRoot, "将被删除{a}{1,2}");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(gone);

        var db = new Database(Path.Combine(root, "db-clean"));
        db.AddRootAndScan(scanRoot);
        Check(db.Search("", Array.Empty<long>(), true).Count == 3, "初始 3 个文件夹（根 + 2 个）",
              $"{db.Search("", Array.Empty<long>(), true).Count}");

        // 给将被删除的那个挂个标签，用来验证标签关联一起清掉。
        var target = db.GetFolders().First(f => f.Name.StartsWith("将被删除"));
        db.EnsureTag("临时标签");
        db.ApplyTag(target.Id, "临时标签");
        Check(db.GetTags().First(t => t.Name == "临时标签").UsageCount == 1, "标签已关联到该文件夹");

        // 从磁盘上删掉，再重扫同一个根。
        Directory.Delete(gone, true);
        var purged = db.AddRootAndScan(scanRoot);
        Check(purged == 1, "重扫清理了 1 条记录", $"{purged}");

        var after = db.Search("", Array.Empty<long>(), true);
        Check(after.Count == 2, "库里只剩根 + 保留那个", $"{after.Count} 条");
        Check(after.All(i => !i.Name.StartsWith("将被删除")), "已不存在的文件夹不再出现");
        Check(db.GetTags().First(t => t.Name == "临时标签").UsageCount == 0, "它上面的标签关联也被清掉");
        Check(Directory.Exists(keep), "磁盘上还在的目录不受影响");

        // 再扫一次不应重复清理。
        Check(db.AddRootAndScan(scanRoot) == 0, "再扫一次没有可清理的记录");
    }

    /// <summary>标签分类、删除标签、分页、从工具中移除。</summary>
    private static void FeatureChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 标签分类 / 删除标签 / 分页 / 从工具中移除 ==");

        var scanRoot = Path.Combine(root, "功能库");
        for (int i = 1; i <= 7; i++) Directory.CreateDirectory(Path.Combine(scanRoot, $"作品{i:00}"));
        // 作品07 下面再放两个子目录，用来验证「移除文件夹会连带内部」。
        Directory.CreateDirectory(Path.Combine(scanRoot, "作品07", "a"));
        Directory.CreateDirectory(Path.Combine(scanRoot, "作品07", "b"));

        var db = new Database(Path.Combine(root, "db-features"));
        db.AddRootAndScan(scanRoot);

        // ---- 标签分类 ----
        db.EnsureTag("风景", "题材");
        db.EnsureTag("人像", "题材");
        db.EnsureTag("2024", "年份");
        db.EnsureTag("临时");

        var tags = db.GetTags();
        Check(tags.First(t => t.Name == "风景").Category == "题材", "新建标签时能带分类");
        Check(tags.First(t => t.Name == "临时").CategoryLabel == "未分类", "空分类显示为「未分类」");
        Check(tags.Last().Name == "临时", "未分类的标签排在最后",
              string.Join(" | ", tags.Select(t => t.CategoryLabel + "/" + t.Name)));

        db.SetTagCategory(tags.First(t => t.Name == "临时").Id, "年份");
        Check(db.GetTags().First(t => t.Name == "临时").Category == "年份", "改分类生效");

        // ---- 改名与重名拦截 ----
        var taggedFolder = db.GetFolders().First(f => f.Name == "作品01");
        db.ApplyTag(taggedFolder.Id, "风景");
        var sceneryTag = db.GetTags().First(t => t.Name == "风景");
        db.RenameTag(sceneryTag.Id, "风光");
        Check(db.GetTags().Any(t => t.Name == "风光"), "标签改名生效");
        Check(db.GetAllAssetTags().TryGetValue(taggedFolder.Id, out var renamedTags) && renamedTags.Contains("风光"),
            "标签改名后文件夹关联不变", renamedTags is null ? "无" : string.Join("、", renamedTags));

        bool duplicateRejected = false;
        try { db.RenameTag(sceneryTag.Id, "人像"); } catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected, "改成已存在的标签名会被拒绝");

        // ---- 删除标签 ----
        var folder1 = db.GetFolders().First(f => f.Name == "作品01");
        var sceneryId = db.GetTags().First(t => t.Name == "风光").Id;
        db.ApplyTag(folder1.Id, "风光");
        Check(db.GetTags().First(t => t.Name == "风光").UsageCount == 1, "标签使用数统计正确");

        db.DeleteTag(sceneryId);
        Check(!db.GetTags().Any(t => t.Name == "风光"), "删除后标签定义消失");
        Check(db.Search("", new[] { sceneryId }, false).Count == 0, "删除后不再命中任何文件夹");

        // ---- 分页 ----
        // 共 10 个文件夹：根 + 作品01..07 + 作品07\a + 作品07\b
        var page1 = db.SearchPage("", Array.Empty<long>(), true, SortMode.FavoriteOrder, 0, 5);
        Check(page1.Total == 10, "总数统计正确（10 个文件夹）", $"total={page1.Total}");
        Check(page1.Items.Count == 5, "第一页取满 5 条", $"{page1.Items.Count} 条");

        var lastPage = db.SearchPage("", Array.Empty<long>(), true, SortMode.FavoriteOrder, 10, 5);
        Check(lastPage.Items.Count == 0, "超出范围的页返回空", $"{lastPage.Items.Count} 条");

        var allIds = new List<long>();
        for (int off = 0; off < 10; off += 5) allIds.AddRange(db.SearchPage("", Array.Empty<long>(), true, SortMode.FavoriteOrder, off, 5).Items.Select(i => i.Id));
        Check(allIds.Count == 10 && allIds.Distinct().Count() == 10, "逐页翻完得到 10 条且无重复", $"{allIds.Count} 条 / {allIds.Distinct().Count()} 个不同 Id");

        // ---- 从工具中移除（不删磁盘目录）----
        var victim = db.GetFolders().First(f => f.Name == "作品01");
        Check(Directory.Exists(victim.Path), "移除前磁盘目录确实存在");

        var removed = db.RemoveFromLibrary(new[] { victim.Id });
        Check(removed == 1, "移除计数为 1", $"{removed}");
        Check(db.Search("作品01", Array.Empty<long>(), true).Count == 0, "搜索结果里没有了");
        Check(db.GetHiddenCount() == 1, "隐藏列表记录了 1 条");
        Check(Directory.Exists(victim.Path), "移除后磁盘目录依然存在");

        db.AddRootAndScan(scanRoot);
        Check(db.Search("", Array.Empty<long>(), true).Any(i => i.Path == victim.Path), "重扫后旧隐藏目录自动回来");
        Check(db.GetHiddenCount() == 0, "重扫自动清空隐藏记录");

        // ---- 移除文件夹会连带内部子目录 ----
        var folder7 = db.GetFolders().First(f => f.Name == "作品07");
        var removedFolder = db.RemoveFromLibrary(new[] { folder7.Id });
        Check(removedFolder == 3, "移除文件夹连带 2 个子目录，共 3 条", $"实际 {removedFolder} 条");
        Check(db.Search("作品07", Array.Empty<long>(), true).Count == 0, "该文件夹及其子目录都不再出现");
        Check(db.GetFolders().All(f => f.Name != "作品07"), "文件夹列表里也没有了");

        // ---- 恢复 ----
        db.RestoreAllHidden();
        Check(db.GetHiddenCount() == 0, "恢复后隐藏列表清空");
        db.AddRootAndScan(scanRoot);
        Check(db.Search("", Array.Empty<long>(), true).Any(i => i.Path == victim.Path), "重扫后被移除的文件夹回来了");
    }

    /// <summary>分类可以先独立创建，标签再选择它。</summary>
    private static void CategoryChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 标签分类独立存储 ==");

        var db = new Database(Path.Combine(root, "db-categories"));
        db.EnsureCategory("先建分类");
        Check(db.GetCategories().Contains("先建分类"), "分类可以先于标签创建");

        var tagId = db.EnsureTag("分类标签", "先建分类");
        Check(db.GetTags().First(t => t.Id == tagId).Category == "先建分类", "标签可以选择已有分类");

        db.EnsureCategory("另一个分类");
        db.SetTagCategory(tagId, "另一个分类");
        Check(db.GetTags().First(t => t.Id == tagId).Category == "另一个分类", "标签改分类生效");
        Check(db.GetCategories().Contains("另一个分类"), "改分类后分类仍在列表里");

        db.EnsureTag("无分类标签");
        Check(db.GetTags().First(t => t.Name == "无分类标签").Category == "", "不选分类就是未分类");
    }

    /// <summary>work-id.json 作为索引依据：标题、作者、收藏夹页码/位置、GlobalIndex。</summary>
    private static void WorkMetadataChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== work-id.json 识别索引 ==");

        var scan = Path.Combine(root, "识别库");
        var metaDir = Path.Combine(scan, "濁音 6{50on!}{愛上陸}{319}");
        var plainDir = Path.Combine(scan, "无识别{作者}{9}");
        Directory.CreateDirectory(metaDir);
        Directory.CreateDirectory(plainDir);
        File.WriteAllText(Path.Combine(metaDir, "work-id.json"), """
            {
              "SchemaVersion": 1,
              "WorkId": "112462",
              "Title": "濁音 6",
              "Authors": ["50on!", "愛上陸"],
              "FavoritePage": 20,
              "FavoritePosition": 8,
              "GlobalIndex": 388,
              "ImageCount": 21
            }
            """);

        var db = new Database(Path.Combine(root, "db-workmeta"));
        db.AddRootAndScan(scan);

        using var c = db.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT FavPage,FavPos,SortTitle,WorkId,Authors,GlobalIndex FROM Assets WHERE Path=$p";
            cmd.Parameters.AddWithValue("$p", metaDir);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("没有索引到带 work-id 的文件夹");
            Check(r.GetInt32(0) == 20, "FavoritePage 用识别文件", r.GetInt32(0).ToString());
            Check(r.GetInt32(1) == 8, "FavoritePosition 用识别文件", r.GetInt32(1).ToString());
            Check(r.GetString(2) == "濁音 6", "SortTitle 用识别文件 Title", r.GetString(2));
            Check(r.GetString(3) == "112462", "保存 WorkId", r.GetString(3));
            Check(r.GetString(4) == "50on!、愛上陸", "保存 Authors", r.GetString(4));
            Check(r.GetInt32(5) == 388, "保存 GlobalIndex", r.GetInt32(5).ToString());
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT FavPage,FavPos,SortTitle FROM Assets WHERE Path=$p";
            cmd.Parameters.AddWithValue("$p", plainDir);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("没有索引到无 work-id 的文件夹");
            Check(r.GetInt32(0) == 9 && r.GetInt32(1) == 0, "无识别文件时回退目录名解析", $"{r.GetInt32(0)},{r.GetInt32(1)}");
            Check(r.GetString(2) == "无识别", "无识别文件时回退目录名标题", r.GetString(2));
        }

        Check(db.Search("112462", Array.Empty<long>(), true).Any(i => i.Path == metaDir), "可按 WorkId 搜索");
        Check(db.Search("愛上陸", Array.Empty<long>(), true).Any(i => i.Path == metaDir), "可按作者搜索");
        var order = db.Search("", Array.Empty<long>(), true, SortMode.GlobalIndex);
        Check(order.First().Path == metaDir, "GlobalIndex 排序", string.Join(" | ", order.Select(i => i.Name)));

        // 批量工具改了文件夹后缀和 work-id 的收藏夹位置后，旧路径消失。
        // 重扫时旧记录会被清理，标签必须按不变的 WorkId 迁到新路径。
        var oldItem = db.Search("濁音", Array.Empty<long>(), true).First(i => i.Path == metaDir);
        var tagId = db.EnsureTag("重命名保留");
        db.ApplyTag(oldItem.Id, "重命名保留");

        var renamed = Path.Combine(scan, "濁音 6{50on!}{愛上陸}{999}");
        Directory.Move(metaDir, renamed);
        File.WriteAllText(Path.Combine(renamed, "work-id.json"), """
            {
              "SchemaVersion": 1,
              "WorkId": "112462",
              "Title": "濁音 6",
              "Authors": ["50on!", "愛上陸"],
              "FavoritePage": 21,
              "FavoritePosition": 3,
              "GlobalIndex": 388,
              "ImageCount": 21
            }
            """);
        db.AddRootAndScan(scan);

        var tagged = db.Search("", new[] { tagId }, true);
        Check(tagged.Any(i => i.Path == renamed), "文件夹改名后标签按 WorkId 保留", string.Join(" | ", tagged.Select(i => i.Path)));
        Check(tagged.All(i => i.Path != metaDir), "旧路径记录已清理", string.Join(" | ", tagged.Select(i => i.Path)));

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT FavPage,FavPos,WorkId FROM Assets WHERE Path=$p";
            cmd.Parameters.AddWithValue("$p", renamed);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Check(r.GetInt32(0) == 21 && r.GetInt32(1) == 3, "修改收藏夹位置后重扫更新", $"{r.GetInt32(0)},{r.GetInt32(1)}");
                Check(r.GetString(2) == "112462", "新路径仍保存 WorkId", r.GetString(2));
            }
            else Check(false, "改名后的文件夹已重新索引", "没有找到新路径");
        }

        // 旧版本「从工具中删除」会写 HiddenPaths 并挡住重扫；现在重扫到它要自动取消隐藏。
        var renamedItem = db.Search("濁音", Array.Empty<long>(), true).First(i => i.Path == renamed);
        db.RemoveFromLibrary(new[] { renamedItem.Id }, hidePaths: true);
        Check(db.GetHiddenCount() > 0, "旧版删除写入隐藏记录");
        db.AddRootAndScan(scan);
        Check(db.Search("濁音", Array.Empty<long>(), true).Any(i => i.Path == renamed), "扫描到旧隐藏路径会自动重新加入");
        Check(db.GetHiddenCount() == 0, "扫描到后隐藏记录自动清空");
    }

    /// <summary>库备份：标签 + 文件夹列表另存为 JSON，再读取回一个空库。</summary>
    private static void ArchiveChecks(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== 库备份导出 / 读取 ==");

        Check(!Database.DefaultDataDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
              "默认数据目录不在 exe 同目录", Database.DefaultDataDirectory);

        var scan = Path.Combine(root, "备份库");
        Directory.CreateDirectory(Path.Combine(scan, "甲{a}{1,1}"));
        Directory.CreateDirectory(Path.Combine(scan, "乙{a}{1,2}"));
        Directory.CreateDirectory(Path.Combine(scan, "丙{a}{1,3}"));

        var db1 = new Database(Path.Combine(root, "db-archive-1"));
        db1.AddRootAndScan(scan);
        db1.EnsureTag("风景", "题材");
        db1.EnsureTag("人像", "题材");
        db1.EnsureTag("未使用", "其它");

        var folderA = db1.GetFolders().First(f => f.Name.StartsWith("甲"));
        var folderB = db1.GetFolders().First(f => f.Name.StartsWith("乙"));
        db1.ApplyTag(folderA.Id, "风景");
        db1.ApplyTag(folderA.Id, "人像");
        db1.ApplyTag(folderB.Id, "风景");

        // 从工具中删除丙：库里的行没了，只留隐藏记录；备份要能恢复这条隐藏记录。
        var folderC = db1.GetFolders().First(f => f.Name.StartsWith("丙"));
        db1.RemoveFromLibrary(new[] { folderC.Id });

        // 扫描根目录本身也会入库，所以导出里是「根 + 甲 + 乙」3 个；丙已从库里删除，只剩隐藏记录。
        var doc = LibraryArchive.Export(db1);
        Check(doc.Folders.Count == 3, "导出 3 个仍在库里的文件夹（根 + 甲 + 乙）", $"{doc.Folders.Count}");
        Check(doc.Tags.Count == 3, "导出 3 个标签（含未使用的）", $"{doc.Tags.Count}");
        Check(doc.HiddenPaths.Count == 1, "导出 1 条隐藏路径", $"{doc.HiddenPaths.Count}");

        var file = Path.Combine(root, "备份.laslib.json");
        LibraryArchive.Save(doc, file);
        Check(File.Exists(file), "JSON 文档已写出", file);

        var loaded = LibraryArchive.Load(file);
        Check(loaded.Folders.Count == doc.Folders.Count, "读回的文件夹数一致", $"{loaded.Folders.Count}");
        Check(loaded.Tags.Count == doc.Tags.Count, "读回的标签数一致", $"{loaded.Tags.Count}");

        var db2 = new Database(Path.Combine(root, "db-archive-2"));
        var result = LibraryArchive.Import(db2, loaded);
        Check(result.FoldersAdded == 3, "新库导入 3 个文件夹", $"{result.FoldersAdded}");
        Check(result.TagsAdded == 3, "新库导入 3 个标签", $"{result.TagsAdded}");
        Check(result.LinksAdded == 3, "新库恢复 3 条标签关联", $"{result.LinksAdded}");
        Check(result.HiddenAdded == 1, "新库恢复 1 条隐藏路径", $"{result.HiddenAdded}");

        // SearchPage 才会把标签填进 AssetItem；GetFolders 不带标签。
        var restoredA = db2.Search("甲", Array.Empty<long>(), true).First();
        Check(new HashSet<string>(restoredA.Tags).SetEquals(new[] { "人像", "风景" }),
              "标签关联恢复正确", string.Join("、", restoredA.Tags));
        Check(db2.GetHiddenCount() == 1, "隐藏列表恢复正确", $"{db2.GetHiddenCount()}");
        Check(db2.Search("", Array.Empty<long>(), true).All(i => !i.Name.StartsWith("丙")), "已隐藏的文件夹不会出现在结果里");

        var again = LibraryArchive.Import(db2, loaded);
        Check(again.FoldersAdded == 0 && again.TagsAdded == 0 && again.LinksAdded == 0,
              "重复导入是幂等的", $"新增文件夹={again.FoldersAdded} 标签={again.TagsAdded} 关联={again.LinksAdded}");

        // 路径当前不存在：保留记录，但 ExistsNow=0，不进搜索结果。
        var missingDoc = new LibraryArchive.Document();
        missingDoc.Folders.Add(new LibraryArchive.FolderRecord
        {
            Path = Path.Combine(root, "不存在的文件夹"),
            ParentPath = root,
            Name = "不存在的文件夹",
        });
        var db3 = new Database(Path.Combine(root, "db-archive-3"));
        var missing = LibraryArchive.Import(db3, missingDoc);
        Check(missing.FoldersAdded == 1 && missing.MissingFolders == 1, "不存在的路径也保留记录",
              $"新增={missing.FoldersAdded} 缺失={missing.MissingFolders}");
        Check(db3.Search("不存在的文件夹", Array.Empty<long>(), true).Count == 0, "不存在的路径不进入搜索结果");
    }
}
