using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;
using ZstdSharp;

namespace CodexCleaner.Tests;

public sealed class BackupTests
{
    private static readonly string[] Ids = Fixture.Ids;
    private static readonly Dictionary<string, string[]> Tables = new()
    {
        ["state_5.sqlite"] = ["threads", "thread_attachments", "thread_dynamic_tools", "thread_read_receipts", "guardian_review_feedback", "thread_spawn_edges"],
        ["thread_history_1.sqlite"] = ["thread_items", "thread_turns", "thread_realtime_items", "thread_history_projection_state"],
        ["memories_1.sqlite"] = ["stage1_outputs", "jobs", "consolidation_progress"],
        ["logs_2.sqlite"] = ["logs"],
        ["goals_1.sqlite"] = ["thread_goals", "thread_goal_continuation_deferrals"],
        ["queue_1.sqlite"] = ["queued_items"],
        ["sqlite/codex-dev.db"] = ["automation_runs", "inbox_items", "live_visualization_suggestions", "local_thread_catalog", "local_thread_catalog_scan_entries", "thread_timeline_ledger"],
    };

    private static string Rows(Fixture f, string database, string table) => string.Join('\n', f.Rows(database, table)
        .Select(row => JsonSerializer.Serialize(row.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)))
        .Order(StringComparer.Ordinal));

    private static Dictionary<string, string> Contents(Fixture f) => Tables.SelectMany(database => database.Value
        .Select(table => (Key: $"{database.Key}/{table}", Value: Rows(f, database.Key, table))))
        .ToDictionary(item => item.Key, item => item.Value);

    private static BackupInfo Delete(Fixture f, Selection? selection = null)
    {
        f.Cleaner.Execute(f.Cleaner.PlanCleanup(selection ?? new Selection([Ids[0]])).Id);
        var backup = Assert.Single(f.Cleaner.Backups());
        Assert.True(SafePaths.Inside(f.Base, f.Cleaner.BackupRoot));
        Assert.True(SafePaths.Inside(f.Cleaner.BackupRoot, backup.Path));
        Assert.Null(backup.Problem);
        Assert.False(backup.NeedsRecovery);
        return backup;
    }

    private static string Meta(string id, string? parent = null) => JsonSerializer.Serialize(new
    {
        type = "session_meta",
        payload = parent is null ? (object)new { id } : new { id, history_base = new { thread_id = parent } },
    }) + "\n";

    private static string[] Lines(string file) => File.ReadAllLines(file).Where(line => line.Length > 0).Order(StringComparer.Ordinal).ToArray();

    private static long Revision(Fixture f, string id) => (long)f.Rows("queue_1.sqlite", "queued_thread_revisions").Single(row => (string)row["thread_id"]! == id)["revision"]!;

    private static FileStream Hold(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Lock(0, long.MaxValue);
        return stream;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SevenDatabasesSegmentsCompressedFilesAndIndexLinesCanBeRestored(bool live)
    {
        using var f = new Fixture();
        f.Running = live;
        using (var db = f.Open("state_5.sqlite"))
            Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Ids[0], Ids[1]);
        using (var db = f.Open("sqlite/codex-dev.db"))
            Fixture.Insert(db, "local_thread_catalog_metadata", new() { ["id"] = 1L, ["catalog_revision"] = 41L });
        var segment = f.Put($"sessions/2026/10/01/rollout-segment-{Ids[0]}.jsonl", Meta(Ids[0]));
        var compressed = f.Data($"sessions/2026/10/01/rollout-cold-{Ids[0]}.jsonl.zst");
        using (var compressor = new Compressor()) File.WriteAllBytes(compressed, compressor.Wrap(Encoding.UTF8.GetBytes(Meta(Ids[0]))).ToArray());
        var history = f.Data("history.jsonl");
        File.AppendAllText(history, File.ReadAllText(history));
        var indexLines = Lines(f.Data("session_index.jsonl"));
        var historyLines = Lines(history);
        var files = new[] { f.RolloutPaths[0], segment, compressed }.ToDictionary(path => path, File.ReadAllBytes);
        var before = Contents(f);
        var otherRevision = Revision(f, Ids[1]);
        var backup = Delete(f);
        Assert.Equal(live, backup.Live);
        Assert.All(files.Keys, path => Assert.False(File.Exists(path)));
        Assert.True(backup.Bytes > files.Values.Sum(bytes => (long)bytes.Length));
        Assert.True(File.Exists(Path.Combine(backup.Path, "rows.sqlite")));
        var desktopAfterDelete = File.ReadAllText(f.Data(".codex-global-state.json"));
        f.Running = false;
        var preview = f.Cleaner.PreviewRestore(backup.Path, [Ids[0]]);
        Assert.True(preview.FileCount >= files.Count);
        Assert.Empty(preview.Skipped);
        var result = f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
        Assert.Equal(before, Contents(f));
        Assert.Equal(otherRevision, Revision(f, Ids[1]));
        Assert.True(Revision(f, Ids[0]) > otherRevision);
        Assert.True((long)f.Rows("sqlite/codex-dev.db", "local_thread_catalog_metadata").Single()["catalog_revision"]! > 42L);
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(indexLines, Lines(f.Data("session_index.jsonl")));
        Assert.Equal(historyLines, Lines(history));
        Assert.Equal(desktopAfterDelete, File.ReadAllText(f.Data(".codex-global-state.json")));
        Assert.Single(f.Cleaner.Backups());
        f.AssertNoTransactions();
    }

    [Fact]
    public void AlreadyExistingChatIsSkippedTogetherWithItsAssociatedContent()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET title='Changed after restoration' WHERE id=$p0", Ids[0]);
        using (var db = f.Open("thread_history_1.sqlite")) Fixture.Exec(db, "UPDATE thread_items SET item_json='{}' WHERE thread_id=$p0", Ids[0]);
        File.AppendAllText(f.RolloutPaths[0], "later content\n");
        var before = Contents(f);
        var file = File.ReadAllBytes(f.RolloutPaths[0]);
        var preview = f.Cleaner.PreviewRestore(backup.Path, [Ids[0]]);
        Assert.Equal([Ids[0]], preview.Skipped);
        var result = f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Equal(0, result.Restored);
        Assert.Equal([Ids[0]], result.Skipped);
        Assert.Equal(before, Contents(f));
        Assert.Equal(file, File.ReadAllBytes(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void ChangedSchemaIsRejectedBeforeRestoringAnyDatabaseOrFile()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        using (var db = f.Open("logs_2.sqlite")) Fixture.Exec(db, "CREATE TABLE future_log_links (thread_id TEXT)");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.PreviewRestore(backup.Path, [Ids[0]]));
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(before, Contents(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void MissingDatabaseIsRejectedWithoutCreatingAReplacement()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        File.Delete(f.Data("goals_1.sqlite"));
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.False(File.Exists(f.Data("goals_1.sqlite")));
        Assert.Single(f.Rows("state_5.sqlite", "threads"));
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void CorruptedBackupFileIsRejectedBeforeWriting()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var rel = Path.GetRelativePath(f.DataRoot, f.RolloutPaths[0]);
        File.AppendAllText(Path.Combine(backup.Path, "files", rel), "changed backup bytes");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(before, Contents(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void CorruptedBackupRowsAreRejectedBeforeWriting()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        File.AppendAllText(Path.Combine(backup.Path, "rows.sqlite"), "changed backup rows");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(before, Contents(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sessions/../../outside.txt")]
    [InlineData("sessions/rollout.jsonl:stream")]
    [InlineData(@"C:\outside.txt")]
    public void BackupManifestPathsCannotEscapeTheDataDirectory(string path)
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var outside = Fixture.PutAt(f.Base, "outside.txt", "Unrelated content");
        var manifestPath = Path.Combine(backup.Path, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["files"]![0]!["path"] = path;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal("Unrelated content", File.ReadAllText(outside));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.Single(f.Rows("state_5.sqlite", "threads"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public void BackupHardLinksAreRejectedWithoutChangingTheirOtherName()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var file = Path.Combine(backup.Path, "files", Path.GetRelativePath(f.DataRoot, f.RolloutPaths[0]));
        var outside = Path.Combine(f.Base, "outside-backup-copy.jsonl");
        File.Copy(file, outside);
        var hash = Fixture.Checksum(outside);
        File.Delete(file);
        Assert.True(CreateHardLink(file, outside, IntPtr.Zero));
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(hash, Fixture.Checksum(outside));
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void RestoreDoesNotFollowADestinationJunction()
    {
        using var f = new Fixture();
        var rel = $"archived_sessions/restore-target/rollout-segment-{Ids[0]}.jsonl";
        f.Put(rel, Meta(Ids[0]));
        var backup = Delete(f);
        var destination = f.Data("archived_sessions/restore-target");
        Directory.Delete(destination);
        var outside = Path.Combine(f.Base, "outside-directory");
        Fixture.PutAt(outside, "keep.txt", "Unrelated content");
        using (var link = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{destination}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            link.WaitForExit();
            Assert.Equal(0, link.ExitCode);
        }
        try
        {
            Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
            Assert.Equal(["keep.txt"], Directory.GetFiles(outside).Select(Path.GetFileName));
            Assert.False(File.Exists(f.RolloutPaths[0]));
        }
        finally { Directory.Delete(destination); }
    }

    [Fact]
    public void ConflictingDestinationFileIsNotOverwritten()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        File.WriteAllText(f.RolloutPaths[0], "New unrelated file");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(before, Contents(f));
        Assert.Equal("New unrelated file", File.ReadAllText(f.RolloutPaths[0]));
    }

    [Fact]
    public void BackupFromAnotherDataRootCannotBeRestoredOrDeleted()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        using var other = new Fixture();
        Assert.Throws<CleanerException>(() => other.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Throws<CleanerException>(() => other.Cleaner.DeleteBackup(backup.Path));
        Assert.True(Directory.Exists(backup.Path));
        Assert.Equal(2, other.Rows("state_5.sqlite", "threads").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreRequiresBothWriterAndMaintenanceLocks(bool maintenance)
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var path = maintenance ? f.Data(".tmp/rollout-maintenance.lock") : f.Data($"thread-writer-locks/{Ids[0]}.lock");
        using (var held = Hold(path))
        {
            Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
            Assert.Single(f.Rows("state_5.sqlite", "threads"));
            Assert.False(File.Exists(f.RolloutPaths[0]));
        }
        Assert.Equal(1, f.Cleaner.Restore(backup.Path, [Ids[0]]).Restored);
    }

    [Fact]
    public void RestoreHoldsBothLocksUntilDatabasesHaveCommitted()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var checkedLocks = false;
        f.Cleaner.Hooks.BeforeCommit = _ =>
        {
            foreach (var relative in new[] { $"thread-writer-locks/{Ids[0]}.lock", ".tmp/rollout-maintenance.lock" })
            {
                using var stream = new FileStream(f.Data(relative), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                Assert.Throws<IOException>(() => stream.Lock(0, long.MaxValue));
            }
            checkedLocks = true;
        };
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.True(checkedLocks);
        Assert.False(File.Exists(f.Data($"thread-writer-locks/{Ids[0]}.lock")));
        using var maintenance = Hold(f.Data(".tmp/rollout-maintenance.lock"));
    }

    [Fact]
    public void RestoreRefusesWhileCodexIsRunning()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        f.Running = true;
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.Single(f.Rows("state_5.sqlite", "threads"));
    }

    [Fact]
    public void FailedLiveCleanupCanBeRecoveredEvenWhenTheMainThreadStillExists()
    {
        using var f = new Fixture();
        f.Running = true;
        var before = Contents(f);
        var file = File.ReadAllBytes(f.RolloutPaths[0]);
        var index = Lines(f.Data("session_index.jsonl"));
        var history = Lines(f.Data("history.jsonl"));
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        f.Cleaner.Hooks.BeforeCommit = number => { if (number == plan.Databases.Count) throw new IOException("Injected final live commit failure"); };
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        var backup = Assert.Single(f.Cleaner.Backups());
        Assert.True(backup.NeedsRecovery);
        Assert.Throws<CleanerException>(() => f.Cleaner.DeleteBackup(backup.Path));
        f.Running = false;
        f.Cleaner.Hooks.BeforeCommit = null;
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        f.Cleaner.RecoverBackup(backup.Path);
        Assert.Equal(before, Contents(f));
        Assert.Equal(file, File.ReadAllBytes(f.RolloutPaths[0]));
        Assert.Equal(index, Lines(f.Data("session_index.jsonl")));
        Assert.Equal(history, Lines(f.Data("history.jsonl")));
        Assert.False(Assert.Single(f.Cleaner.Backups()).NeedsRecovery);
        f.AssertNoTransactions();
    }

    [Fact]
    public void FailedLiveCleanupRecoveryPreservesUnrelatedConcurrentRows()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        f.Cleaner.Hooks.BeforeCommit = index => { if (index == plan.Databases.Count) throw new IOException("Injected final live commit failure"); };
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        var backup = Assert.Single(f.Cleaner.Backups());
        using (var db = f.Open("logs_2.sqlite"))
            Fixture.Insert(db, "logs", new() { ["ts"] = 2L, ["ts_nanos"] = 2L, ["level"] = "INFO", ["target"] = "fixture", ["feedback_log_body"] = "Unrelated concurrent content", ["thread_id"] = Ids[1] });
        f.Running = false;
        f.Cleaner.Hooks.BeforeCommit = null;
        f.Cleaner.RecoverBackup(backup.Path);
        Assert.Single(f.Rows("logs_2.sqlite", "logs"), row => (string?)row["feedback_log_body"] == "Unrelated concurrent content");
        Assert.Single(f.Rows("logs_2.sqlite", "logs"), row => (string?)row["thread_id"] == Ids[0]);
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
    }

    [Fact]
    public void RecoveryDoesNotOverwriteRowsChangedAfterPartialCleanup()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        f.Cleaner.Hooks.BeforeCommit = index => { if (index == plan.Databases.Count) throw new IOException("Injected final live commit failure"); };
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        var backup = Assert.Single(f.Cleaner.Backups());
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET title='Changed after partial cleanup' WHERE id=$p0", Ids[0]);
        var before = Contents(f);
        f.Running = false;
        f.Cleaner.Hooks.BeforeCommit = null;
        Assert.Throws<CleanerException>(() => f.Cleaner.RecoverBackup(backup.Path));
        Assert.Equal(before, Contents(f));
        Assert.True(Assert.Single(f.Cleaner.Backups()).NeedsRecovery);
    }

    [Fact]
    public void GlobalGroupsRestoreOnlyWhenExplicitlySelected()
    {
        using var f = new Fixture();
        var before = Contents(f);
        var memory = Catalog.MemoryDirs.SelectMany(directory => Directory.GetFiles(f.Data(directory), "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, File.ReadAllBytes);
        var desktop = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!;
        var historyBefore = desktop["electron-persisted-atom-state"]!["prompt-history"]!["global"]!.ToJsonString();
        var backup = Delete(f, new Selection([Ids[0]], ClearMemories: true, ClearSharedHistory: true));
        Assert.True(backup.ClearMemories && backup.ClearSharedHistory);
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Empty(f.Rows("memories_1.sqlite", "stage1_outputs"));
        Assert.Empty(f.Rows("logs_2.sqlite", "logs"));
        Assert.All(memory.Keys, path => Assert.False(File.Exists(path)));
        var changed = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!;
        changed["new-unrelated-setting"] = "Keep this later value";
        File.WriteAllText(f.Data(".codex-global-state.json"), changed.ToJsonString());
        var result = f.Cleaner.Restore(backup.Path, [], restoreMemories: true, restoreSharedHistory: true);
        Assert.True(result.RestoredMemories && result.RestoredSharedHistory);
        Assert.Equal(0, result.Restored);
        Assert.Equal(before, Contents(f));
        foreach (var (path, bytes) in memory) Assert.Equal(bytes, File.ReadAllBytes(path));
        var restored = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!;
        Assert.Equal(historyBefore, restored["electron-persisted-atom-state"]!["prompt-history"]!["global"]!.ToJsonString());
        Assert.Equal("invented retained input", (string?)restored["electron-persisted-atom-state"]!["prompt-history"]![Ids[1]]![0]);
        Assert.Equal("Keep this later value", (string?)restored["new-unrelated-setting"]);
    }

    [Fact]
    public void GlobalMemoryGroupConflictsAreRejectedBeforeRestoringItsRows()
    {
        using var f = new Fixture();
        var backup = Delete(f, new Selection([], ClearMemories: true));
        f.Put("memories/MEMORY.md", "New memory after cleanup");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [], restoreMemories: true));
        Assert.Equal(before, Contents(f));
        Assert.Equal("New memory after cleanup", File.ReadAllText(f.Data("memories/MEMORY.md")));
        Assert.False(File.Exists(f.Data("memories_v2/memory.txt")));
    }

    [Fact]
    public void IndexRestorePreservesLaterUnrelatedLinesAndDuplicateHistory()
    {
        using var f = new Fixture();
        var history = f.Data("history.jsonl");
        var original = File.ReadAllText(history);
        File.AppendAllText(history, original);
        var backup = Delete(f);
        var later = JsonSerializer.Serialize(new { session_id = Ids[1], ts = 7, text = "Later retained input" });
        File.AppendAllText(history, later + "\n");
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Equal(2, File.ReadAllLines(history).Count(line => line == original.TrimEnd()));
        Assert.Contains(later, File.ReadAllLines(history));
    }

    [Fact]
    public void ForkRestorationExplainsAnExistingParentOutsideTheBackup()
    {
        using var f = new Fixture();
        File.WriteAllText(f.RolloutPaths[1], Meta(Ids[1], Ids[0]));
        var backup = Delete(f, new Selection([Ids[1]]));
        var preview = f.Cleaner.PreviewRestore(backup.Path, [Ids[1]]);
        Assert.Contains(preview.Warnings, warning => warning.Contains(Ids[0], StringComparison.Ordinal));
        Assert.Equal(1, f.Cleaner.Restore(backup.Path, [Ids[1]]).Restored);
        Assert.Equal(Ids[0], f.Cleaner.ReadSessionMeta(Path.GetRelativePath(f.DataRoot, f.RolloutPaths[1]))?.Base);
    }

    [Fact]
    public void ForkCannotBeRestoredWhenItsExternalParentIsMissing()
    {
        using var f = new Fixture();
        File.WriteAllText(f.RolloutPaths[1], Meta(Ids[1], Ids[0]));
        var backup = Delete(f, new Selection([Ids[1]]));
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "DELETE FROM threads WHERE id=$p0", Ids[0]);
        File.Delete(f.RolloutPaths[0]);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[1]]));
        Assert.False(File.Exists(f.RolloutPaths[1]));
        Assert.Empty(f.Rows("state_5.sqlite", "threads"));
    }

    [Fact]
    public void ParentReferenceChangedAfterCleanupIsNotOverwritten()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Ids[0], Ids[1]);
        var backup = Delete(f);
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Fixture.Stale, Ids[1]);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(Fixture.Stale, f.Rows("state_5.sqlite", "threads").Single()["attached_thread_parent_id"]);
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void RecordOnlyRestorationNeverReadsOrWritesTheOutsideRollout()
    {
        using var f = new Fixture();
        var outside = Fixture.PutAt(f.Base, "outside-rollout.jsonl", "Unrelated outside data");
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", outside, Ids[0]);
        var backup = Delete(f, new Selection([Ids[0]]) { AllowRecordOnly = true });
        File.WriteAllText(outside, "Later outside data");
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Equal(outside, f.Rows("state_5.sqlite", "threads").Single(row => (string)row["id"]! == Ids[0])["rollout_path"]);
        Assert.Equal("Later outside data", File.ReadAllText(outside));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void FailureAfterARestoreCommitRollsBackAndTheBackupCanBeRetried()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var deleted = Contents(f);
        var index = File.ReadAllBytes(f.Data("session_index.jsonl"));
        var history = File.ReadAllBytes(f.Data("history.jsonl"));
        f.Cleaner.Hooks.BeforeCommit = number => { if (number == 2) throw new IOException("Injected restore commit failure"); };
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(deleted, Contents(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(index, File.ReadAllBytes(f.Data("session_index.jsonl")));
        Assert.Equal(history, File.ReadAllBytes(f.Data("history.jsonl")));
        Assert.True(Directory.Exists(backup.Path));
        f.AssertNoTransactions();
        f.Cleaner.Hooks.BeforeCommit = null;
        Assert.Equal(1, f.Cleaner.Restore(backup.Path, [Ids[0]]).Restored);
    }

    [Fact]
    public void BackupWriteFailureStopsBeforeDeletingData()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Cleaner.BackupRoot)!);
        File.WriteAllText(f.Cleaner.BackupRoot, "This path is a file");
        var before = Contents(f);
        Assert.ThrowsAny<Exception>(() => f.Cleaner.Execute(f.Cleaner.PlanCleanup(new Selection([Ids[0]])).Id));
        Assert.Equal(before, Contents(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void DeletingAnOldBackupDoesNotChangeCurrentChatData()
    {
        using var f = new Fixture();
        var backup = Delete(f);
        var before = Contents(f);
        f.Cleaner.DeleteBackup(backup.Path);
        Assert.False(Directory.Exists(backup.Path));
        Assert.Empty(f.Cleaner.Backups());
        Assert.Equal(before, Contents(f));
        Assert.True(File.Exists(f.RolloutPaths[1]));
    }

    [Theory]
    [InlineData("project_id", "projects")]
    [InlineData("thread_section_id", "thread_sections")]
    public void MissingProjectOrSectionIsRejectedBeforeRestoration(string column, string table)
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite"))
        {
            Fixture.Insert(db, table, new() { ["id"] = "shared-dependency", ["name"] = "Shared dependency" });
            Fixture.Exec(db, $"UPDATE threads SET {Db.Quote(column)}=$p0 WHERE id=$p1", "shared-dependency", Ids[0]);
        }
        var backup = Delete(f);
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, $"DELETE FROM {Db.Quote(table)} WHERE id=$p0", "shared-dependency");
        var before = Contents(f);
        Assert.Throws<CleanerException>(() => f.Cleaner.Restore(backup.Path, [Ids[0]]));
        Assert.Equal(before, Contents(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void ThreadInsertTriggersDoNotChangeTheRestoredOriginalValues()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite"))
            Fixture.Exec(db, "UPDATE threads SET created_at_ms=NULL, updated_at_ms=NULL, recency_at=0, recency_at_ms=0 WHERE id=$p0", Ids[0]);
        var before = Contents(f);
        var backup = Delete(f);
        f.Cleaner.Restore(backup.Path, [Ids[0]]);
        Assert.Equal(before, Contents(f));
    }

    [Fact]
    public void OriginallyMissingRolloutIsNotRecreatedAsAnEmptyFile()
    {
        using var f = new Fixture();
        File.Delete(f.RolloutPaths[0]);
        var backup = Delete(f);
        Assert.Equal(1, f.Cleaner.Restore(backup.Path, [Ids[0]]).Restored);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
    }

    [Fact]
    public void LiveDeletionStopsWhenSelectedRowsChangeAfterTheBackup()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        var injected = false;
        f.Cleaner.Hooks.BeforeCommit = number =>
        {
            if (number != 1) return;
            using var db = f.Open("logs_2.sqlite");
            Fixture.Insert(db, "logs", new() { ["ts"] = 3L, ["ts_nanos"] = 3L, ["level"] = "INFO", ["target"] = "fixture", ["feedback_log_body"] = "Selected content added after backup", ["thread_id"] = Ids[0] });
            injected = true;
        };
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        Assert.True(injected);
        Assert.Single(f.Rows("logs_2.sqlite", "logs"), row => (string?)row["feedback_log_body"] == "Selected content added after backup");
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.True(Assert.Single(f.Cleaner.Backups()).NeedsRecovery);
    }

    [Fact]
    public void LiveBackupEstimateIncludesBothCopiesOfAnIndexBeingRewritten()
    {
        using var f = new Fixture();
        f.Running = true;
        var before = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        Assert.True(before.Live);
        Assert.Contains(before.Files, file => file.Path == Catalog.History && !file.Remove);
        var originalEstimate = f.Cleaner.EstimateBackupBytes(before.Id);
        var added = JsonSerializer.Serialize(new { session_id = Ids[1], ts = 2, text = "Additional retained synthetic history" }) + "\n";
        File.AppendAllText(f.Data(Catalog.History), added);
        var after = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        Assert.True(after.Live);
        Assert.Contains(after.Files, file => file.Path == Catalog.History && !file.Remove);
        Assert.Equal(2L * Encoding.UTF8.GetByteCount(added), f.Cleaner.EstimateBackupBytes(after.Id) - originalEstimate);
    }
}
