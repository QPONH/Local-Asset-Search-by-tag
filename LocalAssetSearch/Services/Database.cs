using System.IO;
using Path = global::System.IO.Path;
using Directory = global::System.IO.Directory;
using DirectoryInfo = global::System.IO.DirectoryInfo;
using File = global::System.IO.File;
using FileAttributes = global::System.IO.FileAttributes;
using FileInfo = global::System.IO.FileInfo;
using Microsoft.Data.Sqlite;
using LocalAssetSearch.Models;

namespace LocalAssetSearch.Services;

public sealed class Database
{
    private readonly string _connectionString;
    static Database()
    {
        // Use the SQLite implementation shipped with supported Windows versions.
        // This avoids downloading a separate native SQLite binary.
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }

    /// <summary>应用默认的数据目录：%LOCALAPPDATA%\LocalAssetSearch。</summary>
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssetSearch");

    /// <summary>第二个持久候选：%APPDATA%\LocalAssetSearch（漫游目录，不随 exe 删除）。</summary>
    public static string RoamingDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalAssetSearch");

    /// <summary>最后一个临时候选；只在前面的持久目录都不可写时使用。</summary>
    public static string TempDataDirectory => Path.Combine(Path.GetTempPath(), "LocalAssetSearch");

    /// <summary>实际使用的数据目录。默认目录不可写时会自动退到别处。</summary>
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    /// <summary>发生了数据目录回退时的说明，供界面提示；正常情况为 null。</summary>
    public static string? DataDirectoryNotice { get; private set; }

    /// <summary>
    /// 挑一个真正写得进去的数据目录。
    ///
    /// 为什么需要这个：程序可能被放在受限环境里运行（沙箱、被收紧的令牌等），
    /// 这时 %LOCALAPPDATA% 只有只读权限，SQLite 连 WAL 的 -shm 都建不出来，
    /// 直接抛 "attempt to write a readonly database"，表现就是「双击没反应」。
    /// 与其死掉，不如退到一个可写的目录继续跑，并把这件事明确告诉用户。
    /// </summary>
    private static string PickWritableDirectory()
    {
        // 注意：绝不把 exe 同目录当数据目录。
        // exe 被删除、替换或重新下载后，那里的库会跟着消失，用户就得重新打标签、重新扫描。
        // 所以回退只选「不随 exe 走」的持久目录，TEMP 只作为最后手段。
        var candidates = new[]
        {
            DefaultDataDirectory,
            RoamingDataDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LocalAssetSearch"),
            TempDataDirectory,
        };
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".las-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch { /* 换下一个候选目录 */ }
        }
        return DefaultDataDirectory;
    }

    public Database() : this(null) { }

    private static string? _resolvedDataDirectory;

    /// <summary>
    /// 挑一个可写的数据目录并缓存下来。
    /// 界面在数据库实例化之前也要用它（比如主题设置文件放这里）。
    /// </summary>
    public static string ResolveDataDirectory() => _resolvedDataDirectory ??= PickWritableDirectory();

    /// <summary>仅供自检使用：把数据目录钉到临时位置，避免自检污染真实素材库。</summary>
    internal static void OverrideDataDirectory(string dir) => _resolvedDataDirectory = dir;

    /// <summary>指定数据目录；传 null 表示自动挑一个可写的（测试时可显式指定，避免碰到真实素材库）。</summary>
    public Database(string? dataDirectory)
    {
        var dir = dataDirectory ?? ResolveDataDirectory();
        if (dataDirectory is null && !string.Equals(dir, DefaultDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var isTemp = string.Equals(dir, TempDataDirectory, StringComparison.OrdinalIgnoreCase);
            DataDirectoryNotice = isTemp
                ? $"默认数据目录和用户目录都不可写，本次临时改用：\n{dir}\n\n" +
                  "这是临时目录，可能被系统清理；索引和标签有丢失风险。\n" +
                  "请用正常方式启动（在资源管理器里双击 exe），否则下次可能看不到本次的标签。"
                : $"默认数据目录不可写，本次改用持久目录：\n{dir}\n\n" +
                  "索引和标签会保存在这里，删除、替换或重新下载 exe 都不受影响。\n" +
                  $"但这里看不到 {DefaultDataDirectory} 里原来的库；如果那边有数据，请先修复权限再用默认目录启动。";
        }
        DataDirectory = dir;
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "assets.db") }.ToString();
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS Roots(Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE);
CREATE TABLE IF NOT EXISTS Assets(
 Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE, ParentPath TEXT NOT NULL, Name TEXT NOT NULL,
 Kind TEXT NOT NULL CHECK(Kind IN ('folder','image')), Size INTEGER NOT NULL DEFAULT 0,
 ModifiedUtc TEXT NOT NULL, ExistsNow INTEGER NOT NULL DEFAULT 1,
 FavPage INTEGER, FavPos INTEGER, SortTitle TEXT, WorkId TEXT, Authors TEXT, GlobalIndex INTEGER);
