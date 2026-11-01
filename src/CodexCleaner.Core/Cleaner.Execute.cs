using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.Core;

public sealed partial class Cleaner
{
    public ExecuteResult Execute(string planId) => Run(planId, repair: false);
    public ExecuteResult ExecuteRepair(string planId) => Run(planId, repair: true);

    private ExecuteResult Run(string planId, bool repair) => Exclusive(() =>
    {
        PlanState? plan;
        lock (gate) plans.TryGetValue(planId, out plan);
        if (plan is null || plan.Expires < DateTimeOffset.Now || plan.Public.Repair != repair) throw new CleanerException("清理预览无效或已过期，请重新预览。");
        if (RecoveryIssues().Any(x => x.BlocksWrites)) throw new CleanerException("上次清理异常中断，尚有未验证的故障材料。为防止覆盖数据，本次未执行；请查看故障路径。");
        AssertFingerprints(plan.Fingerprints);
        if (repair)
        {
            using var db = Db.Open(Data("state_5.sqlite"), writable: false);
            foreach (var id in plan.Public.Selection.ChatIds)
                if (Db.Scalar(db, "SELECT 1 FROM threads WHERE id=$p0", [id]) is not null) throw new CleanerException("待修复引用对应的聊天已重新出现，请重新检查；本次未删除现有聊天。");
        }
        var latest = InspectDatabases();
        foreach (var (rel, info) in latest)
            if (info is { Present: true, Supported: false }) throw new CleanerException($"数据库无法安全写入：{rel}：{info.Reason ?? "结构未验证"}");
        var automationFiles = AutomationFiles();
        if (!automationFiles.SequenceEqual(plan.AutomationFiles)) throw new CleanerException("自动化文件列表在预览后改变，请重新预览。");
        var targets = AutomationTargets(latest, automationFiles);
        foreach (var id in plan.Public.Selection.ChatIds)
            if (targets.TryGetValue(id, out var labels)) throw new CleanerException($"聊天被自动化 {string.Join('、', labels)} 引用，请先在 Codex 中处理该任务。");
        if (plan.Public.Selection.ClearMemories && !MemoryFiles().SequenceEqual(plan.MemoryPaths)) throw new CleanerException("记忆文件列表在预览后改变，请重新预览。");
        return Apply(plan, repair);
    });

    private ExecuteResult Apply(PlanState plan, bool repair)
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
            foreach (var (work, db, _) in opened)
            {
                Db.Exec(db, "PRAGMA secure_delete=ON");
                foreach (var action in work.Actions)
                {
                    var sql = action.Kind switch
                    {
                        ActionKind.CatalogRevision => "INSERT INTO local_thread_catalog_metadata (id,catalog_revision) VALUES (1,1) ON CONFLICT(id) DO UPDATE SET catalog_revision=catalog_revision+1",
                        ActionKind.ClearReference => $"UPDATE {Db.Quote(action.Table)} SET {Db.Quote(action.Column!)} = NULL WHERE {action.Where}",
                        _ => $"DELETE FROM {Db.Quote(action.Table)} WHERE {action.Where}",
                    };
                    Db.Exec(db, sql, action.Args);
                }
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
            foreach (var (work, db, _) in opened)
            {
                Db.Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
                foreach (var action in work.Actions.Where(a => a.Kind != ActionKind.CatalogRevision))
                {
                    var check = action.Kind == ActionKind.ClearReference ? $"{action.Where} AND {Db.Quote(action.Column!)} IS NOT NULL" : action.Where;
                    if (Db.Count(db, action.Table, check, action.Args) > 0) throw new CleanerException($"清理结果校验失败：{work.Path}/{action.Table}");
                }
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
                try { ProcessGuard(); RollbackTransaction(manifest, directory); }
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
