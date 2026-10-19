using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalAssetSearch.Services;

/// <summary>
/// 「标签 + 文件列表」的可移植备份：另存为 JSON 文档，或从文档读取回来。
///
/// 只备份索引和标签（文件夹路径、标签定义、标签关联、根目录、隐藏路径），
/// **不备份图片、不移动磁盘目录、不备份缩略图**。
/// 读取是合并：已有的同路径文件夹更新信息，已有的同名标签复用，不会清空当前库。
/// </summary>
public static class LibraryArchive
{
    public const string AppId = "LocalAssetSearch";
    public const int FormatVersion = 1;
    public const string FileExtension = ".laslib.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Document
    {
        public string App { get; set; } = AppId;
        public int FormatVersion { get; set; } = LibraryArchive.FormatVersion;
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        public List<string> Roots { get; set; } = new();
        public List<TagRecord> Tags { get; set; } = new();
        public List<FolderRecord> Folders { get; set; } = new();
        public List<string> HiddenPaths { get; set; } = new();
    }

    public sealed class TagRecord
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Color { get; set; } = "#64748b";
    }

    public sealed class FolderRecord
    {
        public string Path { get; set; } = "";
        public string ParentPath { get; set; } = "";
        public string Name { get; set; } = "";
        public int? FavPage { get; set; }
        public int? FavPos { get; set; }
        public string? SortTitle { get; set; }
        public List<TagRef> Tags { get; set; } = new();
    }

    public sealed class TagRef
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
    }

    public sealed record ImportResult(
        int FoldersAdded,
        int FoldersUpdated,
        int TagsAdded,
        int TagsExisting,
        int LinksAdded,
        int RootsAdded,
        int HiddenAdded,
        int MissingFolders);

    /// <summary>把当前库读成一个可序列化的文档。</summary>
    public static Document Export(Database db)
    {
        var doc = new Document();
        using var c = db.Open();

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Path FROM Roots ORDER BY Path";
            using var r = q.ExecuteReader();
            while (r.Read()) doc.Roots.Add(r.GetString(0));
        }

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Name, Category, Color FROM Tags ORDER BY Name COLLATE NOCASE";
            using var r = q.ExecuteReader();
            while (r.Read())
                doc.Tags.Add(new TagRecord
                {
                    Name = r.GetString(0),
                    Category = r.GetString(1),
                    Color = r.GetString(2),
                });
        }

        // 一次把「素材 → 标签」全读出来，避免每个文件夹都查一次。
        var tagsByAsset = new Dictionary<long, List<TagRef>>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = @"SELECT at.AssetId, t.Name, t.Category
                              FROM AssetTags at JOIN Tags t ON t.Id = at.TagId
                              ORDER BY t.Name COLLATE NOCASE";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (!tagsByAsset.TryGetValue(id, out var list)) tagsByAsset[id] = list = new List<TagRef>();
                list.Add(new TagRef { Name = r.GetString(1), Category = r.GetString(2) });
            }
        }

        using (var q = c.CreateCommand())
        {
            q.CommandText = @"SELECT Id, Path, ParentPath, Name, FavPage, FavPos, SortTitle
                              FROM Assets WHERE Kind='folder' ORDER BY Path";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var folder = new FolderRecord
                {
                    Path = r.GetString(1),
                    ParentPath = r.GetString(2),
                    Name = r.GetString(3),
                };
                if (!r.IsDBNull(4)) folder.FavPage = r.GetInt32(4);
                if (!r.IsDBNull(5)) folder.FavPos = r.GetInt32(5);
                if (!r.IsDBNull(6)) folder.SortTitle = r.GetString(6);
                if (tagsByAsset.TryGetValue(r.GetInt64(0), out var tags)) folder.Tags = tags;
                doc.Folders.Add(folder);
            }
        }

        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Path FROM HiddenPaths ORDER BY Path";
            using var r = q.ExecuteReader();
            while (r.Read()) doc.HiddenPaths.Add(r.GetString(0));
        }

        return doc;
    }

    /// <summary>写成 UTF-8 JSON（不转义中文，方便记事本直接看）。</summary>
    public static void Save(Document document, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));

    /// <summary>读取并校验文档。不是本程序的备份会抛 <see cref="InvalidDataException"/>。</summary>
    public static Document Load(string path)
    {
        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"不是有效的库备份 JSON：{ex.Message}", ex);
        }

        if (doc is null) throw new InvalidDataException("库备份是空的。");
        if (!string.Equals(doc.App, AppId, StringComparison.Ordinal))
            throw new InvalidDataException("这个文件不是 LocalAssetSearch 的库备份。");
        if (doc.FormatVersion < 1 || doc.FormatVersion > FormatVersion)
            throw new InvalidDataException($"不支持的库备份版本：{doc.FormatVersion}（本程序支持 1）。");

        // JSON 里显式写了 null 时，容错成空集合。
        doc.Roots ??= new();
        doc.Tags ??= new();
        doc.Folders ??= new();
        doc.HiddenPaths ??= new();
        return doc;
    }

    /// <summary>
    /// 合并导入：不删除当前库里的东西。同路径文件夹更新，同名标签复用并补空分类，
    /// 标签关联用 INSERT OR IGNORE，所以同一份文档反复读取不会产生重复。
    /// </summary>
    public static ImportResult Import(Database db, Document doc)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var tagIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int foldersAdded = 0, foldersUpdated = 0, tagsAdded = 0, tagsExisting = 0;
        int linksAdded = 0, rootsAdded = 0, hiddenAdded = 0, missingFolders = 0;

        long GetTagId(string name, string category)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return 0;
            if (tagIds.TryGetValue(name, out var known)) return known;

            long id = 0;
            using (var sel = c.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT Id FROM Tags WHERE Name=$n COLLATE NOCASE";
                sel.Parameters.AddWithValue("$n", name);
                var found = sel.ExecuteScalar();
                if (found is not null)
                {
                    id = Convert.ToInt64(found);
                    tagsExisting++;
                    // 只补空分类，不覆盖用户后来改过的分类。
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        using var fix = c.CreateCommand();
                        fix.Transaction = tx;
                        fix.CommandText = "UPDATE Tags SET Category=$cat WHERE Id=$id AND TRIM(Category)=''";
                        fix.Parameters.AddWithValue("$cat", category.Trim());
                        fix.Parameters.AddWithValue("$id", id);
                        fix.ExecuteNonQuery();
                    }
                }
            }

            if (id == 0)
            {
                using (var ins = c.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO Tags(Name,Category) VALUES($n,$cat)";
                    ins.Parameters.AddWithValue("$n", name);
                    ins.Parameters.AddWithValue("$cat", (category ?? "").Trim());
                    ins.ExecuteNonQuery();
                }
                using (var sel = c.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT last_insert_rowid()";
                    id = Convert.ToInt64(sel.ExecuteScalar());
                }
                tagsAdded++;
            }

            tagIds[name] = id;
            return id;
        }

        foreach (var t in doc.Tags)
        {
            if (t is null) continue;
            GetTagId(t.Name, t.Category);
        }

        foreach (var f in doc.Folders)
        {
            if (f is null) continue;
            var path = (f.Path ?? "").Trim();
            if (path.Length == 0) continue;

            var parent = string.IsNullOrWhiteSpace(f.ParentPath)
                ? (Path.GetDirectoryName(path) ?? "")
                : f.ParentPath.Trim();
            var name = string.IsNullOrWhiteSpace(f.Name)
                ? (Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? "")
                : f.Name.Trim();

            bool exists;
            try { exists = Directory.Exists(path); } catch { exists = false; }
            if (!exists) missingFolders++;

            long assetId = 0;
            using (var sel = c.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT Id FROM Assets WHERE Path=$p COLLATE NOCASE";
                sel.Parameters.AddWithValue("$p", path);
                var found = sel.ExecuteScalar();
                if (found is not null) assetId = Convert.ToInt64(found);
            }

            var modified = DateTime.UtcNow.ToString("O");
            if (assetId == 0)
            {
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO Assets(Path,ParentPath,Name,Kind,Size,ModifiedUtc,ExistsNow,FavPage,FavPos,SortTitle)
                                    VALUES($p,$pp,$n,'folder',0,$m,$e,$fp,$fpos,$st)";
                ins.Parameters.AddWithValue("$p", path);
                ins.Parameters.AddWithValue("$pp", parent);
                ins.Parameters.AddWithValue("$n", name);
                ins.Parameters.AddWithValue("$m", modified);
                ins.Parameters.AddWithValue("$e", exists ? 1 : 0);
                ins.Parameters.AddWithValue("$fp", (object?)f.FavPage ?? DBNull.Value);
                ins.Parameters.AddWithValue("$fpos", (object?)f.FavPos ?? DBNull.Value);
                ins.Parameters.AddWithValue("$st", (object?)f.SortTitle ?? DBNull.Value);
                ins.ExecuteNonQuery();

                using var sel = c.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT last_insert_rowid()";
                assetId = Convert.ToInt64(sel.ExecuteScalar());
                foldersAdded++;
            }
            else
            {
                using var up = c.CreateCommand();
                up.Transaction = tx;
                up.CommandText = @"UPDATE Assets
                                   SET ParentPath=$pp, Name=$n, ExistsNow=$e,
                                       FavPage=$fp, FavPos=$fpos, SortTitle=$st, ModifiedUtc=$m
                                   WHERE Id=$id";
                up.Parameters.AddWithValue("$pp", parent);
                up.Parameters.AddWithValue("$n", name);
                up.Parameters.AddWithValue("$e", exists ? 1 : 0);
                up.Parameters.AddWithValue("$fp", (object?)f.FavPage ?? DBNull.Value);
                up.Parameters.AddWithValue("$fpos", (object?)f.FavPos ?? DBNull.Value);
                up.Parameters.AddWithValue("$st", (object?)f.SortTitle ?? DBNull.Value);
                up.Parameters.AddWithValue("$m", modified);
                up.Parameters.AddWithValue("$id", assetId);
                up.ExecuteNonQuery();
                foldersUpdated++;
            }

            foreach (var tag in f.Tags ?? new List<TagRef>())
            {
                if (tag is null) continue;
                var tagId = GetTagId(tag.Name, tag.Category);
                if (tagId == 0) continue;
                using var link = c.CreateCommand();
                link.Transaction = tx;
                link.CommandText = "INSERT OR IGNORE INTO AssetTags(AssetId,TagId) VALUES($a,$t)";
                link.Parameters.AddWithValue("$a", assetId);
                link.Parameters.AddWithValue("$t", tagId);
                linksAdded += link.ExecuteNonQuery();
            }
        }

        foreach (var root in doc.Roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO Roots(Path) VALUES($p)";
            ins.Parameters.AddWithValue("$p", root.Trim());
            rootsAdded += ins.ExecuteNonQuery();
        }

        foreach (var hidden in doc.HiddenPaths)
        {
            if (string.IsNullOrWhiteSpace(hidden)) continue;
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO HiddenPaths(Path) VALUES($p)";
            ins.Parameters.AddWithValue("$p", hidden.Trim());
            hiddenAdded += ins.ExecuteNonQuery();
        }

        tx.Commit();
        return new ImportResult(foldersAdded, foldersUpdated, tagsAdded, tagsExisting, linksAdded, rootsAdded, hiddenAdded, missingFolders);
    }
}
