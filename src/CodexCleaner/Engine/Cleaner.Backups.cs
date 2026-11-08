using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace CodexCleaner.Engine;

public sealed record BackupChat(string Id, string Title, long Messages, string? BlockedReason);

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long Bytes, IReadOnlyList<BackupChat> Chats,
    bool ClearMemories, bool ClearSharedHistory, bool NeedsRecovery, string? Problem, bool Live);

public sealed record BackupRestorePreview(string Path, IReadOnlyList<BackupChat> Chats, IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings, int FileCount, long Bytes, bool RestoreMemories, bool RestoreSharedHistory, bool RecoverIncomplete);

public sealed record BackupRestoreResult(int Restored, IReadOnlyList<string> Skipped, bool RestoredMemories, bool RestoredSharedHistory);

internal sealed class BackupManifest
{
    public string Format { get; set; } = "codex-cleaner-backup-v1";
    public string Id { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "preparing";
    public bool Live { get; set; }
    public bool ClearMemories { get; set; }
    public bool ClearSharedHistory { get; set; }
    public Dictionary<string, string?> Schemas { get; set; } = [];
    public Dictionary<string, string> Migrations { get; set; } = [];
    public List<BackupChatRecord> Chats { get; set; } = [];
    public List<BackupFileRecord> Files { get; set; } = [];
    public List<BackupIndexRecord> Indexes { get; set; } = [];
    public List<BackupDesktopRecord> Desktop { get; set; } = [];
    public List<string> Completed { get; set; } = [];
    public Fingerprint? RowsFingerprint { get; set; }
    public long RowCount { get; set; }
    public string? Error { get; set; }
}

internal sealed class BackupChatRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long Messages { get; set; }
    public bool RecordOnly { get; set; }
    public List<string> Parents { get; set; } = [];
}

internal sealed class BackupFileRecord
{
    public string Path { get; set; } = "";
    public string Scope { get; set; } = "chat";
    public string Owner { get; set; } = "";
    public Fingerprint Before { get; set; } = new(false);
    public bool Remove { get; set; } = true;
    public Fingerprint? After { get; set; }
}

internal sealed class BackupIndexRecord
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public List<BackupIndexLine> Lines { get; set; } = [];
    public Fingerprint? JournalBefore { get; set; }
    public Fingerprint? JournalAfter { get; set; }
}

internal sealed class BackupIndexLine
{
    public string Owner { get; set; } = "";
    public string Text { get; set; } = "";
}

internal sealed class BackupDesktopRecord
{
    public string Path { get; set; } = "";
    public string Container { get; set; } = "";
    public string Before { get; set; } = "";
}

internal sealed class BackupCell
{
    public string Kind { get; set; } = "null";
    public string? Text { get; set; }
    public long Integer { get; set; }
    public double Real { get; set; }
    public byte[]? Blob { get; set; }

    public static BackupCell From(object? value) => value switch
    {
        null or DBNull => new(),
        long number => new() { Kind = "integer", Integer = number },
        double number => new() { Kind = "real", Real = number },
        byte[] bytes => new() { Kind = "blob", Blob = bytes },
        string text => new() { Kind = "text", Text = text },
        _ => throw new CleanerException("备份遇到不受支持的 SQLite 值类型。"),
    };

    [JsonIgnore]
    public object Value => Kind switch
    {
        "null" => DBNull.Value,
        "integer" => Integer,
        "real" when double.IsFinite(Real) => Real,
        "blob" when Blob is not null => Blob,
        "text" when Text is not null => Text,
        _ => throw new CleanerException("备份的 SQLite 值无效。"),
    };
}

internal sealed class BackupRow
{
    public string Database { get; set; } = "";
    public string Table { get; set; } = "";
    public ActionKind Kind { get; set; }
    public string Scope { get; set; } = "chat";
    public List<string> Owners { get; set; } = [];
    public List<string> Columns { get; set; } = [];
    public List<string> Keys { get; set; } = [];
    public List<BackupCell> Values { get; set; } = [];

    public object? Get(string column)
    {
        var index = Columns.IndexOf(column);
        return index < 0 || Values[index].Kind == "null" ? null : Values[index].Value;
    }
}

internal sealed class BackupPackage(string directory, BackupManifest manifest, List<BackupRow> rows)
{
    public string Directory { get; } = directory;
    public BackupManifest Manifest { get; } = manifest;
    public List<BackupRow> Rows { get; } = rows;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupRow))]
internal sealed partial class BackupJson : JsonSerializerContext;

public sealed partial class Cleaner
{
    public string BackupRoot => Path.Combine(Path.GetDirectoryName(TransactionRoot)!, "Backups");

    [GeneratedRegex(@"^sessions-\d{8}-\d{6}-[0-9a-f]{32}\z")]
    private static partial Regex BackupId();

