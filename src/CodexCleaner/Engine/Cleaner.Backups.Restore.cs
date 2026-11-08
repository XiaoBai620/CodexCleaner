using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.Engine;

public sealed partial class Cleaner
{
    private sealed record RestoreFile(string Path, Fingerprint Before, Fingerprint After, byte[]? Content, string? Source);
    private sealed record RestoreWork(BackupPackage Package, List<BackupChatRecord> Chats, List<string> Skipped,
        List<BackupRow> Rows, List<BackupRow> References, List<BackupRow> Queue, List<RestoreFile> Files,
        List<string> Warnings, bool Memories, bool Shared, bool Recovery);

    public BackupRestorePreview PreviewRestore(string path, IReadOnlyList<string> chatIds, bool restoreMemories = false,
        bool restoreSharedHistory = false, bool recoverIncomplete = false) => WithRead(() =>
    {
        var package = LoadBackup(path);
        var baseline = RestoreBaseline(package);
        var work = BuildRestore(package, chatIds, restoreMemories, restoreSharedHistory, recoverIncomplete);
        AssertFingerprints(baseline);
        return RestorePreview(work);
    });

    public BackupRestoreResult Restore(string path, IReadOnlyList<string> chatIds, bool restoreMemories = false,
        bool restoreSharedHistory = false, IProgress<string>? progress = null) =>
        RestoreBackup(path, chatIds, restoreMemories, restoreSharedHistory, false, progress);

    public BackupRestoreResult RecoverBackup(string path, IProgress<string>? progress = null) =>
        RestoreBackup(path, [], false, false, true, progress);

    private static BackupRestorePreview RestorePreview(RestoreWork work) => new(work.Package.Directory,
        work.Chats.Select(c => new BackupChat(c.Id, c.Title, c.Messages, null)).ToList(), work.Skipped,
        work.Warnings, work.Files.Count, work.Files.Sum(f => f.After.SizeBytes), work.Memories, work.Shared, work.Recovery);

    private Dictionary<string, Fingerprint> RestoreBaseline(BackupPackage package) =>
        Fingerprints(Catalog.Schemas.Keys.SelectMany(rel => Catalog.SqliteSuffixes.Select(suffix => rel + suffix))
            .Concat(package.Manifest.Files.Select(file => file.Path)).Concat(IndexFiles).Concat(package.Manifest.Desktop.Select(item => item.Path)));

