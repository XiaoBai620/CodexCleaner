using System.Text.Json;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;
using ZstdSharp;

namespace CodexCleaner.Tests;

// Cleanup beside a running Codex, keyed on thread-writer-locks; synthetic data only.
public sealed class LiveCleanupTests
{
    private static readonly string[] Ids = Fixture.Ids;

    private static string LockPath(Fixture f, string id) => f.Data($"thread-writer-locks/{id}.lock");

    // Holds a writer lock the way Codex does: LockFileEx over the whole file.
    private static FileStream HoldLock(Fixture f, string id)
    {
        Directory.CreateDirectory(f.Data("thread-writer-locks"));
        var stream = new FileStream(LockPath(f, id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Lock(0, long.MaxValue);
        return stream;
    }

    private static int ThreadCount(Fixture f) => f.Rows("state_5.sqlite", "threads").Count;

    [Fact]
    public void ChatHeldByCodexIsMarkedInUseAndCannotBePlanned()
    {
        using var f = new Fixture();
        using var held = HoldLock(f, Ids[0]);
        var chats = f.Cleaner.Snapshot().Chats;
        var busy = chats.Single(c => c.Id == Ids[0]);
        Assert.True(busy.InUse);
        Assert.False(busy.CanDelete);
        Assert.False(chats.Single(c => c.Id == Ids[1]).InUse);
        Assert.Contains("正在 Codex 中使用", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(new Selection([Ids[0]]))).Message);
    }

    [Fact]
    public void StaleLockFileWithoutAHolderIsNotInUse()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Data("thread-writer-locks"));
        File.WriteAllText(LockPath(f, Ids[0]), "");
        Assert.False(f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]).InUse);
    }

    [Fact]
    public void IdleChatIsDeletedWhileCodexRunsAndDesktopStateIsLeftAlone()
    {
        using var f = new Fixture();
        f.Running = true;
        using var other = HoldLock(f, Ids[1]);
        var global = Fixture.Checksum(f.Data(".codex-global-state.json"));
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        Assert.True(plan.Live);
        Assert.DoesNotContain(plan.Files, file => file.Path.StartsWith(".codex-global-state", StringComparison.Ordinal));
        // Codex keeps writing other chats' rows into the shared databases meanwhile.
        using (var db = f.Open("logs_2.sqlite"))
            Fixture.Insert(db, "logs", new() { ["ts"] = 2L, ["ts_nanos"] = 2L, ["level"] = "INFO", ["target"] = "fixture", ["feedback_log_body"] = "Concurrent log", ["thread_id"] = Ids[1] });
        f.Cleaner.Execute(plan.Id);
        Assert.Equal(1, ThreadCount(f));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        Assert.Equal(2, f.Rows("logs_2.sqlite", "logs").Count(r => (string?)r["thread_id"] == Ids[1]));
        Assert.DoesNotContain(Ids[0], File.ReadAllText(f.Data("session_index.jsonl")));
        Assert.DoesNotContain(Ids[0], File.ReadAllText(f.Data("history.jsonl")));
        Assert.Equal(global, Fixture.Checksum(f.Data(".codex-global-state.json")));
        Assert.False(File.Exists(LockPath(f, Ids[0])), "our writer lock is removed after release");
        Assert.True(File.Exists(LockPath(f, Ids[1])));
        f.AssertNoTransactions();
    }

    [Fact]
    public void ChatOpenedByCodexAfterPreviewIsNotDeleted()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        using var opened = HoldLock(f, Ids[0]);
        Assert.Contains("刚刚在 Codex 中被打开", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void ChatUpdatedAfterPreviewIsNotDeleted()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        using (var db = f.Open("state_5.sqlite")) Fixture.Exec(db, "UPDATE threads SET updated_at_ms=1780009999000 WHERE id=$p0", Ids[0]);
        Assert.Contains("预览后有新的变化", Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message);
        Assert.Equal(2, ThreadCount(f));
        Assert.True(File.Exists(f.RolloutPaths[0]));
    }

    [Fact]
    public void LiveFailureRollsBackTheFailingDatabaseAndKeepsTheChatListed()
    {
        using var f = new Fixture();
        f.Running = true;
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        var state = plan.Databases.Count;
        f.Cleaner.Hooks.BeforeCommit = n => { if (n == state) throw new IOException("Injected state commit failure"); };
        var message = Assert.Throws<CleanerException>(() => f.Cleaner.Execute(plan.Id)).Message;
        Assert.Contains("Injected state commit failure", message);
        Assert.Contains("重新预览后可继续清理", message);
        Assert.Contains(f.Rows("state_5.sqlite", "threads"), r => (string)r["id"]! == Ids[0]);
        f.Cleaner.Hooks.BeforeCommit = null;
        f.Cleaner.Execute(f.Cleaner.PlanCleanup(new Selection([Ids[0]])).Id);
        Assert.Equal(1, ThreadCount(f));
    }

    [Fact]
    public void GlobalCleanupNeedsCodexToExit()
    {
        using var f = new Fixture();
        f.Running = true;
        Assert.Contains("退出 Codex", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(new Selection([], ClearMemories: true))).Message);
        Assert.Contains("退出 Codex", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(new Selection([Ids[0]], ClearSharedHistory: true))).Message);
    }

    [Fact]
    public void StoppedCleanupHoldsWriterLocksSoCodexCannotOpenTheChat()
    {
        using var f = new Fixture();
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        var probed = false;
        f.Cleaner.Hooks.BeforeDelete = _ =>
        {
            using var stream = new FileStream(LockPath(f, Ids[0]), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.Throws<IOException>(() => stream.Lock(0, long.MaxValue));
            probed = true;
        };
        f.Cleaner.Execute(plan.Id);
        Assert.True(probed);
        Assert.False(File.Exists(LockPath(f, Ids[0])));
    }

    private static string Meta(string id, string? historyBase = null) =>
        JsonSerializer.Serialize(new { type = "session_meta", payload = historyBase is null ? (object)new { id } : new { id, history_base = new { thread_id = historyBase } } }) + "\n";

    [Fact]
    public void ParentReferencedByAnOutsideForkIsRefusedUnlessTheForkIsSelected()
    {
        using var f = new Fixture();
        File.WriteAllText(f.RolloutPaths[1], Meta(Ids[1], Ids[0]));
        var parent = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.Equal([Ids[1]], parent.ForkChildren);
        Assert.Contains("分叉", Assert.Throws<CleanerException>(() => f.Cleaner.PlanCleanup(new Selection([Ids[0]]))).Message);
        Assert.Equal(2, ThreadCount(f));
        f.Cleaner.Execute(f.Cleaner.PlanCleanup(new Selection(Ids)).Id);
        Assert.Equal(0, ThreadCount(f));
        f.AssertNoTransactions();
    }

    [Fact]
    public void CompressedCopiesAndContinuationSegmentsAreDeletedWithTheChat()
    {
        using var f = new Fixture();
        var compressed = f.Data($"sessions/2026/09/20/rollout-2026-09-20T00-00-00-{Ids[0]}.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(compressed)!);
        using (var compressor = new Compressor()) File.WriteAllBytes(compressed, compressor.Wrap(System.Text.Encoding.UTF8.GetBytes(Meta(Ids[0]))).ToArray());
        var segment = f.Put($"sessions/2026/09/28/rollout-2026-09-28T00-00-00-{Ids[0]}_{Fixture.Stale}.jsonl", Meta(Ids[0], Ids[0]));
        var unrelated = f.Put($"sessions/2026/09/28/rollout-2026-09-28T00-00-01-{Ids[1]}.jsonl.zst", "not zstd");
        var chat = f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]);
        Assert.Equal(3, chat.RolloutFiles.Count);
        Assert.Empty(chat.ForkChildren);
        var plan = f.Cleaner.PlanCleanup(new Selection([Ids[0]]));
        Assert.Equal(3, plan.Files.Count(p => p.Remove));
        f.Cleaner.Execute(plan.Id);
        Assert.False(File.Exists(compressed));
        Assert.False(File.Exists(segment));
        Assert.False(File.Exists(f.RolloutPaths[0]));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(f.RolloutPaths[1]));
        f.AssertNoTransactions();
    }

    [Fact]
    public void ConversationReadsUserAgentAndToolItems()
    {
        using var f = new Fixture();
        using (var db = f.Open("thread_history_1.sqlite"))
        {
            Fixture.Exec(db, "DELETE FROM thread_items");
            var items = new (string Type, JsonObject Json)[]
            {
                ("userMessage", new() { ["type"] = "userMessage", ["id"] = "u", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Invented question" }) }),
                ("reasoning", new() { ["type"] = "reasoning", ["id"] = "r", ["summary"] = new JsonArray("Invented summary"), ["content"] = new JsonArray() }),
                ("commandExecution", new() { ["type"] = "commandExecution", ["id"] = "c1", ["command"] = "echo one", ["aggregatedOutput"] = "one", ["exitCode"] = 0 }),
                ("commandExecution", new() { ["type"] = "commandExecution", ["id"] = "c2", ["command"] = "echo two", ["aggregatedOutput"] = "two", ["exitCode"] = 1 }),
                ("agentMessage", new() { ["type"] = "agentMessage", ["id"] = "a", ["text"] = "**Invented** answer" }),
            };
            for (var i = 0; i < items.Length; i++)
                Fixture.Insert(db, "thread_items", new() { ["thread_id"] = Ids[0], ["turn_id"] = "turn", ["item_id"] = items[i].Json["id"]!.ToString(), ["rollout_ordinal"] = (long)i, ["created_at_ms"] = 1L, ["item_json"] = items[i].Json.ToJsonString(), ["item_type"] = items[i].Type });
        }
        var messages = f.Cleaner.Conversation(Ids[0]);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("Invented question", messages[0].Text);
        Assert.Equal("Invented summary", messages[1].Thinking);
        var tools = messages[2].ToolCalls;
        Assert.Equal(2, tools.Count);
        Assert.Equal("echo two", tools[1].Summary);
        Assert.Contains("退出码 1", tools[1].Result);
        Assert.Equal("**Invented** answer", messages[3].Text);
        Assert.Equal(2, f.Cleaner.Snapshot().Chats.Single(c => c.Id == Ids[0]).Messages);
    }
}