    private string SafeBackupRoot()
    {
        var root = SafePaths.Normal(BackupRoot);
        if (SafePaths.Inside(DataRoot, root) || SafePaths.Inside(root, DataRoot)
            || SafePaths.Inside(TransactionRoot, root) || SafePaths.Inside(root, TransactionRoot))
            throw new CleanerException("备份目录与数据目录、临时事务目录不能互相包含。");
        return Safe(root, missing: true);
    }

    private string BackupDirectory(string path)
    {
        var root = SafeBackupRoot();
        var full = SafePaths.Normal(path);
        if (!SafePaths.Same(Path.GetDirectoryName(full)!, root) || !BackupId().IsMatch(Path.GetFileName(full)))
            throw new CleanerException("只能使用本工具备份目录中的备份。");
        return Safe(root, Path.GetFileName(full));
    }

    private Fingerprint BackupFingerprint(BackupPackage package, string relative) => FingerprintOf(package.Directory, relative);

    private void SaveBackupManifest(BackupPackage package) =>
        AtomicWrite(SafeBackupRoot(), package.Manifest.Id + "/manifest.json", JsonSerializer.SerializeToUtf8Bytes(package.Manifest, BackupJson.Default.BackupManifest));

    private void SetBackupStatus(BackupPackage package, string status, string? error = null)
    {
        var previous = package.Manifest.Status;
        var previousError = package.Manifest.Error;
        package.Manifest.Status = status;
        package.Manifest.Error = error;
        try { SaveBackupManifest(package); }
        catch { package.Manifest.Status = previous; package.Manifest.Error = previousError; throw; }
    }

    private void BackupStep(BackupPackage package, string step)
    {
        if (!package.Manifest.Completed.Contains(step)) package.Manifest.Completed.Add(step);
        SaveBackupManifest(package);
    }

    private void CheckBackupCompatibility(BackupManifest manifest)
    {
        AssertKnownBackupDatabases();
        foreach (var (rel, database) in InspectDatabases())
            if (database is { Present: true, Supported: false }) throw new CleanerException($"数据库或附属文件无法安全恢复：{rel}：{database.Reason}");
        if (!SafePaths.Same(SafePaths.Normal(manifest.DataRoot), DataRoot)) throw new CleanerException("备份不属于当前 Codex 数据目录，不能跨目录恢复。");
        if (manifest.Schemas.Count != Catalog.Schemas.Count || Catalog.Schemas.Keys.Any(rel => !manifest.Schemas.ContainsKey(rel)))
            throw new CleanerException("备份的数据库结构清单不完整。");
        foreach (var (rel, expected) in manifest.Schemas)
        {
            var entry = Stat(rel);
            if (expected is null)
            {
                if (entry is not null) throw new CleanerException($"数据库存在状态与备份时不同：{rel}");
                continue;
            }
            if (!Catalog.Schemas.TryGetValue(rel, out var supported) || expected != supported || !IndependentFile(entry))
                throw new CleanerException($"数据库结构或存在状态与备份不兼容：{rel}");
            using var db = Db.Open(Data(rel), writable: false);
            if (Db.SchemaHash(db) != expected) throw new CleanerException($"数据库结构与备份不兼容：{rel}");
            if (!manifest.Migrations.TryGetValue(rel, out var migrations) || MigrationFingerprint(db, rel) != migrations)
                throw new CleanerException($"数据库迁移版本与备份不兼容：{rel}");
        }
    }

    private void AssertKnownBackupDatabases()
    {
        foreach (var entry in new DirectoryInfo(Safe(DataRoot)).EnumerateFileSystemInfos())
            if (Regex.IsMatch(entry.Name, @"\A(?:state|thread_history|memories|logs|goals|queue)_\d+\.sqlite\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
                && !Catalog.Schemas.ContainsKey(entry.Name)) throw new CleanerException($"发现未验证版本的数据库，不能备份或恢复：{entry.Name}");
    }

    private static string MigrationFingerprint(SqliteConnection db, string rel)
    {
        var columns = rel == "sqlite/codex-dev.db" ? new[] { "id", "predecessor_id", "checksum" } : new[] { "version", "success", "checksum" };
        var table = rel == "sqlite/codex-dev.db" ? "codex_schema_migrations" : "_sqlx_migrations";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var values in Db.Rows(db, $"SELECT {string.Join(',', columns.Select(Db.Quote))} FROM {Db.Quote(table)} ORDER BY {Db.Quote(columns[0])}"))
            hash.AppendData(RowBytes(new BackupRow { Columns = [.. columns], Values = values.Select(BackupCell.From).ToList() }));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] RowBytes(BackupRow row) => JsonSerializer.SerializeToUtf8Bytes(row, BackupJson.Default.BackupRow);