    private RestoreWork BuildRestore(BackupPackage package, IReadOnlyList<string> requested, bool memories, bool shared, bool recovery)
    {
        CheckBackupCompatibility(package.Manifest);
        var manifest = package.Manifest;
        if (recovery ? manifest.Status is not ("prepared" or "applying" or "needs-recovery") : manifest.Status is not ("complete" or "recovered"))
            throw new CleanerException(recovery ? "这个备份没有需要处理的未完成清理。" : "这个备份包含未完成清理，请使用故障恢复入口。");
        if (recovery) { requested = manifest.Chats.Select(c => c.Id).ToList(); memories = manifest.ClearMemories; shared = manifest.ClearSharedHistory; }
        if (requested.Any(id => !Ids.Valid(id))) throw new CleanerException("请选择有效的聊天 ID。");
        var requestedIds = requested.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Any(id => manifest.Chats.All(c => c.Id != id))) throw new CleanerException("所选聊天不在该备份中。");
        if (memories && !manifest.ClearMemories || shared && !manifest.ClearSharedHistory) throw new CleanerException("该备份没有所选的附加清理组。");
        if (requestedIds.Count == 0 && !memories && !shared) throw new CleanerException("没有选择要恢复的聊天或附加清理组。");
        var skipped = new List<string>();
        if (requestedIds.Count > 0)
        {
            using var state = Db.Open(Data("state_5.sqlite"), writable: false);
            foreach (var id in requestedIds)
                if (!recovery && Db.Scalar(state, "SELECT 1 FROM threads WHERE id=$p0", [id]) is not null) skipped.Add(id);
        }
        requestedIds.ExceptWith(skipped);
        var chats = manifest.Chats.Where(c => requestedIds.Contains(c.Id)).ToList();
        var warnings = new List<string> { "恢复聊天不会恢复桌面置顶、草稿或窗口标签状态。" };
        var rows = package.Rows.Where(r => r.Scope == "memories" ? memories : r.Scope == "shared" ? shared : r.Owners.Any(requestedIds.Contains)).ToList();
        AssertRestoreDependencies(package, chats, rows, requestedIds, warnings);
        if (recovery) AssertRecoveryRowSets(package);
        var inserts = new List<BackupRow>();
        var references = new List<BackupRow>();
        var queue = new List<BackupRow>();
        foreach (var group in rows.GroupBy(r => r.Database))
        {
            using var db = Db.Open(Data(group.Key), writable: false);
            foreach (var row in group)
            {
                if (row.Kind == ActionKind.CatalogRevision) continue;
                ValidateRestoreColumns(db, row);
                if (row.Table == "queued_thread_revisions")
                {
                    var current = Db.Scalar(db, "SELECT revision FROM queued_thread_revisions WHERE thread_id=$p0", [row.Get("thread_id")!]);
                    if (current is null) queue.Add(row);
                    else if (!recovery || !Equals(current, row.Get("revision"))) throw RestoreConflict(row);
                    continue;
                }
                var present = ReadRowByKey(db, row);
                if (row.Kind == ActionKind.ClearReference)
                {
                    if (present is null) throw RestoreConflict(row);
                    var parent = present.Get("attached_thread_parent_id");
                    if (parent is null) references.Add(row);
                    else if (!Equals(parent, row.Get("attached_thread_parent_id"))) throw RestoreConflict(row);
                    continue;
                }
                if (present is not null)
                {
                    if (!recovery || !SameRestoreRow(row, present)) throw RestoreConflict(row);
                    continue;
                }
                CheckRestoreUniqueKeys(db, row, rows);
                inserts.Add(row);
            }
        }
        var files = new List<RestoreFile>();
        foreach (var file in manifest.Files.Where(f => f.Scope == "desktop" ? recovery : f.Scope == "memories" ? memories : requestedIds.Contains(f.Owner)))
        {
            var current = FingerprintOf(DataRoot, file.Path);
            if (current == file.Before) continue;
            if (file.Scope == "desktop")
            {
                if (current != file.After) throw new CleanerException($"桌面状态在清理后改变，不能故障恢复：{file.Path}");
            }
            else if (current.Exists) throw new CleanerException($"恢复文件已存在且内容不同：{file.Path}");
            if (file.Before.Exists)
                files.Add(new RestoreFile(file.Path, current, file.Before, null, Safe(package.Directory, "files/" + file.Path)));
        }
        foreach (var index in manifest.Indexes)
            AddRestoredIndex(package, index, requestedIds, files, recovery);
        if (shared && !recovery) AddRestoredPromptHistory(manifest, files);
        if (skipped.Count > 0) warnings.Add($"跳过 {skipped.Count} 个已存在聊天的全部从属内容。");
        if (memories || shared) warnings.Add("附加清理组独立恢复；任何同键或同路径冲突都会停止本次恢复。");
        return new RestoreWork(package, chats, skipped, inserts, references, queue, files, warnings, memories, shared, recovery);
    }

    private void AssertRecoveryRowSets(BackupPackage package)
    {
        var selection = new Selection(package.Manifest.Chats.Select(c => c.Id).ToList(), package.Manifest.ClearMemories, package.Manifest.ClearSharedHistory);
        foreach (var (rel, schema) in package.Manifest.Schemas)
        {
            if (schema is null) continue;
            using var db = Db.Open(Data(rel), writable: false);
            var current = SqlActions(rel, selection.ChatIds, selection).Where(a => a.Kind != ActionKind.CatalogRevision)
                .SelectMany(action => ReadBackupRows(db, rel, action, selection)).ToList();
            var expected = package.Rows.Where(r => r.Database == rel).ToList();
            if (current.Count == 0) continue;
            if (current.Count != expected.Count || current.Any(row => !expected.Any(before => before.Table == row.Table && before.Kind == row.Kind && SameRestoreKey(before, row) && SameRestoreRow(before, row))))
                throw new CleanerException($"数据库内容不符合本次清理前后任一完整状态，不能自动故障恢复：{rel}");
        }
    }

    private static bool SameRestoreKey(BackupRow a, BackupRow b) => a.Keys.SequenceEqual(b.Keys) && a.Keys.All(k => SameRestoreValue(a.Get(k), b.Get(k)));

    private static bool SameRestoreValue(object? a, object? b) => a is byte[] first && b is byte[] second ? first.AsSpan().SequenceEqual(second) : Equals(a, b);

    private static bool SameRestoreRow(BackupRow a, BackupRow b) => a.Columns.SequenceEqual(b.Columns) && a.Columns.All(k => SameRestoreValue(a.Get(k), b.Get(k)));

    private static CleanerException RestoreConflict(BackupRow row) => new($"备份与当前数据冲突，未覆盖现有内容：{row.Database}/{row.Table}。");

    private static void ValidateRestoreColumns(SqliteConnection db, BackupRow row)
    {
        var columns = Db.Rows(db, $"PRAGMA table_info({Db.Quote(row.Table)})").Select(r => (string)r[1]!).ToList();
        if (!columns.SequenceEqual(row.Columns) || row.Columns.Count != row.Values.Count || row.Keys.Count == 0 || row.Keys.Any(k => !columns.Contains(k)))
            throw new CleanerException($"备份列结构不匹配：{row.Database}/{row.Table}");
        foreach (var value in row.Values) _ = value.Value;
    }

    private static void CheckRestoreUniqueKeys(SqliteConnection db, BackupRow row, IReadOnlyList<BackupRow> selected)
    {
        foreach (var index in Db.Rows(db, $"PRAGMA index_list({Db.Quote(row.Table)})").Where(i => (long)i[2]! == 1))
        {
            if ((long)index[4]! != 0) continue;
            var columns = Db.Rows(db, $"PRAGMA index_info({Db.Quote((string)index[1]!)})").Select(r => r[2] as string).ToList();
            if (columns.Any(c => c is null)) throw new CleanerException($"无法验证表达式唯一索引：{row.Table}");
            if (columns.Any(c => row.Get(c!) is null)) continue;
            var names = columns.Select(c => c!).ToList();
            var where = string.Join(" AND ", names.Select((c, i) => $"{Db.Quote(c)}=$p{i}"));
            if (Db.Scalar(db, $"SELECT 1 FROM {Db.Quote(row.Table)} WHERE {where} LIMIT 1", names.Select(c => row.Get(c)!).ToList()) is not null)
                throw RestoreConflict(row);
            if (selected.Any(other => !ReferenceEquals(other, row) && other.Database == row.Database && other.Table == row.Table && other.Kind == ActionKind.Delete && names.All(c => SameRestoreValue(row.Get(c), other.Get(c)))))
                throw new CleanerException($"备份含重复唯一键：{row.Database}/{row.Table}");
        }
    }

    private void AssertRestoreDependencies(BackupPackage package, List<BackupChatRecord> chats, List<BackupRow> rows, HashSet<string> ids, List<string> warnings)
    {
        if (chats.Count == 0 && rows.All(r => r.Database != "state_5.sqlite")) return;
        using var db = Db.Open(Data("state_5.sqlite"), writable: false);
        bool HasThread(string id) => ids.Contains(id) || Db.Scalar(db, "SELECT 1 FROM threads WHERE id=$p0", [id]) is not null;
        foreach (var row in rows.Where(r => r.Database == "state_5.sqlite"))
        {
            if (row.Table == "threads" && row.Kind == ActionKind.Delete)
                foreach (var (column, table) in new[] { ("project_id", "projects"), ("thread_section_id", "thread_sections") })
                    if (row.Get(column) is string id && Db.Scalar(db, $"SELECT 1 FROM {Db.Quote(table)} WHERE id=$p0", [id]) is null)
                        throw new CleanerException($"恢复依赖的项目或分组已不存在：{column}={id}");
            if (row.Table == "thread_spawn_edges")
                foreach (var column in new[] { "parent_thread_id", "child_thread_id" })
                    if (row.Get(column) is string id && !HasThread(id)) throw new CleanerException($"恢复派生关系需要聊天 {id}，请同批恢复或先恢复该聊天。");
            if (row.Table == "threads" && row.Get("attached_thread_parent_id") is string parent && !HasThread(parent))
                throw new CleanerException($"恢复父子关系需要聊天 {parent}，请同批恢复或先恢复该聊天。");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void CheckParent(string id, bool requiredFile = false)
        {
            if (requiredFile && ids.Contains(id) && !package.Manifest.Files.Any(file => file.Scope == "chat" && file.Owner == id && file.Before.Exists))
                throw new CleanerException($"分叉父聊天 {id} 的备份缺少可用会话文件，不能恢复断裂的历史链。");
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new CleanerException($"分叉历史存在循环引用：{id}");
            var saved = package.Manifest.Chats.FirstOrDefault(c => c.Id == id);
            List<string> parents;
            if (ids.Contains(id) && saved is not null)
            {
                parents = saved.Parents;
            }
            else
            {
                if (Db.Scalar(db, "SELECT rollout_path FROM threads WHERE id=$p0", [id]) is not string recorded)
                    throw new CleanerException($"分叉历史依赖的父聊天 {id} 不存在；请先恢复或同批选择父聊天。");
                var rel = RolloutRelative(recorded);
                var variants = new[] { rel, rel + ".zst" }.Where(candidate => Stat(candidate) is not null).ToList();
                if (variants.Count == 0) throw new CleanerException($"分叉历史依赖的父聊天 {id} 缺少可用的会话文件。");
                parents = [];
                foreach (var variant in variants)
                {
                    if (!IndependentFile(Stat(variant)) || ReadSessionMeta(variant) is not { } meta || meta.Id != id)
                        throw new CleanerException($"分叉历史依赖的父聊天文件无法验证：{variant}");
                    if (meta.Base is { } parent && parent != id && !parents.Contains(parent)) parents.Add(parent);
                }
                warnings.Add($"分叉历史依赖当前已有父聊天：{id}；该父聊天不会被覆盖。");
            }
            foreach (var parent in parents.Where(p => p != id)) CheckParent(parent, true);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var chat in chats) CheckParent(chat.Id);
    }

    private void AddRestoredIndex(BackupPackage package, BackupIndexRecord saved, HashSet<string> ids, List<RestoreFile> files, bool recovery)
    {
        var desired = saved.Lines.Where(l => ids.Contains(l.Owner)).ToList();
        var before = FingerprintOf(DataRoot, saved.Path);
        if (before.SizeBytes > Catalog.MaxJsonBytes) throw new CleanerException($"索引太大，无法安全恢复：{saved.Path}");
        var content = before.Exists ? File.ReadAllBytes(Data(saved.Path)) : [];
        if (recovery) AssertRecoveryIndex(package, saved, before, content, ids);
        if (desired.Count == 0) return;
        var text = RestoreIndexText(content, saved.Path);
        var key = saved.Path == Catalog.SessionIndex ? "id" : "session_id";
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in SplitKeepingNewlines(text))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line.TrimStart('\uFEFF')); }
            catch (JsonException e) { throw new CleanerException($"当前索引无法解析：{saved.Path}", e); }
            var identity = line.TrimEnd('\r', '\n');
            if (Json.Str(Json.Get(node, key)) is { } owner && ids.Contains(owner)) current[identity] = current.GetValueOrDefault(identity) + 1;
        }
        var expected = desired.GroupBy(l => l.Text.TrimEnd('\r', '\n'), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (current.Any(p => !expected.TryGetValue(p.Key, out var count) || p.Value > count))
            throw new CleanerException($"当前索引包含同一聊天的不同内容：{saved.Path}");
        var output = new StringBuilder(text);
        foreach (var line in desired)
        {
            var identity = line.Text.TrimEnd('\r', '\n');
            if (current.TryGetValue(identity, out var count) && count > 0) { current[identity] = count - 1; continue; }
            if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
            output.Append(line.Text);
        }
        var after = Encoding.UTF8.GetBytes(output.ToString());
        if (!content.AsSpan().SequenceEqual(after)) files.Add(new RestoreFile(saved.Path, before, new Fingerprint(true, after.Length, DesktopState.Hash(after)), after, null));
    }

    private void AssertRecoveryIndex(BackupPackage package, BackupIndexRecord saved, Fingerprint current, byte[] content, HashSet<string> ids)
    {
        if (saved.JournalBefore is null && saved.JournalAfter is null) return;
        if (saved.JournalBefore is null || saved.JournalAfter is null || !current.Exists)
            throw new CleanerException($"索引故障日志不完整或当前文件缺失，不能自动恢复：{saved.Path}");
        var key = saved.Path == Catalog.SessionIndex ? "id" : "session_id";
        var allowed = false;
        foreach (var (suffix, fingerprint) in new[] { (".before", saved.JournalBefore), (".after", saved.JournalAfter) })
        {
            var relative = "indexes/" + saved.Path + suffix;
            if (BackupFingerprint(package, relative) != fingerprint) throw new CleanerException($"索引故障日志摘要不匹配：{saved.Path}{suffix}");
            var bytes = File.ReadAllBytes(Safe(package.Directory, relative));
            if (new Fingerprint(true, bytes.Length, DesktopState.Hash(bytes)) != fingerprint)
                throw new CleanerException($"索引故障日志读取时发生变化：{saved.Path}{suffix}");
            if (content.AsSpan().SequenceEqual(bytes)) { allowed = true; continue; }
            if (bytes.Length == 0 || bytes[^1] != (byte)'\n' || !content.AsSpan().StartsWith(bytes)) continue;
            var appended = RestoreIndexText(content[bytes.Length..], saved.Path);
            var valid = true;
            foreach (var line in SplitKeepingNewlines(appended))
            {
                if (string.IsNullOrWhiteSpace(line)) { valid = false; break; }
                try
                {
                    var value = JsonNode.Parse(line);
                    if (value is not JsonObject || Json.Str(Json.Get(value, key)) is { } owner && ids.Contains(owner)) { valid = false; break; }
                }
                catch (JsonException) { valid = false; break; }
            }
            if (valid) allowed = true;
        }
        if (!allowed) throw new CleanerException($"索引不符合清理前后完整状态，可能在写入时中断；未覆盖文件，故障材料已保留：{saved.Path}");
    }

    private static string RestoreIndexText(byte[] bytes, string path)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException e) { throw new CleanerException($"索引含无效的 UTF-8 数据，不能安全恢复：{path}", e); }
    }

    private void AddRestoredPromptHistory(BackupManifest manifest, List<RestoreFile> files)
    {
        foreach (var group in manifest.Desktop.GroupBy(d => d.Path))
        {
            var before = FingerprintOf(DataRoot, group.Key);
            if (!before.Exists) throw new CleanerException($"共享输入历史所在的桌面状态已不存在：{group.Key}");
            var root = DesktopState.Read(Data(group.Key), group.Key).Root;
            var changed = false;
            foreach (var item in group)
            {
                var container = item.Container.Length == 0 ? root as JsonObject : Json.Get(root, item.Container) as JsonObject;
                if (container is null || Json.Get(container, "prompt-history") is not JsonObject history)
                    throw new CleanerException($"共享输入历史结构已变化：{group.Key}");
                var value = JsonNode.Parse(item.Before);
                if (history.TryGetPropertyValue("global", out var existing))
                {
                    if (!JsonNode.DeepEquals(existing, value)) throw new CleanerException($"共享输入历史在删除后已有新内容：{group.Key}");
                    continue;
                }
                history["global"] = value;
                changed = true;
            }
            if (!changed) continue;
            var bytes = DesktopState.Serialize(root);
            files.Add(new RestoreFile(group.Key, before, new Fingerprint(true, bytes.Length, DesktopState.Hash(bytes)), bytes, null));
        }
    }

    private BackupRestoreResult RestoreBackup(string path, IReadOnlyList<string> ids, bool memories, bool shared, bool recovery, IProgress<string>? progress) => Exclusive(() =>
    {
        progress?.Report("正在核对备份和恢复范围");
        if (RecoveryIssues().Any(i => i.BlocksWrites && SafePaths.Inside(TransactionRoot, i.Directory))) throw new CleanerException("仍有未验证的事务故障材料，请先处理后再恢复。");
        var package = LoadBackup(path);
        if (BackupRecoveryIssues().Any(issue => issue.BlocksWrites && (!recovery || !SafePaths.Same(issue.Directory, package.Directory))))
            throw new CleanerException("仍有其他未处理的备份故障，请先处理后再恢复。");
        var lockedIds = (recovery ? package.Manifest.Chats.Select(c => c.Id) : ids).ToList();
        if (lockedIds.Any(id => !Ids.Valid(id))) throw new CleanerException("请选择有效的聊天 ID。");
        lockedIds.AddRange(package.Rows.Where(r => r.Kind == ActionKind.ClearReference && r.Owners.Any(lockedIds.Contains)).Select(r => r.Get("id")).OfType<string>());
        using var maintenance = RolloutMaintenanceLock();
        using var locks = ThreadLocks.Acquire(ThreadLockRoot(), lockedIds);
        Hooks.AfterThreadLocks?.Invoke();
        ProcessGuard();
        var baseline = RestoreBaseline(package);
        var work = BuildRestore(package, ids, memories, shared, recovery);
        AssertFingerprints(baseline);
        if (work.Rows.Count == 0 && work.References.Count == 0 && work.Queue.Count == 0 && work.Files.Count == 0)
        {
            if (recovery) SetBackupStatus(package, "recovered");
            return new BackupRestoreResult(0, work.Skipped, work.Memories, work.Shared);
        }
        var dbPaths = work.Rows.Concat(work.References).Concat(work.Queue).Select(r => r.Database).Distinct(StringComparer.Ordinal).ToList();
        var id = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}-{Guid.NewGuid()}";
        var directory = Trash(id, true);
        Directory.CreateDirectory(directory);
        var manifest = new Manifest { Id = id, DataRoot = DataRoot, CreatedAt = DateTimeOffset.UtcNow, Status = "preparing", Kind = "restore" };
        var manifestPath = Trash(id + "/manifest.json", true);
        WriteManifest(manifestPath, manifest);
        var opened = new List<(DatabaseWork Work, SqliteConnection Db, SqliteTransaction? Transaction)>();
        var foreignKeysBefore = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var mutation = false;
        var complete = false;
        try
        {
            progress?.Report("正在备份恢复前的数据");
            foreach (var rel in dbPaths)
            {
                var db = Db.Open(Data(rel), writable: true);
                opened.Add((new DatabaseWork(rel, []), db, null));
                foreignKeysBefore[rel] = RestoreForeignKeyIssues(db);
                var backup = "databases/" + rel;
                var target = Trash(id + "/" + backup, true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString())) db.BackupDatabase(destination);
                manifest.Databases.Add(new ManifestItem { Path = rel, Backup = backup, BackupFingerprint = FingerprintOf(directory, backup) });
            }
            foreach (var file in work.Files)
            {
                var item = new ManifestItem { Path = file.Path, Action = "restore", Original = file.Before };
                if (file.Before.Exists)
                {
                    item.Backup = CopyToBackup(id, file.Path);
                    item.BackupFingerprint = FingerprintOf(directory, item.Backup);
                    if (item.BackupFingerprint != file.Before) throw new CleanerException($"恢复前文件备份校验失败：{file.Path}");
                }
                manifest.Files.Add(item);
            }
            manifest.Status = "prepared";
            WriteManifest(manifestPath, manifest);
            ProcessGuard();
            AssertFingerprints(baseline);
            for (var i = 0; i < opened.Count; i++) opened[i] = opened[i] with { Transaction = opened[i].Db.BeginTransaction(deferred: false) };
            AssertFingerprints(baseline);
            CheckBackupCompatibility(package.Manifest);
            manifest.Status = "applying";
            WriteManifest(manifestPath, manifest);
            mutation = true;
            progress?.Report("正在恢复数据库记录");
            foreach (var (database, db, _) in opened) RestoreDatabaseRows(db, database.Path, work);
            progress?.Report("正在恢复会话文件和索引");
            foreach (var file in work.Files)
            {
                if (FingerprintOf(DataRoot, file.Path) != file.Before) throw new CleanerException($"恢复时文件发生变化：{file.Path}");
                WriteRestoredFile(file, package);
            }
            ProcessGuard();
            for (var i = 0; i < opened.Count; i++)
            {
                Hooks.BeforeCommit?.Invoke(i + 1);
                opened[i].Transaction!.Commit();
                opened[i].Transaction!.Dispose();
                opened[i] = opened[i] with { Transaction = null };
            }
            progress?.Report("正在校验恢复结果");
            foreach (var (database, db, _) in opened)
            {
                ValidateRestoredRows(db, database.Path, work);
                if (Db.Scalar(db, "PRAGMA quick_check") as string != "ok" || RestoreForeignKeyIssues(db).Except(foreignKeysBefore[database.Path]).Any())
                    throw new CleanerException($"恢复后的数据库校验失败：{database.Path}");
            }
            foreach (var file in work.Files)
                if (FingerprintOf(DataRoot, file.Path) != file.After) throw new CleanerException($"恢复文件校验失败：{file.Path}");
            ProcessGuard();
            CloseAll(opened);
            manifest.Status = "complete";
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            WriteManifest(manifestPath, manifest);
            if (recovery) SetBackupStatus(package, "recovered");
            complete = true;
            RemoveTransaction(id);
            return new BackupRestoreResult(work.Chats.Count, work.Skipped, work.Memories, work.Shared);
        }
        catch (Exception error) when (!complete)
        {
            CloseAll(opened);
            Exception? rollbackError = null;
            if (mutation)
                try { progress?.Report("正在还原恢复前的数据"); ProcessGuard(); RollbackRestore(manifest, directory, work.Files); }
                catch (Exception e) { rollbackError = e; }
            if (rollbackError is null)
            {
                manifest.Status = "cleanup-required";
                manifest.Error = error.Message;
                TryWriteManifest(manifestPath, manifest);
                RemoveTransaction(id);
                throw new CleanerException($"{error.Message} 恢复未完成，已保留持久备份{(mutation ? "并还原恢复前的数据" : "，未开始写入")}。", error);
            }
            manifest.Status = "needs-recovery";
            manifest.Error = error.Message;
            manifest.RecoveryError = rollbackError.Message;
            TryWriteManifest(manifestPath, manifest);
            throw new CleanerException($"{error.Message} 恢复回滚未能验证：{rollbackError.Message}。故障材料保留在 {directory}，请保持 Codex 关闭。", error);
        }
        finally { CloseAll(opened); }
    });

    private static int RestoreTableOrder(string table) => table switch
    {
        "threads" or "thread_goals" => 0,
        "thread_turns" or "thread_history_projection_state" => 1,
        "queued_thread_revisions" => 3,
        _ => 2,
    };

    private static HashSet<string> RestoreForeignKeyIssues(SqliteConnection db) =>
        Db.Rows(db, "PRAGMA foreign_key_check").Select(row => string.Join("\u001f", row.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))).ToHashSet(StringComparer.Ordinal);

    private static void RestoreDatabaseRows(SqliteConnection db, string rel, RestoreWork work)
    {
        foreach (var row in work.Rows.Where(r => r.Database == rel).OrderBy(r => RestoreTableOrder(r.Table)))
        {
            var columns = string.Join(',', row.Columns.Select(Db.Quote));
            Db.Exec(db, $"INSERT INTO {Db.Quote(row.Table)} ({columns}) VALUES ({Db.In(0, row.Columns.Count)})", row.Values.Select(v => v.Value).ToList());
            if (row.Table == "threads")
                Db.Exec(db, "UPDATE threads SET created_at_ms=$p0,updated_at_ms=$p1,recency_at=$p2,recency_at_ms=$p3 WHERE id=$p4",
                    [row.Get("created_at_ms") ?? DBNull.Value, row.Get("updated_at_ms") ?? DBNull.Value, row.Get("recency_at")!, row.Get("recency_at_ms")!, row.Get("id")!]);
        }
        foreach (var row in work.References.Where(r => r.Database == rel))
        {
            var args = new List<object> { row.Get("attached_thread_parent_id")! };
            args.AddRange(row.Keys.Select(k => row.Get(k) ?? DBNull.Value));
            var where = string.Join(" AND ", row.Keys.Select((k, i) => $"{Db.Quote(k)} IS $p{i + 1}"));
            if (Db.Exec(db, $"UPDATE threads SET attached_thread_parent_id=$p0 WHERE {where} AND attached_thread_parent_id IS NULL", args) != 1)
                throw RestoreConflict(row);
        }
        foreach (var row in work.Queue.Where(r => r.Database == rel))
            if (Db.Scalar(db, "SELECT 1 FROM queued_thread_revisions WHERE thread_id=$p0", [row.Get("thread_id")!]) is null)
                Db.Exec(db, "INSERT INTO queued_thread_revisions(thread_id) VALUES($p0)", [row.Get("thread_id")!]);
        if (rel == "sqlite/codex-dev.db" && work.Rows.Any(r => r.Database == rel && r.Table == "local_thread_catalog"))
            Db.Exec(db, ActionSql(new SqlAction("local_thread_catalog_metadata", "id=1", [], ActionKind.CatalogRevision)));
    }

    private void ValidateRestoredRows(SqliteConnection db, string rel, RestoreWork work)
    {
        foreach (var row in work.Rows.Where(r => r.Database == rel))
        {
            var restored = ReadRowByKey(db, row);
            if (restored is null || !SameRestoreRow(row, restored)) throw new CleanerException($"恢复内容校验失败：{rel}/{row.Table}");
        }
        foreach (var row in work.References.Where(r => r.Database == rel))
            if (ReadRowByKey(db, row) is not { } restored || !Equals(restored.Get("attached_thread_parent_id"), row.Get("attached_thread_parent_id")))
                throw new CleanerException($"恢复父引用校验失败：{rel}/{row.Table}");
        foreach (var row in work.Queue.Where(r => r.Database == rel))
            if (Db.Scalar(db, "SELECT revision FROM queued_thread_revisions WHERE thread_id=$p0", [row.Get("thread_id")!]) is not long)
                throw new CleanerException("恢复队列版本校验失败。");
    }

    private void WriteRestoredFile(RestoreFile file, BackupPackage package)
    {
        var target = Data(file.Path, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (file.Content is not null) { AtomicWrite(DataRoot, file.Path, file.Content); return; }
        if (file.Source is null) throw new CleanerException("恢复文件缺少数据来源。");
        if (BackupFingerprint(package, Path.GetRelativePath(package.Directory, file.Source).Replace('\\', '/')) != file.After)
            throw new CleanerException($"备份文件在恢复时改变：{file.Path}");
        var temporary = target + $".cleaner-{Guid.NewGuid()}.tmp";
        try
        {
            File.Copy(file.Source, temporary);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            if (FingerprintOf(DataRoot, file.Path) != file.Before) throw new CleanerException($"恢复文件目标发生变化：{file.Path}");
            Data(file.Path, true);
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
    }

    private void RollbackRestore(Manifest manifest, string directory, List<RestoreFile> files)
    {
        var all = manifest.Files;
        manifest.Files = all.Where(f => f.Original?.Exists == true).ToList();
        try { RollbackTransaction(manifest, directory); }
        finally { manifest.Files = all; }
        foreach (var item in files.Where(f => !f.Before.Exists))
        {
            var current = FingerprintOf(DataRoot, item.Path);
            if (!current.Exists) continue;
            if (current != item.After) throw new CleanerException($"新建恢复文件已改变，不能回滚删除：{item.Path}");
            Hooks.BeforeDelete?.Invoke(Data(item.Path));
            File.Delete(Data(item.Path));
            if (FingerprintOf(DataRoot, item.Path).Exists) throw new CleanerException($"恢复回滚未删除新建文件：{item.Path}");
        }
    }
}
