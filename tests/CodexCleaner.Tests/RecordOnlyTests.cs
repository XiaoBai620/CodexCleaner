using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class RecordOnlyTests
{
    private static readonly string[] Ids = Fixture.Ids;

    private static Selection Records(params string[] ids) => new(ids) { AllowRecordOnly = true };

    private static void SetPath(Fixture f, string path)
    {
        using var db = f.Open("state_5.sqlite");
        Fixture.Exec(db, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", path, Ids[0]);
    }

    private static void AssertSelectedRecordsGone(Fixture f)
    {
        foreach (var (database, table, key) in new[]
        {
            ("state_5.sqlite", "threads", "id"),
            ("state_5.sqlite", "thread_attachments", "thread_id"),
            ("state_5.sqlite", "thread_read_receipts", "thread_id"),
            ("thread_history_1.sqlite", "thread_items", "thread_id"),
            ("thread_history_1.sqlite", "thread_turns", "thread_id"),
            ("memories_1.sqlite", "stage1_outputs", "thread_id"),
            ("goals_1.sqlite", "thread_goals", "thread_id"),
            ("queue_1.sqlite", "queued_items", "thread_id"),
            ("sqlite/codex-dev.db", "local_thread_catalog", "thread_id"),
        })
            Assert.DoesNotContain(f.Rows(database, table), row => row[key] as string == Ids[0]);
        Assert.DoesNotContain(Ids[0], File.ReadAllText(f.Data("session_index.jsonl")));
        Assert.DoesNotContain(Ids[0], File.ReadAllText(f.Data("history.jsonl")));
        var atoms = JsonNode.Parse(File.ReadAllText(f.Data(".codex-global-state.json")))!["electron-persisted-atom-state"]!;
        Assert.False(atoms["composer-prompt-drafts-v2"]!.AsObject().ContainsKey(Ids[0]));
        Assert.False(atoms["prompt-history"]!.AsObject().ContainsKey(Ids[0]));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("legacy-relative")]
    [InlineData("empty")]
    public void UnsupportedPathsNeedExplicitConsentAndPreserveTheirFiles(string kind)
    {
        using var f = new Fixture();
        var preservedFile = kind switch
        {
            "outside" => Fixture.PutAt(f.Base, "external/rollout.jsonl", "External content must survive"),
            "legacy-relative" => f.Put("legacy/rollout.jsonl", "Legacy content must survive"),
            _ => f.RolloutPaths[0],
        };
        var storedPath = kind switch
        {
            "outside" => preservedFile,
            "legacy-relative" => "legacy/rollout.jsonl",
            _ => "",
        };
        SetPath(f, storedPath);
        var preservedHash = Fixture.Checksum(preservedFile);
        var otherHash = Fixture.Checksum(f.RolloutPaths[1]);
        var originalHash = Fixture.Checksum(f.RolloutPaths[0]);
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.False(chat.CanDelete);
        Assert.True(chat.CanRemoveRecord);
        Assert.Null(chat.RelativePath);
        Assert.Equal(storedPath, chat.OriginalPath);
        Assert.False(string.IsNullOrWhiteSpace(chat.BlockedReason));
        Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(new Selection([Ids[0]])));

        // An exclusive lock proves record-only cleanup does not read the retained file's contents.
        using (var locked = new FileStream(preservedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var plan = f.Cleaner.PlanCleanup(Records(Ids[0]));
            Assert.True(plan.Selection.AllowRecordOnly);
            Assert.True(Assert.Single(plan.Chats).CanRemoveRecord);
            Assert.DoesNotContain(plan.Files, file => file.Remove);
            Assert.Contains(plan.Warnings, warning =>
                warning.Contains("仅清理本地数据库记录", StringComparison.Ordinal) &&
                warning.Contains("原会话文件路径未验证", StringComparison.Ordinal) &&
                warning.Contains("不会据此查找或删除文件", StringComparison.Ordinal));
            f.Cleaner.Execute(plan.Id);
        }

        AssertSelectedRecordsGone(f);
        Assert.Equal(Ids[1], Assert.Single(f.Rows("state_5.sqlite", "threads"))["id"]);
        Assert.Equal(preservedHash, Fixture.Checksum(preservedFile));
        Assert.Equal(originalHash, Fixture.Checksum(f.RolloutPaths[0]));
        Assert.Equal(otherHash, Fixture.Checksum(f.RolloutPaths[1]));
        Assert.True(File.Exists(f.Data("memories/MEMORY.md")));
        f.AssertNoTransactions();
    }

    [Fact]
    public void WindowsSessionPathCasingDoesNotPreventNormalDeletion()
    {
        using var f = new Fixture();
        SetPath(f, f.RolloutPaths[0].Replace("sessions", "Sessions", StringComparison.Ordinal)
            .Replace(".jsonl", ".JSONL", StringComparison.Ordinal));
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.True(chat.CanDelete);
        Assert.False(chat.CanRemoveRecord);
        f.Cleaner.Execute(f.Cleaner.PlanCleanup(new Selection([Ids[0]])).Id);
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        AssertSelectedRecordsGone(f);
        f.AssertNoTransactions();
    }

    [Fact]
    public void MixedSelectionDeletesVerifiedRolloutAndPreservesUnsupportedRollout()
    {
        using var f = new Fixture();
        var outside = Fixture.PutAt(f.Base, "external-rollout.jsonl", "Retained external content");
        SetPath(f, outside);
        var plan = f.Cleaner.PlanCleanup(Records(Ids));
        Assert.Single(plan.Chats, chat => chat.CanRemoveRecord);
        var removed = Assert.Single(plan.Files, file => file.Remove);
        Assert.EndsWith($"rollout-{Ids[1]}.jsonl", removed.Path);
        f.Cleaner.Execute(plan.Id);
        Assert.Empty(f.Rows("state_5.sqlite", "threads"));
        Assert.True(File.Exists(f.RolloutPaths[0]));
        Assert.False(File.Exists(f.RolloutPaths[1]));
        Assert.Equal("Retained external content", File.ReadAllText(outside));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RecordOnlyConsentDoesNotBypassAutomationProtection()
    {
        using var f = new Fixture();
        SetPath(f, "legacy/rollout.jsonl");
        using (var db = f.Open("sqlite/codex-dev.db"))
            Fixture.Insert(db, "automations", new()
            {
                ["id"] = "protected-job", ["name"] = "Protected synthetic job",
                ["target_thread_id"] = Ids[0], ["status"] = "PAUSED", ["kind"] = "heartbeat",
            });
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.False(chat.CanDelete);
        Assert.False(chat.CanRemoveRecord);
        Assert.Contains("自动化", chat.BlockedReason);
        Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Records(Ids[0])));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        Assert.Single(f.Rows("sqlite/codex-dev.db", "automations"));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RecordOnlyConsentDoesNotBypassUnsupportedDatabaseSchema()
    {
        using var f = new Fixture();
        SetPath(f, "legacy/rollout.jsonl");
        using (var db = f.Open("logs_2.sqlite"))
            Fixture.Exec(db, "CREATE TABLE future_logs (thread_id TEXT, body TEXT)");
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.False(chat.CanRemoveRecord);
        Assert.False(chat.CanDelete);
        Assert.Contains("数据库", chat.BlockedReason);
        Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Records(Ids[0])));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        f.AssertNoTransactions();
    }

    [Fact]
    public void PathChangingAfterRecordOnlyPreviewInvalidatesThePlan()
    {
        using var f = new Fixture();
        SetPath(f, "legacy/rollout.jsonl");
        var plan = f.Cleaner.PlanCleanup(Records(Ids[0]));
        SetPath(f, f.RolloutPaths[0]);
        Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        Assert.True(File.Exists(f.RolloutPaths[0]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void RecordOnlyCleanupRetainsSameIdOnRemoteHosts()
    {
        using var f = new Fixture();
        SetPath(f, "legacy/rollout.jsonl");
        using (var db = f.Open("sqlite/codex-dev.db"))
        {
            Fixture.Insert(db, "local_thread_catalog_hosts", new() { ["host_id"] = "remote-test", ["host_kind"] = "ssh" });
            var copy = Fixture.Query(db, "SELECT * FROM local_thread_catalog WHERE thread_id=$p0", Ids[0]).Single();
            copy["host_id"] = "remote-test";
            Fixture.Insert(db, "local_thread_catalog", copy);
        }
        f.Cleaner.Execute(f.Cleaner.PlanCleanup(Records(Ids[0])).Id);
        var retained = Assert.Single(f.Rows("sqlite/codex-dev.db", "local_thread_catalog"), row => row["thread_id"] as string == Ids[0]);
        Assert.Equal("remote-test", retained["host_id"]);
        Assert.Equal(Ids[1], Assert.Single(f.Rows("state_5.sqlite", "threads"))["id"]);
        f.AssertNoTransactions();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public void RecordOnlyConsentDoesNotBypassHardLinkedVerifiedPaths()
    {
        using var f = new Fixture();
        var alias = Path.Combine(f.Base, "preserved-hardlink.jsonl");
        Assert.True(CreateHardLink(alias, f.RolloutPaths[0], IntPtr.Zero));
        var before = Fixture.Checksum(alias);
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.False(chat.CanDelete);
        Assert.False(chat.CanRemoveRecord);
        Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Records(Ids[0])));
        Assert.Equal(before, Fixture.Checksum(alias));
        Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
        f.AssertNoTransactions();
    }

    [Fact]
    public void RecordOnlyConsentDoesNotBypassJunctionsInVerifiedPaths()
    {
        using var f = new Fixture();
        var outside = Path.Combine(f.Base, "external");
        var externalFile = Fixture.PutAt(outside, "rollout.jsonl", "External file behind a junction");
        var link = f.Data("sessions/junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            CreateNoWindow = true, UseShellExecute = false,
        })!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            SetPath(f, "sessions/junction/rollout.jsonl");
            var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
            Assert.False(chat.CanDelete);
            Assert.False(chat.CanRemoveRecord);
            Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(Records(Ids[0])));
            Assert.Equal("External file behind a junction", File.ReadAllText(externalFile));
            Assert.Equal(2, f.Rows("state_5.sqlite", "threads").Count);
            f.AssertNoTransactions();
        }
        finally { Directory.Delete(link); }
    }
}