    private static (List<string> Columns, List<string> Keys) BackupColumns(SqliteConnection db, string table)
    {
        var columns = Db.Rows(db, $"PRAGMA table_info({Db.Quote(table)})");
        return (columns.Select(row => (string)row[1]!).ToList(), columns.Where(row => (long)row[5]! > 0).OrderBy(row => (long)row[5]!).Select(row => (string)row[1]!).ToList());
    }

    private static List<BackupRow> ReadBackupRows(SqliteConnection db, string rel, SqlAction action, Selection selection)
    {
        if (action.Kind == ActionKind.CatalogRevision) return [];
        var (columns, keys) = BackupColumns(db, action.Table);
        if (columns.Count == 0 || keys.Count == 0) throw new CleanerException($"备份无法确定表的主键：{rel}/{action.Table}");
        var scope = rel == "memories_1.sqlite" && selection.ClearMemories ? "memories"
            : rel == "logs_2.sqlite" && selection.ClearSharedHistory ? "shared" : "chat";
        var result = new List<BackupRow>();
        foreach (var values in Db.Rows(db, $"SELECT {string.Join(',', columns.Select(Db.Quote))} FROM {Db.Quote(action.Table)} WHERE {action.Where}", action.Args))
        {
            var row = new BackupRow { Database = rel, Table = action.Table, Kind = action.Kind, Scope = scope, Columns = columns, Keys = keys, Values = values.Select(BackupCell.From).ToList() };
            row.Owners = RowOwners(row, selection.ChatIds);
            if (scope == "chat" && row.Owners.Count == 0) throw new CleanerException($"备份行无法归属于所选聊天：{rel}/{action.Table}");
            result.Add(row);
        }
        return result.OrderBy(row => Convert.ToBase64String(RowBytes(row)), StringComparer.Ordinal).ToList();
    }

    private static List<string> RowOwners(BackupRow row, IReadOnlyList<string> ids)
    {
        var owners = row.Kind == ActionKind.ClearReference ? new[] { row.Get("attached_thread_parent_id") as string }
            : row.Table == "thread_spawn_edges" ? new[] { row.Get("parent_thread_id") as string, row.Get("child_thread_id") as string }
            : new[] { row.Get(row.Database == "state_5.sqlite" && row.Table == "threads" ? "id" : "thread_id") as string };
        return owners.OfType<string>().Where(ids.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static BackupRow? ReadRowByKey(SqliteConnection db, BackupRow row)
    {
        var (columns, keys) = BackupColumns(db, row.Table);
        if (!columns.SequenceEqual(row.Columns) || !keys.SequenceEqual(row.Keys)) throw new CleanerException($"备份列结构不匹配：{row.Database}/{row.Table}");
        var args = keys.Select(key => row.Values[columns.IndexOf(key)].Value).ToArray();
        var values = Db.Rows(db, $"SELECT {string.Join(',', columns.Select(Db.Quote))} FROM {Db.Quote(row.Table)} WHERE {string.Join(" AND ", keys.Select((key, i) => $"{Db.Quote(key)} IS $p{i}"))}", args);
        if (values.Count > 1) throw new CleanerException("备份主键不能唯一定位数据行。");
        return values.Count == 0 ? null : new BackupRow { Database = row.Database, Table = row.Table, Kind = row.Kind, Scope = row.Scope, Owners = row.Owners, Columns = columns, Keys = keys, Values = values[0].Select(BackupCell.From).ToList() };
    }

    private static List<BackupIndexLine> SelectedIndexLines(string rel, byte[] bytes, IReadOnlySet<string> ids)
    {
        var key = rel == Catalog.SessionIndex ? "id" : "session_id";
        var result = new List<BackupIndexLine>();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException e) { throw new CleanerException($"索引不是有效的 UTF-8：{rel}", e); }
        foreach (var line in SplitKeepingNewlines(text))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? value;
            try { value = JsonNode.Parse(line.TrimStart('\uFEFF')); }
            catch (JsonException e) { throw new CleanerException($"索引包含无法解析的 JSON 行：{rel}", e); }
            if (Json.Str(Json.Get(value, key)) is { } id && ids.Contains(id)) result.Add(new BackupIndexLine { Owner = id, Text = line });
        }
        return result;
    }

