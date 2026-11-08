using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.Engine;

public sealed partial class Cleaner
{
    public ExecuteResult Execute(string planId, IProgress<string>? progress = null)
    {
        PlanState? plan;
        lock (gate) plans.TryGetValue(planId, out plan);
        return plan?.Public.Live == true ? Exclusive(() => RunLive(planId, progress), live: true) : Run(planId, repair: false, progress);
    }

    public ExecuteResult ExecuteRepair(string planId, IProgress<string>? progress = null) => Run(planId, repair: true, progress);

    private PlanState TakePlan(string planId, bool repair, bool live)
    {
        PlanState? plan;
        lock (gate) plans.TryGetValue(planId, out plan);
        if (plan is null || plan.Expires < DateTimeOffset.Now || plan.Public.Repair != repair || plan.Public.Live != live) throw new CleanerException("清理预览无效或已过期，请重新预览。");
        if (RecoveryIssues().Any(x => x.BlocksWrites)) throw new CleanerException("上次清理异常中断，尚有未验证的故障材料。为防止覆盖数据，本次未执行；请查看故障路径。");
        return plan;
    }

    private void AssertStillAllowed(PlanState plan)
    {
        var latest = InspectDatabases();
        foreach (var (rel, info) in latest)
            if (info is { Present: true, Supported: false }) throw new CleanerException($"数据库无法安全写入：{rel}：{info.Reason ?? "结构未验证"}");
        var automationFiles = AutomationFiles();
        if (!automationFiles.SequenceEqual(plan.AutomationFiles)) throw new CleanerException("自动化文件列表在预览后改变，请重新预览。");
        var targets = AutomationTargets(latest, automationFiles);
        foreach (var id in plan.Public.Selection.ChatIds)
            if (targets.TryGetValue(id, out var labels)) throw new CleanerException($"聊天被自动化 {string.Join('、', labels)} 引用，请先在 Codex 中处理该任务。");
        if (plan.Public.Repair) return;
        // Forks created after the preview would lose their history base.
        var rollouts = ScanRollouts();
        AssertNoExternalForks(plan.Public.Chats, rollouts, plan.Public.Chats);
        var planned = plan.Files.Where(f => f.Remove).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var chat in plan.Public.Chats.Where(c => !c.CanRemoveRecord))
            if (rollouts.FilesOf(chat.Id).FirstOrDefault(rel => !planned.Contains(rel)) is { } added)
                throw new CleanerException($"聊天「{chat.Title}」在预览后新增了会话文件，请重新预览：{added}");
    }

    private ExecuteResult Run(string planId, bool repair, IProgress<string>? progress) => Exclusive(() =>
    {
        progress?.Report(repair ? "正在锁定修复范围" : "正在锁定所选会话");
        var plan = TakePlan(planId, repair, live: false);
        AssertFingerprints(plan.Fingerprints);
        if (repair)
        {
            using var db = Db.Open(Data("state_5.sqlite"), writable: false);
            foreach (var id in plan.Public.Selection.ChatIds)
                if (Db.Scalar(db, "SELECT 1 FROM threads WHERE id=$p0", [id]) is not null) throw new CleanerException("待修复引用对应的聊天已重新出现，请重新检查；本次未删除现有聊天。");
        }
        AssertStillAllowed(plan);
        if (plan.Public.Selection.ClearMemories && !MemoryFiles().SequenceEqual(plan.MemoryPaths)) throw new CleanerException("记忆文件列表在预览后改变，请重新预览。");
        // Holding the writer locks keeps a Codex that starts mid-cleanup from opening these chats.
        using var locks = ThreadLocks.Acquire(ThreadLockRoot(), repair ? [] : plan.Public.Selection.ChatIds);
        return Apply(plan, repair, progress);
    });

    // Runs beside Codex. Every selected chat's writer lock is held, so Codex cannot load, resume or
    // append to it; shared databases keep receiving Codex writes for other chats, so each database
    // commits its own transaction instead of being restored from a file copy. The thread row in
    // state_5.sqlite goes last: if an earlier step fails the chat stays listed and can be retried.
    private ExecuteResult RunLive(string planId, IProgress<string>? progress)
    {
        var plan = TakePlan(planId, repair: false, live: true);
        var ids = plan.Public.Selection.ChatIds;
        progress?.Report("正在占用会话整理锁和写入锁");
        using var maintenance = RolloutMaintenanceLock();
        using var locks = ThreadLocks.Acquire(ThreadLockRoot(), ids);
        Hooks.AfterThreadLocks?.Invoke();
        progress?.Report("正在复查所选会话");
        AssertFingerprints(plan.Fingerprints);
        AssertStillAllowed(plan);
        using (var state = Db.Open(Data("state_5.sqlite"), writable: false))
            foreach (var (id, stamp) in UpdatedStamps(state, ids))
                if (stamp != plan.Updated[id]) throw new CleanerException($"聊天 {id} 在预览后有新的变化，请重新预览。");
        lock (gate) plans.Remove(planId);
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var done = new List<string>();
        var commits = 0;
        try
        {
            foreach (var work in plan.Databases.Where(d => d.Path != "state_5.sqlite"))
            {
                progress?.Report($"正在清理数据库（第 {commits + 1}/共 {plan.Databases.Count} 个）：{work.Path}");
                CommitLive(work, ++commits);
                done.Add(work.Path);
            }
            progress?.Report("正在删除会话文件");
            foreach (var file in plan.Files.Where(f => f.Remove))
            {
                Hooks.BeforeDelete?.Invoke(Data(file.Path));
                File.Delete(Data(file.Path));
                done.Add(file.Path);
            }
            progress?.Report("正在更新索引");
            foreach (var rel in IndexFiles) if (RewriteIndexLive(rel, selected)) done.Add(rel);
            foreach (var work in plan.Databases.Where(d => d.Path == "state_5.sqlite"))
            {
                progress?.Report($"正在清理数据库（第 {commits + 1}/共 {plan.Databases.Count} 个）：{work.Path}");
                CommitLive(work, ++commits);
                done.Add(work.Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or CleanerException or InvalidOperationException)
        {
            throw new CleanerException(done.Count == 0
                ? $"{e.Message} 未修改任何数据。"
                : $"{e.Message} 出错的数据库已回滚；已完成：{string.Join('、', done)}。聊天仍保留在列表中，重新预览后可继续清理。", e);
        }
        return new ExecuteResult(null, null);
    }

    private void CommitLive(DatabaseWork work, int index)
    {
        using var db = Db.Open(Data(work.Path), writable: true, busySeconds: 10);
        Db.Exec(db, "PRAGMA secure_delete=ON");
        using var transaction = db.BeginTransaction(deferred: false);
        foreach (var action in work.Actions) Db.Exec(db, ActionSql(action), action.Args);
        AssertActionsApplied(db, work);
        Hooks.BeforeCommit?.Invoke(index);
        transaction.Commit();
    }

    // Codex appends history.jsonl under an exclusive LockFileEx lock (codex-rs/message-history);
    // holding the same lock makes the in-place rewrite exclusive with its writers and readers.
    private bool RewriteIndexLive(string rel, IReadOnlySet<string> ids)
    {
        if (Stat(rel) is not { } entry) return false;
        if (entry is not FileInfo file || file.Length > Catalog.MaxJsonBytes || !IndependentFile(file)) throw new CleanerException($"索引太大或类型未知：{rel}");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        ThreadLocks.Wait(stream, $"Codex 正在写入 {rel}，请稍后重试。");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (WithoutIds(rel, bytes, ids) is not { } kept) return false;
        stream.Position = 0;
        stream.Write(kept);
        stream.SetLength(kept.Length);
        stream.Flush(true);
        return true;
    }

    // Rollout compression and migration replace rollout files under this lock (codex-rs/rollout/src/maintenance.rs).
    private FileStream RolloutMaintenanceLock()
    {
        Directory.CreateDirectory(Data(".tmp", missing: true));
        var stream = new FileStream(Data(".tmp/rollout-maintenance.lock", missing: true), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (ThreadLocks.TryLock(stream)) return stream;
        stream.Dispose();
        throw new CleanerException("Codex 正在整理会话文件（压缩或迁移），请稍后重试。");
    }

    private static string ActionSql(SqlAction action) => action.Kind switch
    {
        ActionKind.CatalogRevision => "INSERT INTO local_thread_catalog_metadata (id,catalog_revision) VALUES (1,1) ON CONFLICT(id) DO UPDATE SET catalog_revision=catalog_revision+1",
        ActionKind.ClearReference => $"UPDATE {Db.Quote(action.Table)} SET {Db.Quote(action.Column!)} = NULL WHERE {action.Where}",
        _ => $"DELETE FROM {Db.Quote(action.Table)} WHERE {action.Where}",
    };

    private static void AssertActionsApplied(SqliteConnection db, DatabaseWork work)
    {
        foreach (var action in work.Actions.Where(a => a.Kind != ActionKind.CatalogRevision))
        {
            var check = action.Kind == ActionKind.ClearReference ? $"{action.Where} AND {Db.Quote(action.Column!)} IS NOT NULL" : action.Where;
            if (Db.Count(db, action.Table, check, action.Args) > 0) throw new CleanerException($"清理结果校验失败：{work.Path}/{action.Table}");
        }
    }

    private ExecuteResult Apply(PlanState plan, bool repair, IProgress<string>? progress)
    {
        var id = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}-{Guid.NewGuid()}";
        var directory = Trash(id, true);
        Directory.CreateDirectory(directory);
        var manifest = new Manifest { Id = id, DataRoot = DataRoot, CreatedAt = DateTimeOffset.UtcNow, Status = "preparing", Kind = repair ? "repair" : "cleanup" };
        var manifestPath = Trash($"{id}/manifest.json", true);
        WriteManifest(manifestPath, manifest);
        var opened = new List<(DatabaseWork Work, SqliteConnection Db, SqliteTransaction? Transaction)>();
        bool mutationStarted = false, complete = false;
        try
        {
            progress?.Report("正在备份数据库和文件");
            foreach (var f in plan.Files)
                manifest.Files.Add(new ManifestItem { Path = f.Path, Action = f.Remove ? "remove" : "rewrite", Backup = CopyToBackup(id, f.Path), Original = plan.Fingerprints[f.Path] });
            foreach (var d in plan.Databases)
            {
                var db = Db.Open(Data(d.Path), writable: true);
                opened.Add((d, db, null));
                var backupRel = $"databases/{d.Path}";
                var backupPath = Trash($"{id}/{backupRel}", true);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                using (var target = new SqliteConnection($"Data Source={backupPath};Pooling=False")) db.BackupDatabase(target);
                manifest.Databases.Add(new ManifestItem { Path = d.Path, Backup = backupRel });
            }
            progress?.Report("正在校验备份");
            foreach (var item in manifest.Files.Concat(manifest.Databases))
            {
                item.BackupFingerprint = FingerprintOf(directory, item.Backup);
                if (item.Original is not null && item.BackupFingerprint != item.Original) throw new CleanerException($"临时事务副本与原文件不一致，停止清理：{item.Path}");
            }
            manifest.Status = "prepared";
            WriteManifest(manifestPath, manifest);
            ProcessGuard();
            AssertFingerprints(plan.Fingerprints);
            for (var i = 0; i < opened.Count; i++) opened[i] = opened[i] with { Transaction = opened[i].Db.BeginTransaction(deferred: false) };
            // Recheck after obtaining every SQLite write lock, closing the window
            // between the previous fingerprint check and BEGIN IMMEDIATE.
            AssertFingerprints(plan.Fingerprints);
            manifest.Status = "applying";
            WriteManifest(manifestPath, manifest);
            mutationStarted = true;
            progress?.Report(repair ? "正在修复失效引用" : "正在删除所选内容");
            foreach (var (work, db, _) in opened)
            {
                Db.Exec(db, "PRAGMA secure_delete=ON");
                foreach (var action in work.Actions) Db.Exec(db, ActionSql(action), action.Args);
            }
            foreach (var f in plan.Files)
            {
                if (f.Remove) { Hooks.BeforeDelete?.Invoke(Data(f.Path)); File.Delete(Data(f.Path)); }
                else AtomicWrite(DataRoot, f.Path, f.Content!);
            }
            ProcessGuard();
            var commits = 0;
            for (var i = 0; i < opened.Count; i++)
            {
                Hooks.BeforeCommit?.Invoke(++commits);
                opened[i].Transaction!.Commit();
                opened[i].Transaction!.Dispose();
                opened[i] = opened[i] with { Transaction = null };
            }
            progress?.Report("正在校验清理结果");
            foreach (var (work, db, _) in opened)
            {
                Db.Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
                AssertActionsApplied(db, work);
                if (Db.Scalar(db, "PRAGMA quick_check") as string != "ok") throw new CleanerException($"数据库一致性校验失败：{work.Path}");
            }
            CloseAll(opened);
            foreach (var f in plan.Files)
            {
                var after = FingerprintOf(DataRoot, f.Path);
                if (f.Remove ? after.Exists : !after.Exists || after.Sha256 != DesktopState.Hash(f.Content!)) throw new CleanerException($"文件清理结果校验失败：{f.Path}");
            }
            ProcessGuard();
            manifest.Status = "complete";
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            WriteManifest(manifestPath, manifest);
            complete = true;
            lock (gate) plans.Remove(plan.Public.Id);
            progress?.Report("正在清理临时文件");
            try { RemoveTransaction(id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CleanerException) { return new ExecuteResult(directory, e.Message); }
            return new ExecuteResult(null, null);
        }
        catch (Exception error) when (!complete)
        {
            CloseAll(opened);
            Exception? rollbackError = null;
            if (mutationStarted)
            {
                try { progress?.Report("正在还原原有数据"); ProcessGuard(); RollbackTransaction(manifest, directory); }
                catch (Exception e) { rollbackError = e; }
            }
            if (rollbackError is null)
            {
                try { RemoveTransaction(id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CleanerException)
                {
                    manifest.Status = "cleanup-required";
                    manifest.Error = e.Message;
                    TryWriteManifest(manifestPath, manifest);
                    throw new CleanerException($"{error.Message} {(mutationStarted ? "已自动还原并验证原有数据。" : "未开始清理。")}临时副本删除失败：{directory}。", error);
                }
                throw new CleanerException($"{error.Message} {(mutationStarted ? "已自动还原并验证原有数据，没有保留恢复副本。" : "未开始清理，临时副本已移除。")}", error);
            }
            manifest.Status = "needs-recovery";
            manifest.Error = error.Message;
            manifest.RecoveryError = rollbackError.Message;
            TryWriteManifest(manifestPath, manifest);
            throw new CleanerException($"{error.Message} 自动回滚未能完成验证：{rollbackError.Message}。故障材料已保留在 {directory}；请保持 Codex 关闭，不要继续清理。", error);
        }
        finally { CloseAll(opened); }
    }

    private static void CloseAll(List<(DatabaseWork Work, SqliteConnection Db, SqliteTransaction? Transaction)> opened)
    {
        foreach (var (_, db, transaction) in opened)
        {
            try { transaction?.Rollback(); } catch (Exception e) when (e is SqliteException or InvalidOperationException) { }
            transaction?.Dispose();
            db.Dispose();
        }
        opened.Clear();
    }

    private string CopyToBackup(string packageId, string rel)
    {
        var target = Trash($"{packageId}/files/{rel}", true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Data(rel), Trash($"{packageId}/files/{rel}", true));
        var backup = Path.GetRelativePath(Path.Combine(TransactionRoot, packageId), target).Replace('\\', '/');
        Hooks.AfterBackupCopy?.Invoke(packageId, backup);
        return backup;
    }

    internal void AtomicWrite(string root, string rel, byte[] content)
    {
        var target = Safe(root, rel, missing: true);
        var temporary = $"{target}.cleaner-{Guid.NewGuid()}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { stream.Write(content); stream.Flush(true); }
            Safe(root, rel, missing: true);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void WriteManifest(string file, Manifest manifest)
    {
        if (!SafePaths.Inside(TransactionRoot, file)) throw new CleanerException("清单必须位于临时事务目录。");
        AtomicWrite(TransactionRoot, Path.GetRelativePath(TransactionRoot, file).Replace('\\', '/'), JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson.Default.Manifest));
    }

    private void TryWriteManifest(string file, Manifest manifest)
    {
        try { WriteManifest(file, manifest); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or CleanerException) { }
    }

    private void RollbackTransaction(Manifest manifest, string directory)
    {
        Hooks.BeforeRollback?.Invoke();
        foreach (var item in manifest.Files.Concat(manifest.Databases))
            if (FingerprintOf(directory, item.Backup) != item.BackupFingerprint) throw new CleanerException($"临时事务副本校验失败：{item.Backup}");
        foreach (var item in manifest.Databases)
        {
            var destination = Data(item.Path, true);
            var temporary = $"{destination}.cleaner-{Guid.NewGuid()}.tmp";
            try
            {
                File.Copy(Trash($"{manifest.Id}/{item.Backup}"), temporary);
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) File.Delete(Data(item.Path + suffix, true));
                Data(item.Path, true);
                File.Move(temporary, destination, overwrite: true);
            }
            finally { File.Delete(temporary); }
            if (FingerprintOf(DataRoot, item.Path) != item.BackupFingerprint) throw new CleanerException($"数据库自动还原校验失败：{item.Path}");
            using var db = Db.Open(destination, writable: false);
            if (Db.Scalar(db, "PRAGMA quick_check") as string != "ok") throw new CleanerException($"数据库自动还原一致性失败：{item.Path}");
        }
        foreach (var item in manifest.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Data(item.Path, true))!);
            AtomicWrite(DataRoot, item.Path, File.ReadAllBytes(Trash($"{manifest.Id}/{item.Backup}")));
            if (FingerprintOf(DataRoot, item.Path) != item.Original) throw new CleanerException($"文件自动还原校验失败：{item.Path}");
        }
    }

    [GeneratedRegex(@"^[0-9TZ-]+-[0-9a-f-]{36}\z")]
    private static partial Regex TransactionId();

    private void RemoveTransaction(string id)
    {
        if (!TransactionId().IsMatch(id)) throw new CleanerException("临时事务 ID 无效。");
        var root = new DirectoryInfo(Trash(id));
        // Validate everything first so a planted link or hard link aborts before any deletion.
        var entries = root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList();
        foreach (var entry in entries)
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || (entry is FileInfo f && FileLinks.Count(f.FullName) != 1))
                throw new CleanerException("临时事务目录含特殊文件，拒绝删除。");
        var manifest = Path.Combine(root.FullName, "manifest.json");
        foreach (var file in entries.OfType<FileInfo>().Where(f => f.FullName != manifest)) file.Delete();
        if (File.Exists(manifest)) File.Delete(manifest);
        foreach (var dir in entries.OfType<DirectoryInfo>().OrderByDescending(d => d.FullName.Length)) dir.Delete();
        root.Delete();
    }
}