CREATE INDEX IF NOT EXISTS IX_Assets_ParentPath ON Assets(ParentPath);
CREATE INDEX IF NOT EXISTS IX_Assets_Name ON Assets(Name);
CREATE TABLE IF NOT EXISTS Tags(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, Color TEXT NOT NULL DEFAULT '#64748b', Category TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS Categories(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS AssetTags(AssetId INTEGER NOT NULL REFERENCES Assets(Id) ON DELETE CASCADE, TagId INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE, PRIMARY KEY(AssetId,TagId));
CREATE TABLE IF NOT EXISTS Groups(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS GroupAssets(GroupId INTEGER NOT NULL REFERENCES Groups(Id) ON DELETE CASCADE, AssetId INTEGER NOT NULL REFERENCES Assets(Id) ON DELETE CASCADE, PRIMARY KEY(GroupId,AssetId));
CREATE TABLE IF NOT EXISTS HiddenPaths(Path TEXT NOT NULL PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        MigrateKindSchema(c);
        MigrateTagCategoryColumn(c);
        MigrateSortColumns(c);
        MigrateWorkMetadataColumns(c);
        SeedCategories(c);
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=ON;"; pragma.ExecuteNonQuery(); }
    }

    /// <summary>
    /// 旧库的 Assets.Kind 里可能还留着 'video'（更早的版本）或 'pdf'（v0.3.x）。
    /// 现在只支持 folder / image，所以一旦发现旧 CHECK 约束就把表重建一遍，
    /// 顺手清掉这些记录以及挂在它们上面的标签。
    /// </summary>
    private static void MigrateKindSchema(SqliteConnection c)
    {
        string schema;
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Assets'"; schema = cmd.ExecuteScalar()?.ToString() ?? ""; }
        bool legacy = schema.Contains("'video'", StringComparison.OrdinalIgnoreCase) || schema.Contains("'pdf'", StringComparison.OrdinalIgnoreCase);
        if (!legacy) return;
        using var tx = c.BeginTransaction();
        using var cmd2 = c.CreateCommand(); cmd2.Transaction = tx;
        cmd2.CommandText = @"
DELETE FROM AssetTags WHERE AssetId IN (SELECT Id FROM Assets WHERE Kind NOT IN ('folder','image'));
DELETE FROM GroupAssets WHERE AssetId IN (SELECT Id FROM Assets WHERE Kind NOT IN ('folder','image'));
CREATE TABLE Assets_new(
 Id INTEGER PRIMARY KEY, Path TEXT NOT NULL UNIQUE, ParentPath TEXT NOT NULL, Name TEXT NOT NULL,
 Kind TEXT NOT NULL CHECK(Kind IN ('folder','image')), Size INTEGER NOT NULL DEFAULT 0,
 ModifiedUtc TEXT NOT NULL, ExistsNow INTEGER NOT NULL DEFAULT 1);
INSERT INTO Assets_new(Id,Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow)
SELECT Id,Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow FROM Assets WHERE Kind IN ('folder','image');
DROP TABLE Assets;
ALTER TABLE Assets_new RENAME TO Assets;
CREATE INDEX IF NOT EXISTS IX_Assets_ParentPath ON Assets(ParentPath);
CREATE INDEX IF NOT EXISTS IX_Assets_Name ON Assets(Name);";
        cmd2.ExecuteNonQuery(); tx.Commit();
    }

    /// <summary>
    /// 老库的 Tags 表没有 Category 列（分类是后加的功能）。
    /// CREATE TABLE IF NOT EXISTS 不会给已存在的表补列，所以这里显式补一次。
    /// </summary>
    /// <summary>
    /// 分类以前只是 Tags.Category 里的文本。为了让分类能独立存在（先建分类、暂时不挂标签），
    /// 新增 Categories 表；启动时把现有标签用到的分类补进去。
    /// </summary>
    private static void SeedCategories(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Categories(Name) SELECT DISTINCT TRIM(Category) FROM Tags WHERE TRIM(Category) <> ''";
        cmd.ExecuteNonQuery();
    }

    private static void MigrateTagCategoryColumn(SqliteConnection c)
    {
        bool hasCategory = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Tags)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (string.Equals(r.GetString(1), "Category", StringComparison.OrdinalIgnoreCase)) { hasCategory = true; break; }
        }
        if (hasCategory) return;
        using var alter = c.CreateCommand();
        alter.CommandText = "ALTER TABLE Tags ADD COLUMN Category TEXT NOT NULL DEFAULT ''";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 识别文件相关的列：WorkId（作品编号）、Authors（作者）、GlobalIndex（站内全局序号）。
    /// 老库没有这些列，补上；已有记录需要重扫才会回填。
    /// </summary>
    private static void MigrateWorkMetadataColumns(SqliteConnection c)
    {
        bool hasWorkId = false, hasAuthors = false, hasGlobalIndex = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Assets)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (string.Equals(name, "WorkId", StringComparison.OrdinalIgnoreCase)) hasWorkId = true;
                if (string.Equals(name, "Authors", StringComparison.OrdinalIgnoreCase)) hasAuthors = true;
                if (string.Equals(name, "GlobalIndex", StringComparison.OrdinalIgnoreCase)) hasGlobalIndex = true;
            }
        }
        if (!hasWorkId)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE Assets ADD COLUMN WorkId TEXT";
            alter.ExecuteNonQuery();
        }
        if (!hasAuthors)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE Assets ADD COLUMN Authors TEXT";
            alter.ExecuteNonQuery();
        }
        if (!hasGlobalIndex)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE Assets ADD COLUMN GlobalIndex INTEGER";
            alter.ExecuteNonQuery();
        }

        // 第一次升级到带识别文件的版本：把现有库里的文件夹就地补一遍，
        // 用户不用为了看到新的标题/顺序而重新扫描整个目录。
        if (!hasWorkId || !hasAuthors || !hasGlobalIndex) BackfillWorkMetadata(c);
    }

    /// <summary>升级时按现有 Path 读一次 work-id.json，能读到的就覆盖旧的目录名解析结果。</summary>
    private static void BackfillWorkMetadata(SqliteConnection c)
    {
        var rows = new List<(long Id, string Path, string Name)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Id, Path, Name FROM Assets WHERE Kind='folder'";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }
        if (rows.Count == 0) return;

        using var tx = c.BeginTransaction();
        using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = @"UPDATE Assets SET FavPage=$fp, FavPos=$fpos, SortTitle=$st,
                           WorkId=$wid, Authors=$authors, GlobalIndex=$gi WHERE Id=$id";
        var pFp = up.Parameters.Add("$fp", SqliteType.Integer);
        var pFpos = up.Parameters.Add("$fpos", SqliteType.Integer);
        var pSt = up.Parameters.Add("$st", SqliteType.Text);
        var pWid = up.Parameters.Add("$wid", SqliteType.Text);
        var pAuthors = up.Parameters.Add("$authors", SqliteType.Text);
        var pGi = up.Parameters.Add("$gi", SqliteType.Integer);
        var pId = up.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, path, name) in rows)
        {
            var meta = WorkMetadata.TryRead(path);
            if (meta is null) continue;
            var parsed = FavoriteOrder.Parse(name);
            var favPage = meta.FavoritePage ?? parsed?.Page;
            var favPos = meta.FavoritePosition ?? (meta.FavoritePage is not null ? 0 : parsed?.Position);
            pFp.Value = favPage.HasValue ? favPage.Value : (object)DBNull.Value;
            pFpos.Value = favPos.HasValue ? favPos.Value : (object)DBNull.Value;
            pSt.Value = string.IsNullOrWhiteSpace(meta.Title) ? FavoriteOrder.TitleOf(name) : meta.Title!;
            pWid.Value = (object?)meta.WorkId ?? DBNull.Value;
            pAuthors.Value = (object?)meta.AuthorsText ?? DBNull.Value;
            pGi.Value = meta.GlobalIndex is int gi ? (object)gi : DBNull.Value;
            pId.Value = id;
            up.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var cmd=c.CreateCommand(); cmd.CommandText="PRAGMA foreign_keys=ON;"; cmd.ExecuteNonQuery(); return c; }

    /// <summary>
    /// 排序用的三列：收藏夹页码、页内序号、标题（首字母排序用）。
    /// 老库没有这些列，补上并**直接从目录名回填**，这样不用等用户重扫一次就能按新规则排序。
    /// </summary>
    private static void MigrateSortColumns(SqliteConnection c)
    {
        bool hasFav = false, hasTitle = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Assets)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (string.Equals(name, "FavPage", StringComparison.OrdinalIgnoreCase)) hasFav = true;
                if (string.Equals(name, "SortTitle", StringComparison.OrdinalIgnoreCase)) hasTitle = true;
            }
        }
        if (!hasFav)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE Assets ADD COLUMN FavPage INTEGER; ALTER TABLE Assets ADD COLUMN FavPos INTEGER;";
            alter.ExecuteNonQuery();
        }
        if (!hasTitle)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE Assets ADD COLUMN SortTitle TEXT;";
            alter.ExecuteNonQuery();
        }
        BackfillSortColumns(c);
    }

    /// <summary>
    /// 按名字把几个排序列补出来。用 <c>SortTitle IS NULL</c> 判断「还没补过」——
    /// FavPage 对不符合命名规则的目录本来就是 NULL，不能拿它当标记。
    /// </summary>
    private static void BackfillSortColumns(SqliteConnection c)
    {
        var rows = new List<(long Id, string Name)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Id, Name FROM Assets WHERE SortTitle IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        if (rows.Count == 0) return;

        using var tx = c.BeginTransaction();
        using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = "UPDATE Assets SET FavPage=$p, FavPos=$pos, SortTitle=$t WHERE Id=$id";
        var pPage = up.Parameters.Add("$p", SqliteType.Integer);
        var pPos = up.Parameters.Add("$pos", SqliteType.Integer);
        var pTitle = up.Parameters.Add("$t", SqliteType.Text);
        var pId = up.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, name) in rows)
        {
            var parsed = FavoriteOrder.Parse(name);
            pPage.Value = parsed.HasValue ? parsed.Value.Page : (object)DBNull.Value;
            pPos.Value = parsed.HasValue ? parsed.Value.Position : (object)DBNull.Value;
            pTitle.Value = FavoriteOrder.TitleOf(name);
            pId.Value = id;
            up.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>各排序方式对应的 SQL 子句。</summary>
    private static string OrderByClause(SortMode sort) => sort switch
    {
        SortMode.TitleAsc => "ORDER BY a.SortTitle COLLATE NOCASE, a.Name COLLATE NOCASE",
        SortMode.TitleDesc => "ORDER BY a.SortTitle COLLATE NOCASE DESC, a.Name COLLATE NOCASE DESC",
        // 识别文件里的 GlobalIndex：数字小的在前，没有识别文件的排最后。
        SortMode.GlobalIndex => "ORDER BY (a.GlobalIndex IS NULL), a.GlobalIndex, a.Name COLLATE NOCASE",
        // 倒序：页码大的在前，同一页里序号大的在前；解析不出的仍然排最后。
        SortMode.FavoriteOrderDesc => "ORDER BY (a.FavPage IS NULL), a.FavPage DESC, a.FavPos DESC, a.Name COLLATE NOCASE DESC",
        // 默认：解析得出 {页码,序号} 的排前面，页码小的在前，同页里序号小的在前，其余按名称。
        _ => "ORDER BY (a.FavPage IS NULL), a.FavPage, a.FavPos, a.Name COLLATE NOCASE",
    };

    /// <summary>
    /// 扫描一个根目录。返回**清理掉的、磁盘上已不存在的记录条数**。
    /// 重复扫描同一个目录时，已经不存在的文件夹会被真正删除（含标签关联），不再只是标记。
    /// </summary>
    public int AddRootAndScan(string root, Action<string>? progress = null)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var c = Open();
        using (var cmd = c.CreateCommand()) { cmd.CommandText="INSERT OR IGNORE INTO Roots(Path) VALUES($p)"; cmd.Parameters.AddWithValue("$p",root); cmd.ExecuteNonQuery(); }

        // 用户从工具里移除过的条目，重扫时不要再收回来。
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Path FROM HiddenPaths";
            using var r = cmd.ExecuteReader();
            while (r.Read()) hidden.Add(r.GetString(0));
        }

        using var tx = c.BeginTransaction();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Upsert(string path, string kind, long size, DateTime modified)
        {
            path = Path.GetFullPath(path); seen.Add(path);
            var name = Path.GetFileName(path);
            // 优先用 work-id.json 的识别信息；没有或损坏时回退到目录名末尾的 {页数,位置}。
            var meta = kind == "folder" ? WorkMetadata.TryRead(path) : null;
            var parsed = FavoriteOrder.Parse(name);
            var favPage = meta?.FavoritePage ?? parsed?.Page;
            var favPos = meta?.FavoritePosition ?? (meta?.FavoritePage is not null ? 0 : parsed?.Position);
            var sortTitle = string.IsNullOrWhiteSpace(meta?.Title) ? FavoriteOrder.TitleOf(name) : meta!.Title!;
            using var cmd = c.CreateCommand(); cmd.Transaction=tx;
            cmd.CommandText=@"INSERT INTO Assets(Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow,FavPage,FavPos,SortTitle,WorkId,Authors,GlobalIndex)
VALUES($p,$parent,$name,$kind,$size,$modified,1,$favPage,$favPos,$sortTitle,$workId,$authors,$globalIndex)
ON CONFLICT(Path) DO UPDATE SET ParentPath=excluded.ParentPath,Name=excluded.Name,Kind=excluded.Kind,Size=excluded.Size,ModifiedUtc=excluded.ModifiedUtc,ExistsNow=1,FavPage=excluded.FavPage,FavPos=excluded.FavPos,SortTitle=excluded.SortTitle,WorkId=excluded.WorkId,Authors=excluded.Authors,GlobalIndex=excluded.GlobalIndex;";
            cmd.Parameters.AddWithValue("$p",path); cmd.Parameters.AddWithValue("$parent",Path.GetDirectoryName(path) ?? ""); cmd.Parameters.AddWithValue("$name",name); cmd.Parameters.AddWithValue("$kind",kind); cmd.Parameters.AddWithValue("$size",size); cmd.Parameters.AddWithValue("$modified",modified.ToUniversalTime().ToString("O"));
            cmd.Parameters.Add("$favPage", SqliteType.Integer).Value = favPage.HasValue ? favPage.Value : (object)DBNull.Value;
            cmd.Parameters.Add("$favPos", SqliteType.Integer).Value = favPos.HasValue ? favPos.Value : (object)DBNull.Value;
            cmd.Parameters.AddWithValue("$sortTitle", sortTitle);
            cmd.Parameters.AddWithValue("$workId", (object?)meta?.WorkId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authors", (object?)meta?.AuthorsText ?? DBNull.Value);
            cmd.Parameters.Add("$globalIndex", SqliteType.Integer).Value = meta?.GlobalIndex is int gi ? (object)gi : DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        var stack = new Stack<string>(); stack.Push(root.TrimEnd(Path.DirectorySeparatorChar)); int folderCount=0;
        while(stack.Count>0)
        {
            var dir=stack.Pop();
            // 整个目录被移除过就不要再深入——否则它里面新增的子目录又会被收回来。
            if (hidden.Contains(dir)) continue;
            try
            {
                var di=new DirectoryInfo(dir);
                Upsert(dir,"folder",0,di.LastWriteTimeUtc);
                folderCount++;
                if(folderCount%200==0) progress?.Invoke($"已索引 {folderCount:N0} 个文件夹…");
                foreach(var sub in Directory.EnumerateDirectories(dir)) { try { if ((File.GetAttributes(sub)&FileAttributes.ReparsePoint)==0) stack.Push(sub); } catch {} }
            }
            catch { continue; }
        }
        using(var cmd=c.CreateCommand()) { cmd.Transaction=tx; cmd.CommandText="UPDATE Assets SET ExistsNow=0 WHERE (Path=$root OR Path LIKE $prefix ESCAPE '\\')"; cmd.Parameters.AddWithValue("$root",root.TrimEnd(Path.DirectorySeparatorChar)); cmd.Parameters.AddWithValue("$prefix",EscapeLike(root)+"%"); cmd.ExecuteNonQuery(); }
        foreach(var p in seen) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE Assets SET ExistsNow=1 WHERE Path=$p"; cmd.Parameters.AddWithValue("$p",p); cmd.ExecuteNonQuery(); }

        // 重扫同一个目录时，这个根下面已经不在磁盘上的记录直接删掉——标签关联随 ON DELETE CASCADE 一起清。
        // 删之前再确认一次磁盘上确实没有了：外接盘临时没挂上时不该把记录清空。
        var stale = new List<(long Id, string Path)>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT Id, Path FROM Assets WHERE ExistsNow=0 AND (Path=$root OR Path LIKE $prefix ESCAPE '\\')";
            q.Parameters.AddWithValue("$root", root.TrimEnd(Path.DirectorySeparatorChar));
            q.Parameters.AddWithValue("$prefix", EscapeLike(root) + "%");
            using var r = q.ExecuteReader();
            while (r.Read()) stale.Add((r.GetInt64(0), r.GetString(1)));
        }
        int purged = 0;
        foreach (var (id, path) in stale)
        {
            try { if (Directory.Exists(path) || File.Exists(path)) continue; } catch { continue; }
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Assets WHERE Id=$id";
            del.Parameters.AddWithValue("$id", id);
            purged += del.ExecuteNonQuery();
        }

        tx.Commit();
        progress?.Invoke(purged > 0
            ? $"扫描完成：{folderCount:N0} 个文件夹；已清理 {purged:N0} 个磁盘上已不存在的记录。"
            : $"扫描完成：{folderCount:N0} 个文件夹。");
        return purged;
    }
    private static string EscapeLike(string s)=>s.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_");

    // ---------- 标签 ----------

    /// <summary>所有标签，带分类和「有多少素材在用」。未分类的排在最后。</summary>
    public List<TagInfo> GetTags()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText=@"SELECT t.Id, t.Name, t.Category,
 (SELECT COUNT(*) FROM AssetTags at WHERE at.TagId=t.Id)
 FROM Tags t
 ORDER BY CASE WHEN TRIM(t.Category)='' THEN 1 ELSE 0 END, t.Category COLLATE NOCASE, t.Name COLLATE NOCASE";
        using var r=cmd.ExecuteReader();
        var list=new List<TagInfo>();
        while(r.Read()) list.Add(new TagInfo{ Id=r.GetInt64(0), Name=r.GetString(1), Category=r.GetString(2), UsageCount=r.GetInt32(3) });
        return list;
    }

    public long EnsureTag(string name, string? category = null)
    {
        name=name.Trim(); if(name.Length==0) throw new ArgumentException("标签名不能为空");
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText = category is null
            ? "INSERT OR IGNORE INTO Tags(Name) VALUES($n); SELECT Id FROM Tags WHERE Name=$n COLLATE NOCASE"
            : "INSERT OR IGNORE INTO Tags(Name,Category) VALUES($n,$cat); SELECT Id FROM Tags WHERE Name=$n COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$n",name);
        if (category is not null) cmd.Parameters.AddWithValue("$cat",category.Trim());
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        var categoryName = (category ?? "").Trim();
        if (categoryName.Length > 0)
        {
            using var cat = c.CreateCommand();
            cat.CommandText = "INSERT OR IGNORE INTO Categories(Name) VALUES($cat)";
            cat.Parameters.AddWithValue("$cat", categoryName);
            cat.ExecuteNonQuery();
        }
        return id;
    }

    /// <summary>所有分类（包括先建好、暂时没有标签的分类），按名称排序。</summary>
    public List<string> GetCategories()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // 老库的 Categories 可能还没补齐，所以把 Tags.Category 也并进来。
        cmd.CommandText = @"SELECT Name FROM Categories
                            UNION
                            SELECT DISTINCT TRIM(Category) FROM Tags WHERE TRIM(Category) <> ''
                            ORDER BY Name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var name = r.GetString(0).Trim();
            if (name.Length > 0 && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>先建一个分类；标签本身仍然只引用分类名，不强制先建后用。</summary>
    public void EnsureCategory(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Categories(Name) VALUES($n)";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    public void RenameTag(long tagId, string newName)
    {
        newName=newName.Trim(); if(newName.Length==0) throw new ArgumentException("标签名不能为空");
        using var c=Open();
        using (var check=c.CreateCommand())
        {
            check.CommandText="SELECT COUNT(*) FROM Tags WHERE Name=$n COLLATE NOCASE AND Id<>$id";
            check.Parameters.AddWithValue("$n",newName); check.Parameters.AddWithValue("$id",tagId);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) throw new InvalidOperationException($"已经有一个叫「{newName}」的标签了。");
        }
        using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE Tags SET Name=$n WHERE Id=$id";
        cmd.Parameters.AddWithValue("$n",newName); cmd.Parameters.AddWithValue("$id",tagId);
        cmd.ExecuteNonQuery();
    }

    public void SetTagCategory(long tagId, string category)
    {
        var categoryName = (category ?? "").Trim();
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE Tags SET Category=$cat WHERE Id=$id";
        cmd.Parameters.AddWithValue("$cat",categoryName); cmd.Parameters.AddWithValue("$id",tagId);
        cmd.ExecuteNonQuery();
        if (categoryName.Length > 0)
        {
            using var cat = c.CreateCommand();
            cat.CommandText = "INSERT OR IGNORE INTO Categories(Name) VALUES($cat)";
            cat.Parameters.AddWithValue("$cat", categoryName);
            cat.ExecuteNonQuery();
        }
    }

    /// <summary>删除标签。AssetTags 上有 ON DELETE CASCADE，关联会一起清掉。</summary>
    public void DeleteTag(long tagId)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="DELETE FROM Tags WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",tagId);
        cmd.ExecuteNonQuery();
    }

    public void ApplyTag(long assetId,string tag,bool remove=false){ using var c=Open(); using var cmd=c.CreateCommand(); if(remove){cmd.CommandText="DELETE FROM AssetTags WHERE AssetId=$a AND TagId=(SELECT Id FROM Tags WHERE Name=$n COLLATE NOCASE)";}else{cmd.CommandText="INSERT OR IGNORE INTO AssetTags(AssetId,TagId) SELECT $a,Id FROM Tags WHERE Name=$n COLLATE NOCASE";} cmd.Parameters.AddWithValue("$a",assetId);cmd.Parameters.AddWithValue("$n",tag);cmd.ExecuteNonQuery(); }

    /// <summary>
    /// 读出所有打过标签的条目：键为 AssetId，值为标签名（已按名称排序）。
    /// 结果卡片的标签芯片、以及标签对话框里「这个素材已有哪些标签」都靠它。
    /// </summary>
    public Dictionary<long,List<string>> GetAllAssetTags()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT at.AssetId, t.Name FROM AssetTags at JOIN Tags t ON t.Id=at.TagId ORDER BY t.Name COLLATE NOCASE";
        using var r=cmd.ExecuteReader();
        var map=new Dictionary<long,List<string>>();
        while(r.Read()){ var id=r.GetInt64(0); if(!map.TryGetValue(id,out var list)) map[id]=list=new List<string>(); list.Add(r.GetString(1)); }
        return map;
    }

    // ---------- 搜索（带分页）----------

    /// <summary>把筛选条件拼成 WHERE 子句和对应参数，供计数与取数两条语句共用。</summary>
    private static (string Where, List<(string Name, object Value)> Args) BuildFilter(
        string keyword, IReadOnlyCollection<long> tagIds, bool matchAll)
    {
        // 只处理文件夹：图片不再进索引，也不会出现在结果里。
        var where=new List<string>{"a.ExistsNow=1","a.Kind='folder'",
            "NOT EXISTS(SELECT 1 FROM HiddenPaths h WHERE h.Path=a.Path)"};
        var args=new List<(string,object)>();
        if(!string.IsNullOrWhiteSpace(keyword)){where.Add("(a.Name LIKE $kw ESCAPE '\\' OR a.Path LIKE $kw ESCAPE '\\' OR a.SortTitle LIKE $kw ESCAPE '\\' OR a.WorkId LIKE $kw ESCAPE '\\' OR a.Authors LIKE $kw ESCAPE '\\')");args.Add(("$kw","%"+EscapeLike(keyword.Trim())+"%"));}
        if(tagIds.Count>0)
        {
            var ids=string.Join(",",tagIds);
            if(matchAll){where.Add($@"(SELECT COUNT(DISTINCT effective.TagId) FROM (SELECT t.TagId AS TagId FROM AssetTags t WHERE t.AssetId=a.Id UNION SELECT t.TagId AS TagId FROM AssetTags t JOIN Assets f ON f.Id=t.AssetId WHERE f.Kind='folder' AND (a.Path=f.Path OR substr(a.Path,1,length(f.Path)+1)=f.Path||char(92))) effective WHERE effective.TagId IN ({ids})) >= {tagIds.Count}");}
            else {where.Add($@"(EXISTS(SELECT 1 FROM AssetTags t WHERE t.AssetId=a.Id AND t.TagId IN ({ids})) OR EXISTS(SELECT 1 FROM AssetTags t JOIN Assets f ON f.Id=t.AssetId WHERE f.Kind='folder' AND (a.Path=f.Path OR substr(a.Path,1,length(f.Path)+1)=f.Path||char(92)) AND t.TagId IN ({ids})))");}
        }
        return (string.Join(" AND ",where), args);
    }

    /// <summary>分页查询。返回当前页的条目和符合条件总数。</summary>
    public (List<AssetItem> Items, int Total) SearchPage(string keyword, IReadOnlyCollection<long> tagIds, bool matchAll, SortMode sort, int offset, int limit)
    {
        if (offset < 0) offset = 0;
        if (limit <= 0) limit = 60;
        var (whereSql, args) = BuildFilter(keyword, tagIds, matchAll);

        using var c=Open();
        int total;
        using (var count=c.CreateCommand())
        {
            count.CommandText=$"SELECT COUNT(*) FROM Assets a WHERE {whereSql}";
            foreach(var (n,v) in args) count.Parameters.AddWithValue(n,v);
            total=Convert.ToInt32(count.ExecuteScalar() ?? 0);
        }

        var result=new List<AssetItem>();
        using (var cmd=c.CreateCommand())
        {
            cmd.CommandText=$"SELECT a.Id,a.Path,a.Name,a.Kind,a.Size,a.ModifiedUtc FROM Assets a WHERE {whereSql} {OrderByClause(sort)} LIMIT $limit OFFSET $offset";
            foreach(var (n,v) in args) cmd.Parameters.AddWithValue(n,v);
            cmd.Parameters.AddWithValue("$limit",limit);
            cmd.Parameters.AddWithValue("$offset",offset);
            using var r=cmd.ExecuteReader();
            while(r.Read()) result.Add(new AssetItem{Id=r.GetInt64(0),Path=r.GetString(1),Name=r.GetString(2),Kind=r.GetString(3),Size=r.GetInt64(4),Modified=DateTime.Parse(r.GetString(5)).ToLocalTime()});
        }
        if(result.Count>0){ var tagMap=GetAllAssetTags(); foreach(var item in result) if(tagMap.TryGetValue(item.Id,out var names)) item.Tags=names; }
        return (result, total);
    }

    /// <summary>不分页的整取版本，供脚本和测试使用。</summary>
    public List<AssetItem> Search(string keyword, IReadOnlyCollection<long> tagIds, bool matchAll)
        => SearchPage(keyword, tagIds, matchAll, SortMode.FavoriteOrder, 0, 100000).Items;

    /// <summary>不分页的整取版本，可指定排序方式。</summary>
    public List<AssetItem> Search(string keyword, IReadOnlyCollection<long> tagIds, bool matchAll, SortMode sort)
        => SearchPage(keyword, tagIds, matchAll, sort, 0, 100000).Items;

    // ---------- 从工具中移除（不碰磁盘文件）----------

    /// <summary>
    /// 把条目从工具里移除：路径记进 HiddenPaths（重扫也不会再回来），
    /// 并删掉索引行（标签关联随 ON DELETE CASCADE 一起清）。
    /// 选中的是文件夹时，连同它内部当前已索引的项目一起移除。
    /// 磁盘上的原始文件完全不动。
    /// </summary>
    public int RemoveFromLibrary(IReadOnlyCollection<long> ids, bool hidePaths = true)
    {
        if (ids.Count == 0) return 0;
        var idList = string.Join(",", ids);
        using var c = Open();

        var paths = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT Path FROM Assets WHERE Id IN ({idList})";
            using var r = q.ExecuteReader();
            while (r.Read()) paths.Add(r.GetString(0));
        }
        if (paths.Count == 0) return 0;

        var all = new List<string>(paths);
        using (var sub = c.CreateCommand())
        {
            var clauses = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                clauses.Add("(a.Path LIKE $p" + i + " ESCAPE '\\')");
                sub.Parameters.AddWithValue("$p" + i, EscapeLike(paths[i].TrimEnd('\\') + "\\") + "%");
            }
            sub.CommandText = $"SELECT a.Path FROM Assets a WHERE {string.Join(" OR ", clauses)}";
            using var r = sub.ExecuteReader();
            while (r.Read())
            {
                var p = r.GetString(0);
                if (!all.Contains(p, StringComparer.OrdinalIgnoreCase)) all.Add(p);
            }
        }

        using var tx = c.BeginTransaction();
        if (hidePaths)
        {
            using var hide = c.CreateCommand();
            hide.Transaction = tx;
            hide.CommandText = "INSERT OR IGNORE INTO HiddenPaths(Path) VALUES($p)";
            hide.Parameters.Add("$p", SqliteType.Text);
            foreach (var p in all)
            {
                hide.Parameters["$p"].Value = p;
                hide.ExecuteNonQuery();
            }
        }

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            if (hidePaths)
            {
                // 原来的「从工具中删除」：只删选中行，子项靠隐藏列表挡住。
                del.CommandText = $"DELETE FROM Assets WHERE Id IN ({idList})";
            }
            else
            {
                // 磁盘文件夹已经删掉：按路径前缀删掉选中行和所有子项，也不用写隐藏列表；
                // 以后从回收站恢复再扫描时可以重新收进来。
                var clauses = new List<string>();
                for (int i = 0; i < paths.Count; i++)
                {
                    clauses.Add($"(Path=$d{i} OR Path LIKE $q{i} ESCAPE '\\')");
                    del.Parameters.AddWithValue("$d" + i, paths[i]);
                    del.Parameters.AddWithValue("$q" + i, EscapeLike(paths[i].TrimEnd('\\') + "\\") + "%");
                }
                del.CommandText = $"DELETE FROM Assets WHERE {string.Join(" OR ", clauses)}";
            }
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return all.Count;
    }

    public int GetHiddenCount()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM HiddenPaths";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>清空隐藏列表。之后需要重新扫描目录才能把索引补回来。</summary>
    public int RestoreAllHidden()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM HiddenPaths";
        return cmd.ExecuteNonQuery();
    }

    public List<AssetItem> GetFolders(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,Path,Name,Kind,Size,ModifiedUtc FROM Assets WHERE Kind='folder' AND ExistsNow=1 AND NOT EXISTS(SELECT 1 FROM HiddenPaths h WHERE h.Path=Assets.Path) ORDER BY Path LIMIT 5000";using var r=cmd.ExecuteReader();var list=new List<AssetItem>();while(r.Read())list.Add(new AssetItem{Id=r.GetInt64(0),Path=r.GetString(1),Name=r.GetString(2),Kind=r.GetString(3),Size=0,Modified=DateTime.Parse(r.GetString(5)).ToLocalTime()});return list;}
}
