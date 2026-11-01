using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class CleanerTests
{
    private static readonly string[] Ids = Fixture.Ids;
    private const string Stale = Fixture.Stale;

    private static Selection Pick(params string[] ids) => new(ids);

    private static readonly Dictionary<string, string[]> LinkedTables = new()
    {
        ["state_5.sqlite"] = ["threads", "thread_attachments", "thread_dynamic_tools", "thread_read_receipts", "guardian_review_feedback"],
        ["thread_history_1.sqlite"] = ["thread_turns", "thread_items", "thread_realtime_items", "thread_history_projection_state"],
        ["memories_1.sqlite"] = ["stage1_outputs"],
        ["logs_2.sqlite"] = ["logs"],
        ["goals_1.sqlite"] = ["thread_goals", "thread_goal_continuation_deferrals"],
        ["queue_1.sqlite"] = ["queued_items", "queued_thread_revisions"],
        ["sqlite/codex-dev.db"] = ["automation_runs", "inbox_items", "live_visualization_suggestions", "local_thread_catalog", "local_thread_catalog_scan_entries", "thread_timeline_ledger"],
    };

    private static Dictionary<string, string> Linked(Fixture f, string id) =>
        LinkedTables.SelectMany(db => db.Value.Select(table => (Key: $"{db.Key}/{table}", Rows: f.Rows(db.Key, table)
            .Where(r => (table == "threads" ? r["id"] : r["thread_id"]) as string == id)
            .Select(r => string.Join('|', r.Select(c => $"{c.Key}={(c.Value is byte[] b ? Convert.ToHexString(b) : c.Value)}"))))))
            .ToDictionary(x => x.Key, x => string.Join('\n', x.Rows));

    private static void AssertGone(Fixture f, string id)
    {
        foreach (var (table, rows) in Linked(f, id)) Assert.True(rows.Length == 0, table);
    }

    private static int ThreadCount(Fixture f) => f.Rows("state_5.sqlite", "threads").Count;

    private static void Execute(Fixture f, Selection selection) => f.Cleaner.Execute(f.Cleaner.PlanCleanup(selection).Id);

    [Fact]
    public void PreviewIsReadOnlyAndEnumeratesSyntheticChats()
    {
        using var f = new Fixture();
        var before = Fixture.Checksum(f.Data("state_5.sqlite"));
        Assert.Equal(2, f.Cleaner.Snapshot().Chats.Count);
        Assert.NotEmpty(f.Cleaner.PlanCleanup(Pick(Ids[0])).Id);
        Assert.Equal(before, Fixture.Checksum(f.Data("state_5.sqlite")));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void ExecutionRemovesSelectedChatCopiesAndPreservesUnselectedData()
    {
        using var f = new Fixture();
        var other = Linked(f, Ids[1]);
        string[] protectedFiles = ["config.toml", "auth.json", "plugins/example/README.md", "skills/example/SKILL.md", "mystery-user-file.txt"];
        var protectedBefore = protectedFiles.ToDictionary(p => p, p => Fixture.Checksum(f.Data(p)));
        Execute(f, Pick(Ids[0]));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        AssertGone(f, Ids[0]);
        Assert.Equal(other, Linked(f, Ids[1]));
        Assert.Empty(f.Rows("state_5.sqlite", "thread_spawn_edges"));
        Assert.DoesNotContain(Ids[0], File.ReadAllText(f.Data("session_index.jsonl")));
        foreach (var p in protectedFiles) Assert.Equal(protectedBefore[p], Fixture.Checksum(f.Data(p)));
        Assert.True(File.Exists(f.Data("memories/MEMORY.md")), "global memory needs explicit opt-in");
        f.AssertNoTransactions();
    }

    [Fact]
    public void ClearingAllMemoriesPreservesChatHistory()
    {
        using var f = new Fixture();
        var items = f.Rows("thread_history_1.sqlite", "thread_items").Count;
        Execute(f, new Selection([], ClearMemories: true));
        Assert.Empty(f.Rows("memories_1.sqlite", "stage1_outputs"));
        Assert.Empty(f.Rows("memories_1.sqlite", "jobs"));
        Assert.Single(f.Rows("memories_1.sqlite", "consolidation_progress"));
        foreach (var rel in new[] { "memories/MEMORY.md", "memories_v2/memory.txt", "memories_extensions/extension.txt" }) Assert.False(File.Exists(f.Data(rel)), rel);
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(items, f.Rows("thread_history_1.sqlite", "thread_items").Count);
        Assert.True(File.Exists(f.Data("skills/example/SKILL.md")));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RunningCodexBlocksDeletionEvenIfPlannedWhileStopped()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        f.Running = true;
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void FailedProcessVerificationBlocksDeletion()
    {
        using var f = new Fixture();
        var cleaner = new Cleaner(f.DataRoot, f.TrashRoot, () => throw new InvalidOperationException("synthetic process query failure"));
        var plan = cleaner.PlanCleanup(Pick(Ids[0]));
        Assert.Throws<InvalidOperationException>(() => cleaner.Execute(plan.Id));
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void ConsumedPlanCannotRepeat()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        f.Cleaner.Execute(plan.Id);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.Contains("重新预览", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        f.AssertNoTransactions();
    }

    [Fact]
    public void ChangesAfterPreviewInvalidatePlan()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        File.AppendAllText(f.RolloutPaths[0], "\n");
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void MissingRolloutIsRemovableAsStaleIndexEntry()
    {
        using var f = new Fixture();
        File.Delete(f.RolloutPaths[0]);
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.True(chat.CanDelete);
        Assert.True(chat.RolloutMissing);
        Execute(f, Pick(Ids[0]));
        AssertGone(f, Ids[0]);
        Assert.True(File.Exists(f.RolloutPaths[1]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void UnsupportedSchemaIsRefusedWithoutDeletingRollout()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "CREATE TABLE future_chat_cache (thread_id TEXT, message TEXT)");
        var state = f.Cleaner.Snapshot();
        Assert.False(state.Capabilities.DeleteChats);
        Assert.Matches("结构|版本", state.Databases["state_5.sqlite"].Reason);
        Assert.Contains(state.Warnings, w => w.Contains("state_5.sqlite"));
        Assert.Throws<CleanerException>(() => Execute(f, Pick(Ids[0])));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(2, ThreadCount(f));
    }

    [Theory]
    [InlineData("../private.txt")]
    [InlineData("sessions/../../private.txt")]
    [InlineData(@"C:\private.txt")]
    [InlineData("sessions/name.")]
    public void RelativePathsCannotEscapeDataRoot(string relative) =>
        Assert.Throws<CleanerException>(() => SafePaths.ValidRelative(relative));

    [Fact]
    public void OutsideRolloutPathIsRefusedWithoutTouchingIt()
    {
        using var f = new Fixture();
        var outside = Fixture.PutAt(f.Base, "outside-rollout.jsonl", "Unrelated outside data");
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", outside, Ids[0]);
        Assert.Matches("路径|目录外", f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]).BlockedReason);
        Assert.Throws<CleanerException>(() => Execute(f, Pick(Ids[0])));
        Assert.Equal("Unrelated outside data", File.ReadAllText(outside));
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void MemoryJunctionCannotMakeCleanupTraverseOutside()
    {
        using var f = new Fixture();
        var outsideDir = Path.Combine(f.Base, "unrelated");
        Fixture.PutAt(outsideDir, "keep.txt", "Must survive");
        var link = f.Data(@"memories\junction");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outsideDir}\"") { CreateNoWindow = true, UseShellExecute = false })!) mklink.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.Throws<CleanerException>(() => Execute(f, new Selection([], ClearMemories: true)));
        Assert.Equal("Must survive", File.ReadAllText(Path.Combine(outsideDir, "keep.txt")));
        Directory.Delete(link);
    }

    [Fact]
    public void AutomationReferencesNameTheBlockerAndLeaveScheduleUnchanged()
    {
        using var f = new Fixture();
        using (var db = f.Open("sqlite/codex-dev.db"))
        {
            Fixture.Insert(db, "automations", new() { ["id"] = "fixture-automation", ["name"] = "Invented schedule", ["prompt"] = "Invented prompt", ["status"] = "ACTIVE", ["created_at"] = 1L, ["updated_at"] = 1L, ["target_thread_id"] = Ids[0], ["kind"] = "heartbeat", ["next_run_at"] = 123L });
            Fixture.Insert(db, "automations", new() { ["id"] = "retained-automation", ["name"] = "Retained schedule", ["prompt"] = "Retained prompt", ["status"] = "ACTIVE", ["created_at"] = 1L, ["updated_at"] = 1L, ["target_thread_id"] = Ids[1], ["kind"] = "heartbeat", ["next_run_at"] = 456L });
        }
        var before = f.Rows("sqlite/codex-dev.db", "automations").Count;
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.Contains("自动化", chat.BlockedReason);
        Assert.Contains("Invented schedule", chat.BlockedReason);
        Assert.Contains("自动化", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[0]))).Message);
        Assert.Equal(before, f.Rows("sqlite/codex-dev.db", "automations").Count);
        Assert.True(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void MultilinePromptExamplesDoNotCreateFalseTargets()
    {
        using var f = new Fixture();
        const string rel = "automations/fixture/automation.toml";
        var text = $"name = \"Example task\"\nstatus = \"ACTIVE\"\ntarget_thread_id = \"{Ids[1]}\"\nprompt = \"\"\"\nAn invented configuration example:\ntarget_thread_id = \"{Ids[0]}\"\nstatus = \"PAUSED\"\n\"\"\"\n";
        f.Put(rel, text);
        var state = f.Cleaner.Snapshot();
        Assert.True(state.Chats.Single(c => c.Id == Ids[0]).CanDelete);
        Assert.Contains("自动化", state.Chats.Single(c => c.Id == Ids[1]).BlockedReason);
        Execute(f, Pick(Ids[0]));
        Assert.Equal(text, File.ReadAllText(f.Data(rel)));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RemoteHostRecordsSurviveLocalChatDeletion()
    {
        using var f = new Fixture();
        string[] tables = ["live_visualization_suggestions", "local_thread_catalog", "local_thread_catalog_scan_entries", "thread_timeline_ledger"];
        using (var db = f.Open("sqlite/codex-dev.db"))
        {
            Fixture.Insert(db, "local_thread_catalog_hosts", new() { ["host_id"] = "remote-fixture", ["host_kind"] = "ssh" });
            foreach (var table in tables) CopyRow(db, table, Ids[0], new() { ["host_id"] = "remote-fixture" });
        }
        Execute(f, Pick(Ids[0]));
        foreach (var table in tables) Assert.Single(f.Rows("sqlite/codex-dev.db", table), r => (string)r["host_id"]! == "remote-fixture");
        f.AssertNoTransactions();
    }

    private static void CopyRow(Microsoft.Data.Sqlite.SqliteConnection db, string table, string threadId, Dictionary<string, object?> overrides)
    {
        var source = Fixture.Query(db, $"SELECT * FROM \"{table}\" WHERE thread_id=$p0", threadId).First();
        foreach (var (k, v) in overrides) source[k] = v;
        Fixture.Insert(db, table, source);
    }

    [Fact]
    public void ColdWalDatabasesCleanOnceWithoutFalseStaleFailure()
    {
        using var f = new Fixture();
        foreach (var name in LinkedTables.Keys) using (var db = f.Open(name)) Fixture.Exec(db, "PRAGMA journal_mode=WAL");
        Execute(f, Pick(Ids[0]));
        Assert.Equal(1, ThreadCount(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void SharedHistoryCleanupKeepsPerChatDrafts()
    {
        using var f = new Fixture();
        Execute(f, new Selection([], ClearSharedHistory: true));
        Assert.Empty(f.Rows("logs_2.sqlite", "logs"));
        var global = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!;
        var history = global["electron-persisted-atom-state"]!["prompt-history"]!.AsObject();
        Assert.False(history.ContainsKey("global"));
        Assert.Equal("invented retained input", (string?)history[Ids[1]]![0]);
        Assert.Equal(f.Base, (string?)global["saved-workspace-roots"]![0]);
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void SecondFileDeletionFailureRollsBackEverything()
    {
        using var f = new Fixture();
        var (a, b) = (Linked(f, Ids[0]), Linked(f, Ids[1]));
        var plan = f.Cleaner.PlanCleanup(Pick(Ids));
        var removedFirst = false;
        f.Cleaner.Hooks.BeforeDelete = file =>
        {
            if (file == Path.GetFullPath(f.RolloutPaths[0])) removedFirst = true;
            if (file == Path.GetFullPath(f.RolloutPaths[1])) { Assert.True(removedFirst); throw new IOException("Injected second rollout unlink failure"); }
        };
        Assert.Contains("Injected second rollout unlink failure", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.True(File.Exists(f.RolloutPaths[0]), "previously removed file is automatically restored");
        Assert.True(File.Exists(f.RolloutPaths[1]));
        Assert.Equal(a, Linked(f, Ids[0]));
        Assert.Equal(b, Linked(f, Ids[1]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void FailureAfterADatabaseCommitRestoresEverything()
    {
        using var f = new Fixture();
        var (a, b) = (Linked(f, Ids[0]), Linked(f, Ids[1]));
        var global = File.ReadAllText(f.Data(".codex-global-state.json"));
        var plan = f.Cleaner.PlanCleanup(Pick(Ids));
        f.Cleaner.Hooks.BeforeCommit = n => { if (n == 2) throw new IOException("Injected later database commit failure"); };
        Assert.Contains("Injected later database commit failure", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal(a, Linked(f, Ids[0]));
        Assert.Equal(b, Linked(f, Ids[1]));
        Assert.True(File.Exists(f.RolloutPaths[0]) && File.Exists(f.RolloutPaths[1]));
        Assert.Equal(global, File.ReadAllText(f.Data(".codex-global-state.json")));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RollbackFailurePreservesFaultMaterialsAndBlocksLaterCleanup()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids));
        f.Cleaner.Hooks.BeforeDelete = file => { if (file == Path.GetFullPath(f.RolloutPaths[1])) throw new IOException("Injected delete failure"); };
        f.Cleaner.Hooks.BeforeRollback = () => throw new IOException("Injected rollback failure");
        Assert.Contains("Injected rollback failure", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        f.Cleaner.Hooks.BeforeDelete = null;
        f.Cleaner.Hooks.BeforeRollback = null;
        var packages = Directory.GetDirectories(f.TrashRoot);
        Assert.Single(packages);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(packages[0], "manifest.json")))!;
        Assert.Equal("needs-recovery", (string?)manifest["status"]);
        Assert.NotEmpty(manifest["databases"]!.AsArray());
        Assert.NotEmpty(f.Cleaner.Snapshot().RecoveryIssues);
        var before = Fixture.Checksum(f.RolloutPaths[1]);
        Assert.Matches("故障|中断", Assert.Throws<CleanerException>(() => Execute(f, Pick(Ids[1]))).Message);
        Assert.Equal(before, Fixture.Checksum(f.RolloutPaths[1]));
        Assert.Equal(packages, Directory.GetDirectories(f.TrashRoot));
    }

    [Fact]
    public void ArchivedChatDeletionRemovesItsArchiveFile()
    {
        using var f = new Fixture();
        var archive = f.Data($"archived_sessions/{Path.GetFileName(f.RolloutPaths[0])}");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Move(f.RolloutPaths[0], archive);
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET archived=1,rollout_path=$p0 WHERE id=$p1", archive, Ids[0]);
        Assert.True(f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]).Archived);
        Execute(f, Pick(Ids[0]));
        Assert.False(File.Exists(archive));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void ExtendedPathPrefixMapsToTheSameRollout()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", @"\\?\" + f.RolloutPaths[0], Ids[0]);
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.True(chat.CanDelete, chat.BlockedReason);
        Execute(f, Pick(Ids[0]));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        f.AssertNoTransactions();
    }

    private sealed record DesktopContext(JsonNode Before, string LocalHost, string LocalUnreadHost);

    private static DesktopContext AddDesktopReferences(Fixture f, string target)
    {
        var value = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!.AsObject();
        var localHost = "local:" + f.DataRoot;
        var localUnreadHost = "local:" + new string('a', 64);
        var otherLocal = "local:" + new string('b', 64);
        JsonArray Pair() => new(target, Ids[1]);
        value["app-server-projects-migration-by-host"] = new JsonObject
        {
            [localHost] = new JsonObject { ["version"] = 1, ["projectsMigrated"] = true, ["threadAssignmentsMigrated"] = false, ["pendingThreadAssignmentIds"] = Pair() },
            ["ssh:fixture"] = new JsonObject { ["version"] = 1, ["projectsMigrated"] = true, ["threadAssignmentsMigrated"] = false, ["pendingThreadAssignmentIds"] = Pair() },
        };
        value["electron-thread-read-state-v1"] = new JsonObject
        {
            ["version"] = 1,
            ["unreadByIdentity"] = new JsonObject { ["fixture-account"] = new JsonObject { [localUnreadHost] = Pair(), [otherLocal] = Pair(), ["ssh:fixture"] = Pair() } },
            ["legacyMigration"] = new JsonObject
            {
                ["identityKey"] = "fixture-account",
                ["unreadThreadIdsByHostId"] = new JsonObject { ["local"] = Pair(), ["ssh:fixture"] = Pair() },
                ["adoptedHostIds"] = new JsonObject { ["local"] = localUnreadHost },
            },
        };
        value["thread-project-assignments"] = new JsonObject { [target] = "project-fixture", [Ids[1]] = "project-fixture" };
        value["archived-thread-ids"] = new JsonArray(target);
        value["pinned-thread-ids"] = Pair();
        var atom = value["electron-persisted-atom-state"]!.AsObject();
        atom["heartbeat-thread-permissions-by-id"] = new JsonObject { [target] = new JsonObject { ["allow"] = true }, [Ids[1]] = new JsonObject { ["allow"] = false } };
        atom["client-thread-bindings-v1"] = new JsonObject { ["client-new-thread:fixture-client"] = target, ["client-new-thread:retained-client"] = Ids[1] };
        JsonObject Recent(string host, string thread, string path) => new() { ["hostId"] = host, ["threadId"] = thread, ["path"] = path, ["updatedAt"] = 1 };
        atom["recent-workspace-files-v3"] = new JsonObject
        {
            ["selected"] = Recent("local", "local:" + target, "selected.txt"),
            ["selected-legacy"] = Recent("local", target, "selected-legacy.txt"),
            ["selected-client"] = Recent("local", "client-new-thread:fixture-client", "selected-client.txt"),
            ["retained"] = Recent("local", "local:" + Ids[1], "retained.txt"),
            ["remote"] = Recent("ssh:fixture", "local:" + target, "remote.txt"),
        };
        value["unknown-future-state"] = new JsonObject { ["text"] = target, ["ids"] = Pair(), ["settings"] = new JsonObject { ["keep"] = true } };
        foreach (var rel in new[] { ".codex-global-state.json", ".codex-global-state.json.bak" }) f.Put(rel, value.ToJsonString());
        return new DesktopContext(value.DeepClone(), localHost, localUnreadHost);
    }

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(x => (string)x!).ToArray();

    private static void AssertDesktopReferences(Fixture f, string target, DesktopContext context)
    {
        foreach (var rel in new[] { ".codex-global-state.json", ".codex-global-state.json.bak" })
        {
            var value = JsonNode.Parse(File.ReadAllText(f.Data(rel)))!;
            var migration = value["app-server-projects-migration-by-host"]!;
            Assert.Equal([Ids[1]], Strings(migration[context.LocalHost]!["pendingThreadAssignmentIds"]));
            Assert.True(JsonNode.DeepEquals(context.Before["app-server-projects-migration-by-host"]!["ssh:fixture"], migration["ssh:fixture"]));
            var unread = value["electron-thread-read-state-v1"]!;
            var account = unread["unreadByIdentity"]!["fixture-account"]!;
            Assert.Equal([Ids[1]], Strings(account[context.LocalUnreadHost]));
            Assert.Equal([target, Ids[1]], Strings(account["ssh:fixture"]));
            Assert.Equal([target, Ids[1]], Strings(account["local:" + new string('b', 64)]));
            Assert.Equal([Ids[1]], Strings(unread["legacyMigration"]!["unreadThreadIdsByHostId"]!["local"]));
            Assert.Equal([target, Ids[1]], Strings(unread["legacyMigration"]!["unreadThreadIdsByHostId"]!["ssh:fixture"]));
            Assert.Equal([Ids[1]], value["thread-project-assignments"]!.AsObject().Select(p => p.Key));
            Assert.Equal([Ids[1]], Strings(value["pinned-thread-ids"]));
            Assert.Empty(Strings(value["archived-thread-ids"]));
            var atom = value["electron-persisted-atom-state"]!;
            Assert.Equal([Ids[1]], atom["heartbeat-thread-permissions-by-id"]!.AsObject().Select(p => p.Key));
            Assert.Equal(["client-new-thread:retained-client"], atom["client-thread-bindings-v1"]!.AsObject().Select(p => p.Key));
            Assert.Equal(["retained", "remote"], atom["recent-workspace-files-v3"]!.AsObject().Select(p => p.Key));
            Assert.True(JsonNode.DeepEquals(context.Before["unknown-future-state"], value["unknown-future-state"]));
            Assert.True(JsonNode.DeepEquals(context.Before["saved-workspace-roots"], value["saved-workspace-roots"]));
        }
    }

    [Fact]
    public void SelectedDeletionUpdatesDesktopReferencesInBothStateFiles()
    {
        using var f = new Fixture();
        var context = AddDesktopReferences(f, Ids[0]);
        using (var db = f.Open("state_5.sqlite"))
        {
            Fixture.Insert(db, "projects", new() { ["id"] = "project-fixture", ["name"] = "Keep project", ["position"] = 0L, ["created_at_ms"] = 1L, ["updated_at_ms"] = 1L });
            Fixture.Insert(db, "project_roots", new() { ["project_id"] = "project-fixture", ["position"] = 0L, ["path"] = f.Base });
            Fixture.Exec(db, "UPDATE threads SET project_id='project-fixture'");
        }
        using (var db = f.Open("sqlite/codex-dev.db")) Fixture.Insert(db, "local_thread_catalog_metadata", new() { ["id"] = 1L, ["catalog_revision"] = 7L });
        Execute(f, Pick(Ids[0]));
        AssertDesktopReferences(f, Ids[0], context);
        Assert.Single(f.Rows("state_5.sqlite", "projects"));
        Assert.Single(f.Rows("state_5.sqlite", "project_roots"));
        Assert.Equal("project-fixture", f.Rows("state_5.sqlite", "threads").Single()["project_id"]);
        Assert.True((long)f.Rows("sqlite/codex-dev.db", "local_thread_catalog_metadata").Single()["catalog_revision"]! > 7);
        f.AssertNoTransactions();
    }

    [Fact]
    public void RepairRemovesStaleLocalReferencesAndPreservesValidOnes()
    {
        using var f = new Fixture();
        var context = AddDesktopReferences(f, Stale);
        var before = Linked(f, Ids[0]);
        var global = Fixture.Checksum(f.Data(".codex-global-state.json"));
        var plan = f.Cleaner.PlanRepair();
        Assert.Equal(global, Fixture.Checksum(f.Data(".codex-global-state.json")));
        f.Cleaner.ExecuteRepair(plan.Id);
        AssertDesktopReferences(f, Stale, context);
        Assert.Equal(2, ThreadCount(f));
        Assert.Equal(before, Linked(f, Ids[0]));
        Assert.All(f.RolloutPaths, p => Assert.True(File.Exists(p)));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RepairRemovesOrphanLocalCatalogEntriesButKeepsRemoteCopies()
    {
        using var f = new Fixture();
        string[] tables = ["local_thread_catalog", "local_thread_catalog_scan_entries", "thread_timeline_ledger"];
        using (var db = f.Open("sqlite/codex-dev.db"))
        {
            Fixture.Insert(db, "local_thread_catalog_hosts", new() { ["host_id"] = "ssh:fixture", ["host_kind"] = "ssh" });
            Fixture.Insert(db, "local_thread_catalog_metadata", new() { ["id"] = 1L, ["catalog_revision"] = 10L });
            foreach (var table in tables) foreach (var host in new[] { "local", "ssh:fixture" }) CopyRow(db, table, Ids[0], new() { ["thread_id"] = Stale, ["host_id"] = host });
        }
        f.Cleaner.ExecuteRepair(f.Cleaner.PlanRepair().Id);
        foreach (var table in tables) Assert.Equal("ssh:fixture", f.Rows("sqlite/codex-dev.db", table).Single(r => (string)r["thread_id"]! == Stale)["host_id"]);
        Assert.True((long)f.Rows("sqlite/codex-dev.db", "local_thread_catalog_metadata").Single()["catalog_revision"]! > 10);
        Assert.Equal(2, ThreadCount(f));
        f.AssertNoTransactions();
    }

    [Fact]
    public void DeletingParentDetachesAttachedChildChats()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Ids[0], Ids[1]);
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        Assert.Equal(new PlanTable("threads", 1, true), plan.Databases.Single(d => d.Path == "state_5.sqlite").Tables.Single(t => t.ClearsReference));
        f.Cleaner.Execute(plan.Id);
        var thread = f.Rows("state_5.sqlite", "threads").Single();
        Assert.Equal(Ids[1], thread["id"]);
        Assert.Null(thread["attached_thread_parent_id"]);
        Assert.Equal([Ids[1]], f.Rows("state_5.sqlite", "thread_read_receipts").Select(r => (string)r["thread_id"]!));
        f.AssertNoTransactions();
    }

    [Fact]
    public void DeletingParentAndChildTogetherNeedsNoDetach()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Ids[0], Ids[1]);
        var plan = f.Cleaner.PlanCleanup(Pick(Ids));
        Assert.Equal(0, plan.Databases.Single(d => d.Path == "state_5.sqlite").Tables.Single(t => t.ClearsReference).Rows);
        f.Cleaner.Execute(plan.Id);
        Assert.Equal(0, ThreadCount(f));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RepairClearsStaleReadReceiptsAndParentReferences()
    {
        using var f = new Fixture();
        using (var db = f.Open("state_5.sqlite"))
        {
            Fixture.Insert(db, "thread_read_receipts", new() { ["thread_id"] = Stale, ["revision"] = "1" });
            Fixture.Exec(db, "UPDATE threads SET attached_thread_parent_id=$p0 WHERE id=$p1", Stale, Ids[1]);
        }
        var plan = f.Cleaner.PlanRepair();
        Assert.Equal([Stale], plan.Selection.ChatIds);
        f.Cleaner.ExecuteRepair(plan.Id);
        var threads = f.Rows("state_5.sqlite", "threads");
        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.Null(t["attached_thread_parent_id"]));
        Assert.Equal(Ids.Order(), f.Rows("state_5.sqlite", "thread_read_receipts").Select(r => (string)r["thread_id"]!).Order());
        f.AssertNoTransactions();
    }

    [Fact]
    public void RepairDoesNotGuessUnknownOrRemoteIdentifiersAreLocal()
    {
        using var f = new Fixture();
        var file = f.Data(".codex-global-state.json");
        var value = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        value["pinned-thread-ids"] = new JsonArray(Stale);
        value["app-server-projects-migration-by-host"] = new JsonObject { ["ssh:fixture"] = new JsonObject { ["version"] = 1, ["pendingThreadAssignmentIds"] = new JsonArray(Stale) } };
        File.WriteAllText(file, value.ToJsonString());
        var before = Fixture.Checksum(file);
        Assert.True(f.Cleaner.PlanRepair().IsEmpty);
        Assert.Equal(before, Fixture.Checksum(file));
        Assert.Equal(2, ThreadCount(f));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RepairRefusesACandidateThatBecomesLiveDuringPreview()
    {
        using var f = new Fixture();
        AddDesktopReferences(f, Stale);
        var global = Fixture.Checksum(f.Data(".codex-global-state.json"));
        var newRollout = "";
        f.Cleaner.Hooks.AfterJsonEdits = () =>
        {
            if (newRollout.Length > 0) return;
            newRollout = f.Put($"sessions/2026/09/27/rollout-{Stale}.jsonl", $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{Stale}\"}}}}\n");
            using var db = f.Open("state_5.sqlite");
            var source = Fixture.Query(db, "SELECT * FROM threads WHERE id=$p0", Ids[0]).Single();
            source["id"] = Stale;
            source["title"] = "New thread created during preview";
            source["rollout_path"] = newRollout;
            Fixture.Insert(db, "threads", source);
        };
        Assert.Matches("变化|重新|出现", Assert.Throws<CleanerException>(() => f.Cleaner.ExecuteRepair(f.Cleaner.PlanRepair().Id)).Message);
        Assert.Equal(3, ThreadCount(f));
        Assert.Equal(global, Fixture.Checksum(f.Data(".codex-global-state.json")));
        Assert.True(File.Exists(newRollout));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RolloutPathChangeDuringPreviewCannotBecomeTheBaseline()
    {
        using var f = new Fixture();
        var replacement = f.Put("sessions/replacement.jsonl", File.ReadAllText(f.RolloutPaths[0]));
        f.Cleaner.Hooks.AfterJsonEdits = () => { using var db = f.Open("state_5.sqlite"); Fixture.Exec(db, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", replacement, Ids[0]); };
        Assert.Contains("生成预览时发生变化", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[0]))).Message);
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]) && File.Exists(replacement));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RolloutMetadataReplacedDuringPreviewInvalidatesPlan()
    {
        using var f = new Fixture();
        f.Cleaner.Hooks.AfterCheckRollout = _ => File.WriteAllText(f.RolloutPaths[0], "{\"type\":\"session_meta\",\"payload\":{\"id\":\"unrelated-fixture-chat\"}}\n");
        Assert.Contains("生成预览时发生变化", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[0]))).Message);
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void MissingRolloutThatAppearsAfterPreviewRequiresNewPreview()
    {
        using var f = new Fixture();
        var content = File.ReadAllBytes(f.RolloutPaths[0]);
        File.Delete(f.RolloutPaths[0]);
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        File.WriteAllBytes(f.RolloutPaths[0], content);
        Assert.Contains("预览后已改变", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal(2, ThreadCount(f));
        Assert.Equal(content, File.ReadAllBytes(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void CorruptTemporaryBackupAbortsBeforeDeletingAnything()
    {
        using var f = new Fixture();
        var before = Linked(f, Ids[0]);
        var rollout = Fixture.Checksum(f.RolloutPaths[0]);
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        var rel = Path.GetRelativePath(f.DataRoot, f.RolloutPaths[0]).Replace('\\', '/');
        f.Cleaner.Hooks.AfterBackupCopy = (package, backup) => { if (backup == $"files/{rel}") File.AppendAllText(Path.Combine(f.TrashRoot, package, backup), "Injected backup corruption"); };
        Assert.Contains("副本与原文件不一致", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal(before, Linked(f, Ids[0]));
        Assert.Equal(rollout, Fixture.Checksum(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void DatabaseWriteJustBeforeWriteLocksAbortsWithoutOverwriting()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        var checks = 0;
        f.Cleaner.Hooks.AfterFingerprintCheck = () =>
        {
            if (++checks != 2) return;
            using var db = f.Open("state_5.sqlite");
            Fixture.Exec(db, "UPDATE threads SET title='Concurrent fixture update' WHERE id=$p0", Ids[0]);
        };
        Assert.Contains("预览后已改变", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal("Concurrent fixture update", f.Rows("state_5.sqlite", "threads").Single(r => (string)r["id"]! == Ids[0])["title"]);
        Assert.True(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public async Task InProgressReadPreventsAWriteAndReleasesAfterward()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Cleaner.Hooks.DuringRead = () => { started.Set(); release.Wait(); };
        var reading = Task.Run(f.Cleaner.Snapshot);
        started.Wait();
        try { Assert.Contains("读取尚未完成", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message); }
        finally { release.Set(); await reading; f.Cleaner.Hooks.DuringRead = null; }
        f.Cleaner.Execute(plan.Id);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public void SqliteSharedMemoryHardLinksAreRejectedBeforeOpening()
    {
        using var f = new Fixture();
        var outside = Fixture.PutAt(f.Base, "outside-shared-memory.txt", "Unrelated outside content");
        Assert.True(CreateHardLink(f.Data("state_5.sqlite-shm"), outside, IntPtr.Zero));
        var before = Fixture.Checksum(outside);
        var state = f.Cleaner.Snapshot();
        Assert.False(state.Databases["state_5.sqlite"].Supported);
        Assert.Contains("附属文件", state.Databases["state_5.sqlite"].Reason);
        Assert.Contains("结构未验证", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[0]))).Message);
        Assert.Equal(before, Fixture.Checksum(outside));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void TransactionRootAndDataRootCannotContainEachOther()
    {
        using var f = new Fixture();
        Assert.Throws<CleanerException>(() => new Cleaner(f.DataRoot, Path.Combine(f.DataRoot, "tx")));
        Assert.Throws<CleanerException>(() => new Cleaner(f.DataRoot, f.Base));
    }

    [Theory]
    [InlineData("Codex", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1023.6123.0_x64__2p2nqsd0c76g0\app\Codex.exe", true)]
    [InlineData("node", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1023.6123.0_x64__2p2nqsd0c76g0\app\resources\cua_node\bin\node.exe", true)]
    [InlineData("extension-host", @"C:\Users\Example\.codex\plugins\cache\openai-bundled\chrome\latest\extension-host\windows\x64\extension-host.exe", false)]
    [InlineData("codex", @"C:\Users\Example\.codex\plugins\.plugin-appserver\codex.exe", true)]
    [InlineData("codex-command-runner-0.165.0-alpha.1.2", @"C:\Users\Example\.codex\.sandbox-bin\codex-command-runner-0.165.0-alpha.1.2.exe", true)]
    [InlineData("codex", null, true)]
    [InlineData("codex", @"C:\Users\Example\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.exe", true)]
    [InlineData("codex-windows-sandbox-service", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1023.6123.0_x64__2p2nqsd0c76g0\app\resources\codex-windows-sandbox-service.exe", false)]
    [InlineData("node", @"C:\Program Files\nodejs\node.exe", false)]
    [InlineData("CodexCleaner", @"C:\Users\Example\Desktop\CodexCleaner.exe", false)]
    [InlineData("explorer", null, false)]
    public void CodexProcessDetectionCoversStorePackageAndDataDirectory(string name, string? path, bool expected) =>
        Assert.Equal(expected, CodexProcesses.IsCodex(name, path, @"C:\Users\Example\.codex"));

    [Fact]
    public void QuotedAndEscapedTomlTargetsStillProtectTheirChat()
    {
        using var f = new Fixture();
        const string rel = "automations/quoted-target/automation.toml";
        var escapedStart = $"\\u{(int)Ids[0][0]:x4}{Ids[0][1..]}";
        string[] variants =
        [
            $"\"target_thread_id\" = \"{Ids[0]}\"",
            $"'targetThreadId' = '{Ids[0]}'",
            $"\"target\\u005fthread\\U0000005fid\" = \"{escapedStart}\"",
            $"\uFEFFtarget_thread_id = \"{Ids[0]}\"",
        ];
        foreach (var target in variants)
        {
            var file = f.Put(rel, target + "\nname = \"Synthetic automation\"\n");
            var before = Fixture.Checksum(file);
            Assert.Contains("自动化", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[0]))).Message);
            Assert.Equal(before, Fixture.Checksum(file));
        }
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(2, ThreadCount(f));
        f.AssertNoTransactions();
    }

    [Fact]
    public void TomlEvenBackslashRunsCloseMultilinePrompts()
    {
        using var f = new Fixture();
        const string rel = "automations/backslash-prompt/automation.toml";
        var content = "";
        foreach (var count in new[] { 2, 4 })
        {
            content = $"name = \"Synthetic automation\"\nprompt = \"\"\"Work in C:{new string('\\', count)}\"\"\"\n";
            f.Put(rel, content);
            Assert.NotEmpty(f.Cleaner.PlanCleanup(Pick(Ids[0])).Id);
        }
        Execute(f, Pick(Ids[0]));
        Assert.Equal(content, File.ReadAllText(f.Data(rel)));
        f.AssertNoTransactions();
    }

    [Fact]
    public void TomlOddBackslashEscapesKeepFakeTargetsInsidePrompts()
    {
        using var f = new Fixture();
        const string rel = "automations/backslash-target/automation.toml";
        var content = "";
        foreach (var count in new[] { 1, 3 })
        {
            content = $"name = \"Synthetic automation\"\nprompt = \"\"\"\nAn escaped quote: {new string('\\', count)}\"\"\"\n"
                + $"target_thread_id = \"{Ids[0]}\"\nWork in C:\\\\\"\"\"\n"
                + $"target_thread_id = \"{Ids[1]}\"\n";
            f.Put(rel, content);
            Assert.NotEmpty(f.Cleaner.PlanCleanup(Pick(Ids[0])).Id);
            Assert.Contains("自动化", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Pick(Ids[1]))).Message);
        }
        Execute(f, Pick(Ids[0]));
        Assert.Equal(content, File.ReadAllText(f.Data(rel)));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void UnfinishedLockInspectionReportsItsPathAndPreservesFaultFiles()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        var lockFile = Fixture.PutAt(f.TrashRoot, ".mutation.lock", "Synthetic previous mutation lock");
        var reclaim = Fixture.PutAt(f.TrashRoot, ".mutation-reclaim.lock", "Synthetic interrupted lock inspection");
        var message = Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message;
        Assert.Contains("未完成的写入锁检查", message);
        Assert.Contains(reclaim, message);
        Assert.Equal("Synthetic previous mutation lock", File.ReadAllText(lockFile));
        Assert.Equal("Synthetic interrupted lock inspection", File.ReadAllText(reclaim));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void LockInspectionPermissionFailureKeepsItsDiagnostic()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        Fixture.PutAt(f.TrashRoot, ".mutation.lock", "Synthetic previous mutation lock");
        f.Cleaner.Hooks.BeforeReclaimOpen = () => throw new UnauthorizedAccessException("Synthetic reclaim access denied");
        Assert.Equal("Synthetic reclaim access denied", Assert.Throws<UnauthorizedAccessException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.Equal(2, ThreadCount(f));
    }

    [Fact]
    public void StaleLockFromAFinishedProcessIsReclaimed()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(Pick(Ids[0]));
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        exited.WaitForExit();
        Fixture.PutAt(f.TrashRoot, ".mutation.lock", new JsonObject { ["pid"] = exited.Id, ["createdAt"] = "2026-01-01T00:00:00Z", ["dataRoot"] = f.DataRoot }.ToJsonString());
        f.Cleaner.Execute(plan.Id);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void FixtureSchemaMatchesTheSupportedFingerprints()
    {
        using var f = new Fixture();
        var state = f.Cleaner.Snapshot();
        Assert.All(state.Databases, d => Assert.True(d.Value.Supported, $"{d.Key}: {d.Value.Reason}"));
        Assert.True(state.Capabilities.DeleteChats);
        Assert.Empty(state.Warnings);
    }
}