    private byte[] ReadBackupIndex(string rel, bool live)
    {
        if (Stat(rel) is not { } entry) return [];
        if (entry is not FileInfo file || file.Length > Catalog.MaxJsonBytes || !IndependentFile(file)) throw new CleanerException($"索引无法安全备份：{rel}");
        using var stream = new FileStream(file.FullName, FileMode.Open, live ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (live) ThreadLocks.Wait(stream, $"Codex 正在写入 {rel}，请稍后重试。");
        if (stream.Length > Catalog.MaxJsonBytes) throw new CleanerException($"索引过大：{rel}");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private BackupPackage PrepareBackup(PlanState plan, IProgress<string>? progress)
    {
        progress?.Report("正在保存可恢复备份");
        AssertKnownBackupDatabases();
        var id = $"sessions-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var directory = Safe(SafeBackupRoot(), id, missing: true);
        Directory.CreateDirectory(directory);
        var manifest = new BackupManifest { Id = id, DataRoot = DataRoot, CreatedAt = DateTimeOffset.UtcNow, Live = plan.Public.Live,
            ClearMemories = plan.Public.Selection.ClearMemories, ClearSharedHistory = plan.Public.Selection.ClearSharedHistory };
        var package = new BackupPackage(directory, manifest, []);
        try
        {
            SaveBackupManifest(package);
            foreach (var (rel, supported) in Catalog.Schemas)
            {
                if (Stat(rel) is null) { manifest.Schemas[rel] = null; continue; }
                if (!IndependentFile(Stat(rel))) throw new CleanerException($"数据库无法安全备份：{rel}");
                using var source = Db.Open(Data(rel), writable: false);
                var schema = Db.SchemaHash(source);
                if (schema != supported) throw new CleanerException($"数据库结构未验证：{rel}");
                manifest.Schemas[rel] = schema;
                manifest.Migrations[rel] = MigrationFingerprint(source, rel);
                var work = plan.Databases.FirstOrDefault(work => work.Path == rel);
                if (work is null) continue;
                using var transaction = source.BeginTransaction(deferred: true);
                foreach (var action in work.Actions) package.Rows.AddRange(ReadBackupRows(source, rel, action, plan.Public.Selection));
            }
            foreach (var chat in plan.Public.Chats)
            {
                var record = new BackupChatRecord { Id = chat.Id, Title = chat.Title, Messages = chat.Messages ?? 0, RecordOnly = chat.CanRemoveRecord };
                foreach (var rel in plan.Files.Where(file => file.Remove && !Catalog.MemoryDirs.Any(dir => file.Path.StartsWith(dir + "/", StringComparison.Ordinal))).Select(file => file.Path))
                {
                    var meta = ReadSessionMeta(rel);
                    if (meta is { } value && value.Id == chat.Id && value.Base is { } parent && parent != chat.Id && !record.Parents.Contains(parent)) record.Parents.Add(parent);
                }
                manifest.Chats.Add(record);
                if (!chat.CanRemoveRecord && chat.RelativePath is { } missing && Stat(missing) is null)
                    manifest.Files.Add(new BackupFileRecord { Path = missing, Owner = chat.Id, Before = new Fingerprint(false), After = new Fingerprint(false) });
            }
            foreach (var work in plan.Files.Where(file => file.Remove || Catalog.GlobalFiles.Contains(file.Path)))
            {
                var before = FingerprintOf(DataRoot, work.Path);
                if (!before.Exists) throw new CleanerException($"待备份文件已不存在：{work.Path}");
                var memory = Catalog.MemoryDirs.Any(dir => work.Path.StartsWith(dir + "/", StringComparison.Ordinal));
                var owner = work.Remove && !memory ? ReadSessionMeta(work.Path)?.Id : null;
                if (work.Remove && !memory && (owner is null || !manifest.Chats.Any(chat => chat.Id == owner && !chat.RecordOnly))) throw new CleanerException($"会话备份归属不匹配：{work.Path}");
                var target = Safe(directory, "files/" + work.Path, missing: true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Data(work.Path), target);
                using (var durable = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None)) durable.Flush(true);
                if (FingerprintOf(directory, "files/" + work.Path) != before || FingerprintOf(DataRoot, work.Path) != before) throw new CleanerException($"备份文件校验失败：{work.Path}");
                manifest.Files.Add(new BackupFileRecord { Path = work.Path, Scope = memory ? "memories" : work.Remove ? "chat" : "desktop", Owner = owner ?? "", Before = before,
                    Remove = work.Remove, After = work.Remove ? new Fingerprint(false) : new Fingerprint(true, work.Content!.Length, DesktopState.Hash(work.Content)) });
                if (!work.Remove && manifest.ClearSharedHistory)
                {
                    var (root, _) = DesktopState.Read(Data(work.Path), work.Path);
                    foreach (var container in new[] { "", "electron-persisted-atom-state" })
                        if (Json.Get(Json.Get(container.Length == 0 ? root : Json.Get(root, container), "prompt-history"), "global") is { } value)
                            manifest.Desktop.Add(new BackupDesktopRecord { Path = work.Path, Container = container, Before = value.ToJsonString() });
                }
            }
            foreach (var rel in IndexFiles)
            {
                var existed = Stat(rel) is not null;
                var bytes = ReadBackupIndex(rel, plan.Public.Live);
                manifest.Indexes.Add(new BackupIndexRecord { Path = rel, Existed = existed, Lines = SelectedIndexLines(rel, bytes, plan.Public.Selection.ChatIds.ToHashSet(StringComparer.Ordinal)) });
            }
            var rowsPath = Safe(directory, "rows.sqlite", missing: true);
            using (var rows = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = rowsPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                rows.Open();
                Db.Exec(rows, "PRAGMA journal_mode=DELETE");
                Db.Exec(rows, "PRAGMA synchronous=FULL");
                Db.Exec(rows, "CREATE TABLE backup_rows (ordinal INTEGER PRIMARY KEY, payload BLOB NOT NULL)");
                using var transaction = rows.BeginTransaction();
                for (var i = 0; i < package.Rows.Count; i++) Db.Exec(rows, "INSERT INTO backup_rows (ordinal,payload) VALUES ($p0,$p1)", [i, RowBytes(package.Rows[i])]);
                transaction.Commit();
                if ((long)Db.Scalar(rows, "SELECT count(*) FROM backup_rows")! != package.Rows.Count || Db.Scalar(rows, "PRAGMA quick_check") as string != "ok") throw new CleanerException("备份数据库校验失败。");
            }
            manifest.RowCount = package.Rows.Count;
            manifest.RowsFingerprint = FingerprintOf(directory, "rows.sqlite");
            SetBackupStatus(package, "prepared");
            LoadBackup(directory);
            return package;
        }
        catch
        {
            RemoveBackupDirectory(directory);
            throw;
        }
    }

    private void VerifyBackupRows(BackupPackage package, SqliteConnection db, DatabaseWork work, Selection selection)
    {
        var current = work.Actions.SelectMany(action => ReadBackupRows(db, work.Path, action, selection)).Select(row => Convert.ToBase64String(RowBytes(row))).Order(StringComparer.Ordinal);
        var expected = package.Rows.Where(row => row.Database == work.Path).Select(row => Convert.ToBase64String(RowBytes(row))).Order(StringComparer.Ordinal);
        if (!current.SequenceEqual(expected)) throw new CleanerException($"备份后数据库中的待清理内容发生变化，停止删除：{work.Path}");
    }

    public long EstimateBackupBytes(string planId) => WithRead(() =>
    {
        PlanState? plan;
        lock (gate) plans.TryGetValue(planId, out plan);
        if (plan is null || plan.Expires < DateTimeOffset.UtcNow) throw new CleanerException("清理预览无效或已过期，请重新预览。");
        if (plan.Public.Repair) return 0L;
        var bytes = 16384L;
        foreach (var file in plan.Files)
            if (Stat(file.Path) is FileInfo info) bytes = checked(bytes + info.Length * (plan.Public.Live && IndexFiles.Contains(file.Path) ? 2 : 1));
        foreach (var work in plan.Databases)
        {
            using var db = Db.Open(Data(work.Path), writable: false);
            foreach (var action in work.Actions.Where(action => action.Kind != ActionKind.CatalogRevision))
            {
                var (columns, _) = BackupColumns(db, action.Table);
                var terms = string.Join('+', columns.Select(column => $"coalesce(octet_length({Db.Quote(column)}),0)"));
                var size = (long)Db.Scalar(db, $"SELECT coalesce(sum({terms}+{columns.Count * 32 + 128}),0) FROM {Db.Quote(action.Table)} WHERE {action.Where}", action.Args)!;
                bytes = checked(bytes + size);
            }
        }
        return bytes;
    });

    private static void VerifyBackupIndex(BackupPackage package, string rel, byte[] bytes, IReadOnlySet<string> ids)
    {
        var current = SelectedIndexLines(rel, bytes, ids);
        var expected = package.Manifest.Indexes.Single(index => index.Path == rel).Lines;
        if (current.Count != expected.Count || current.Where((line, index) => line.Owner != expected[index].Owner || line.Text != expected[index].Text).Any())
            throw new CleanerException($"备份后索引中的待清理内容发生变化，停止删除：{rel}");
    }

    private BackupPackage LoadBackup(string path, bool rows = true)
    {
        var directory = BackupDirectory(path);
        var manifestPath = Safe(directory, "manifest.json");
        if (!IndependentFile(new FileInfo(manifestPath)) || new FileInfo(manifestPath).Length > Catalog.MaxJsonBytes) throw new CleanerException("备份清单无法安全读取。");
        BackupManifest manifest;
        try { manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), BackupJson.Default.BackupManifest) ?? throw new CleanerException("备份清单为空。"); }
        catch (JsonException e) { throw new CleanerException("备份清单格式无效。", e); }
        if (manifest.Format != "codex-cleaner-backup-v1" || manifest.Id != Path.GetFileName(directory) || manifest.Status is not ("prepared" or "applying" or "needs-recovery" or "complete" or "recovered")
            || string.IsNullOrWhiteSpace(manifest.DataRoot) || manifest.Schemas is null || manifest.Migrations is null || manifest.Chats is null || manifest.Files is null
            || manifest.Indexes is null || manifest.Desktop is null || manifest.Completed is null || manifest.RowCount < 0)
            throw new CleanerException("备份格式不支持或备份准备未完成。");
        if (!SafePaths.Same(SafePaths.Normal(manifest.DataRoot), DataRoot)) throw new CleanerException("备份不属于当前 Codex 数据目录。");
        if (manifest.Chats.Count > 500 || manifest.Chats.Any(chat => chat is null || !Ids.Valid(chat.Id) || chat.Parents is null || chat.Parents.Any(parent => !Ids.Valid(parent))) || manifest.Chats.Select(chat => chat.Id).Distinct().Count() != manifest.Chats.Count)
            throw new CleanerException("备份中的聊天标识无效。");
        var package = new BackupPackage(directory, manifest, []);
        if (manifest.RowsFingerprint is null || BackupFingerprint(package, "rows.sqlite") != manifest.RowsFingerprint) throw new CleanerException("备份数据库内容摘要不匹配。");
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Before is null || file.Path is null) throw new CleanerException("备份文件条目无效。");
            SafePaths.ValidRelative(file.Path);
            var memory = Catalog.MemoryDirs.Any(dir => file.Path.StartsWith(dir + "/", StringComparison.Ordinal));
            var rollout = (file.Path.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase))
                && (file.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase));
            if (file.Scope == "chat" ? !file.Remove || !rollout || !manifest.Chats.Any(chat => chat.Id == file.Owner && !chat.RecordOnly)
                : file.Scope == "memories" ? !file.Remove || !memory || !manifest.ClearMemories
                : file.Scope == "desktop" ? file.Remove || !Catalog.GlobalFiles.Contains(file.Path) || file.After is null : true)
                throw new CleanerException("备份包含未授权的文件类型或归属。");
            if (BackupFingerprint(package, "files/" + file.Path) != file.Before) throw new CleanerException($"备份文件内容摘要不匹配：{file.Path}");
            if (file.Scope == "chat" && file.Before.Exists && ReadBackupSessionMeta(package, file.Path).Id != file.Owner)
                throw new CleanerException($"备份会话文件归属不匹配：{file.Path}");
        }
        if (manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count) throw new CleanerException("备份包含重复文件路径。");
        foreach (var chat in manifest.Chats)
        {
            var parents = manifest.Files.Where(file => file.Scope == "chat" && file.Owner == chat.Id && file.Before.Exists)
                .Select(file => ReadBackupSessionMeta(package, file.Path).Base).OfType<string>().Where(parent => parent != chat.Id).Distinct().Order(StringComparer.Ordinal);
            if (!parents.SequenceEqual(chat.Parents.Order(StringComparer.Ordinal))) throw new CleanerException("备份分叉依赖与会话文件不匹配。");
        }
        if (manifest.Indexes.Any(index => index is null || index.Lines is null) || manifest.Indexes.Count != IndexFiles.Length || !manifest.Indexes.Select(index => index.Path).Order(StringComparer.Ordinal).SequenceEqual(IndexFiles.Order(StringComparer.Ordinal))) throw new CleanerException("备份索引清单不完整。");
        foreach (var index in manifest.Indexes)
        {
            if ((index.JournalBefore is null) != (index.JournalAfter is null)) throw new CleanerException("备份索引故障材料不完整。");
            if (index.JournalBefore is not null && (BackupFingerprint(package, "indexes/" + index.Path + ".before") != index.JournalBefore
                || BackupFingerprint(package, "indexes/" + index.Path + ".after") != index.JournalAfter)) throw new CleanerException("备份索引故障材料摘要不匹配。");
            foreach (var line in index.Lines)
                if (line is null || line.Text is null || !manifest.Chats.Any(chat => chat.Id == line.Owner) || SelectedIndexLines(index.Path, Encoding.UTF8.GetBytes(line.Text), new HashSet<string>([line.Owner])).Count != 1)
                    throw new CleanerException("备份索引行归属无效。");
        }
        foreach (var item in manifest.Desktop)
            if (item is null || !manifest.ClearSharedHistory || !Catalog.GlobalFiles.Contains(item.Path) || item.Before is null || item.Container is not ("" or "electron-persisted-atom-state") || JsonNode.Parse(item.Before) is null)
                throw new CleanerException("备份共享历史字段无效。");
        if (rows)
        {
            using var db = Db.Open(Safe(directory, "rows.sqlite"), writable: false);
            if (Db.Scalar(db, "PRAGMA quick_check") as string != "ok") throw new CleanerException("备份数据库一致性校验失败。");
            foreach (var data in Db.Rows(db, "SELECT payload FROM backup_rows ORDER BY ordinal"))
            {
                if (data[0] is not byte[] payload) throw new CleanerException("备份数据行类型无效。");
                var row = JsonSerializer.Deserialize(payload, BackupJson.Default.BackupRow) ?? throw new CleanerException("备份数据行无效。");
                ValidateBackupRow(row, manifest);
                if (row.Database == "state_5.sqlite" && row.Table == "threads" && row.Kind == ActionKind.Delete)
                {
                    var chat = manifest.Chats.Single(saved => saved.Id == row.Get("id") as string);
                    if (!chat.RecordOnly)
                    {
                        if (row.Get("rollout_path") is not string original || IsSpecialRolloutPath(original)) throw new CleanerException("备份聊天的会话路径无效。");
                        var relative = RolloutRelative(original);
                        if (!manifest.Files.Any(file => file.Owner == chat.Id && string.Equals(file.Path, relative, StringComparison.OrdinalIgnoreCase)))
                            throw new CleanerException("备份聊天的会话路径与文件清单不一致。");
                    }
                }
                package.Rows.Add(row);
            }
            if (package.Rows.Count != manifest.RowCount) throw new CleanerException("备份数据行数校验失败。");
        }
        return package;
    }

    private (string Id, string? Base) ReadBackupSessionMeta(BackupPackage package, string relative)
    {
        try
        {
            using var file = File.OpenRead(Safe(package.Directory, "files/" + relative));
            using var stream = relative.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new DecompressionStream(file) : (Stream)file;
            foreach (var line in Lines(stream, Catalog.MaxJsonBytes))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var value = JsonNode.Parse(line.TrimStart('\uFEFF'));
                if (Json.Str(Json.Get(value, "type")) == "session_meta")
                {
                    var payload = Json.Get(value, "payload");
                    var id = Json.Str(Json.Get(payload, "id"));
                    var parent = Json.Str(Json.Get(Json.Get(payload, "history_base"), "thread_id"));
                    if (!Ids.Valid(id) || parent is not null && !Ids.Valid(parent)) break;
                    return (id!, parent);
                }
                if (Json.Str(Json.Get(value, "type")) is "response_item" or "inter_agent_communication") break;
            }
        }
        catch (Exception e) when (e is ZstdException or InvalidDataException or JsonException) { throw new CleanerException($"备份会话元数据无法验证：{relative}", e); }
        throw new CleanerException($"备份会话元数据缺失：{relative}");
    }

    private static void ValidateBackupRow(BackupRow row, BackupManifest manifest)
    {
        var ids = manifest.Chats.Select(chat => chat.Id).ToList();
        var selection = new Selection(ids, manifest.ClearMemories, manifest.ClearSharedHistory);
        if (row.Database is null || row.Table is null || row.Columns is null || row.Keys is null || row.Values is null || row.Owners is null
            || !Catalog.Schemas.ContainsKey(row.Database) || !manifest.Schemas.TryGetValue(row.Database, out var schema) || schema is null
            || !SqlActions(row.Database, ids, selection).Any(action => action.Table == row.Table && action.Kind == row.Kind && action.Kind != ActionKind.CatalogRevision)
            || row.Columns.Count == 0 || row.Columns.Count != row.Values.Count || row.Columns.Any(string.IsNullOrEmpty) || row.Values.Any(value => value is null) || row.Columns.Distinct().Count() != row.Columns.Count
            || row.Keys.Count == 0 || row.Keys.Any(key => !row.Columns.Contains(key))) throw new CleanerException("备份数据行不属于已验证的清理范围。");
        var scope = row.Database == "memories_1.sqlite" && manifest.ClearMemories ? "memories" : row.Database == "logs_2.sqlite" && manifest.ClearSharedHistory ? "shared" : "chat";
        if (row.Scope != scope || !RowOwners(row, ids).SequenceEqual(row.Owners) || (scope == "chat" && row.Owners.Count == 0)) throw new CleanerException("备份数据行归属不匹配。");
        if (row.Kind == ActionKind.ClearReference && row.Get("id") is string child && ids.Contains(child)) throw new CleanerException("备份父引用范围无效。");
        if (row.Database == "sqlite/codex-dev.db" && row.Table is "live_visualization_suggestions" or "local_thread_catalog" or "local_thread_catalog_scan_entries" or "thread_timeline_ledger"
            && row.Get("host_id") as string != "local") throw new CleanerException("备份包含非本机聊天数据。");
        foreach (var value in row.Values) _ = value.Value;
    }

    public IReadOnlyList<BackupInfo> Backups() => WithRead(() =>
    {
        var root = SafeBackupRoot();
        if (!Directory.Exists(root)) return [];
        var result = new List<BackupInfo>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().Where(directory => BackupId().IsMatch(directory.Name)).OrderByDescending(directory => directory.Name, StringComparer.Ordinal))
        {
            try
            {
                var package = LoadBackup(directory.FullName, rows: false);
                string? problem = null;
                try { CheckBackupCompatibility(package.Manifest); }
                catch (CleanerException e) { problem = e.Message; }
                var existing = new HashSet<string>(StringComparer.Ordinal);
                if (Stat("state_5.sqlite") is FileInfo)
                {
                    using var state = Db.Open(Data("state_5.sqlite"), writable: false);
                    foreach (var row in Db.Rows(state, "SELECT id FROM threads")) existing.Add((string)row[0]!);
                }
                var manifest = package.Manifest;
                result.Add(new BackupInfo(directory.FullName, manifest.CreatedAt, BackupSize(directory.FullName),
                    manifest.Chats.Select(chat => new BackupChat(chat.Id, chat.Title, chat.Messages, existing.Contains(chat.Id) ? "当前数据库中已有同 ID 的聊天" : null)).ToList(),
                    manifest.ClearMemories, manifest.ClearSharedHistory, manifest.Status is not ("complete" or "recovered"), problem, manifest.Live));
            }
            catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or JsonException or SqliteException)
            {
                result.Add(new BackupInfo(directory.FullName, directory.CreationTimeUtc, 0, [], false, false, true, $"无法读取备份：{e.Message}", false));
            }
        }
        return (IReadOnlyList<BackupInfo>)result;
    });

    private long BackupSize(string path)
    {
        var total = 0L;
        foreach (var entry in new DirectoryInfo(SafeBackupRoot()).EnumerateFileSystemInfos(Path.GetFileName(path)))
        {
            if (entry is not DirectoryInfo directory || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new CleanerException("备份目录含特殊文件。");
            var pending = new Stack<DirectoryInfo>([directory]);
            while (pending.TryPop(out var current))
                foreach (var child in current.EnumerateFileSystemInfos())
                {
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new CleanerException("备份目录含链接。");
                    if (child is DirectoryInfo subdirectory) pending.Push(subdirectory);
                    else if (child is FileInfo file && IndependentFile(file)) total += file.Length;
                    else throw new CleanerException("备份目录含特殊文件。");
                }
        }
        return total;
    }

    private void RemoveBackupDirectory(string path)
    {
        var directory = BackupDirectory(path);
        _ = BackupSize(directory);
        Directory.Delete(directory, recursive: true);
    }

    public void DeleteBackup(string path) => Exclusive(() =>
    {
        var package = LoadBackup(path, rows: false);
        if (package.Manifest.Status is not ("complete" or "recovered")) throw new CleanerException("未完成清理的故障备份不能删除，请先处理故障。");
        RemoveBackupDirectory(path);
        return 0;
    }, live: true);

    internal IReadOnlyList<RecoveryIssue> BackupRecoveryIssues()
    {
        var root = SafeBackupRoot();
        if (!Directory.Exists(root)) return [];
        var issues = new List<RecoveryIssue>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().Where(directory => BackupId().IsMatch(directory.Name)))
        {
            try
            {
                var path = Safe(BackupDirectory(directory.FullName), "manifest.json");
                if (!IndependentFile(new FileInfo(path)) || new FileInfo(path).Length > Catalog.MaxJsonBytes) throw new CleanerException("备份清单无法安全读取。");
                var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), BackupJson.Default.BackupManifest) ?? throw new CleanerException("备份清单为空。");
                if (manifest.Format != "codex-cleaner-backup-v1" || manifest.Id != directory.Name || string.IsNullOrWhiteSpace(manifest.DataRoot)
                    || manifest.Schemas is null || manifest.Migrations is null || manifest.Chats is null || manifest.Files is null || manifest.Indexes is null || manifest.Desktop is null || manifest.Completed is null)
                    throw new CleanerException("备份清单格式无效。");
                if (!SafePaths.Same(SafePaths.Normal(manifest.DataRoot), DataRoot)) continue;
                if (manifest.Status is not ("complete" or "recovered")) issues.Add(new RecoveryIssue(directory.FullName, "清理部分执行或异常中断，请退出 Codex 后在备份与恢复中处理。", true));
            }
            catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or JsonException or SqliteException)
            {
                issues.Add(new RecoveryIssue(directory.FullName, e.Message, true));
            }
        }
        return issues;
    }
}
