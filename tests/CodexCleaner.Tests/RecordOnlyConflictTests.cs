using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class RecordOnlyConflictTests
{
    private static string SelectedId => Fixture.Ids[0];

    private static void SetPath(Fixture fixture, string path)
    {
        using var database = fixture.Open("state_5.sqlite");
        Fixture.Exec(database, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", path, SelectedId);
    }

    [Theory]
    [InlineData("memories/retained.jsonl")]
    [InlineData("MEMORIES/RETAINED.JSONL")]
    public void GlobalMemoryCleanupCannotDeleteTheRetainedRollout(string storedPath)
    {
        using var fixture = new Fixture();
        var retained = fixture.Put("memories/retained.jsonl", "Retained rollout content");
        SetPath(fixture, storedPath);
        var before = Fixture.Checksum(fixture.Data("state_5.sqlite"));
        var selection = new Selection([SelectedId], ClearMemories: true) { AllowRecordOnly = true };

        var error = Assert.Throws<CleanerException>(() => fixture.Cleaner.PlanCleanup(selection));

        Assert.Contains("范围重合", error.Message);
        Assert.Contains("取消相关附加清理", error.Message);
        Assert.Equal("Retained rollout content", File.ReadAllText(retained));
        Assert.Equal(before, Fixture.Checksum(fixture.Data("state_5.sqlite")));
        Assert.Equal(2, fixture.Rows("state_5.sqlite", "threads").Count);
        fixture.AssertNoTransactions();
    }

    [Theory]
    [InlineData("history.jsonl")]
    [InlineData("session_index.jsonl")]
    [InlineData("state_5.sqlite")]
    [InlineData("STATE_5.SQLITE")]
    [InlineData("state_5.sqlite-wal")]
    [InlineData("state_5.sqlite-shm")]
    [InlineData("state_5.sqlite-journal")]
    public void RecordCleanupCannotModifyItsRetainedSourceThroughAnotherScope(string storedPath)
    {
        using var fixture = new Fixture();
        SetPath(fixture, storedPath);
        string[] tracked = ["history.jsonl", "session_index.jsonl", "state_5.sqlite"];
        var before = tracked.ToDictionary(path => path, path => Fixture.Checksum(fixture.Data(path)));

        var error = Assert.Throws<CleanerException>(() => fixture.Cleaner.PlanCleanup(
            new Selection([SelectedId]) { AllowRecordOnly = true }));

        Assert.Contains("范围重合", error.Message);
        foreach (var path in tracked) Assert.Equal(before[path], Fixture.Checksum(fixture.Data(path)));
        Assert.Equal(2, fixture.Rows("state_5.sqlite", "threads").Count);
        fixture.AssertNoTransactions();
    }

    [Fact]
    public void DisablingGlobalMemoryCleanupAllowsRecordRemovalAndPreservesTheSource()
    {
        using var fixture = new Fixture();
        var retained = fixture.Put("memories/retained.jsonl", "Retained rollout content");
        SetPath(fixture, "memories/retained.jsonl");

        var plan = fixture.Cleaner.PlanCleanup(new Selection([SelectedId]) { AllowRecordOnly = true });
        fixture.Cleaner.Execute(plan.Id);

        Assert.Equal("Retained rollout content", File.ReadAllText(retained));
        Assert.Equal(Fixture.Ids[1], Assert.Single(fixture.Rows("state_5.sqlite", "threads"))["id"]);
        Assert.True(File.Exists(fixture.RolloutPaths[1]));
        fixture.AssertNoTransactions();
    }
}
