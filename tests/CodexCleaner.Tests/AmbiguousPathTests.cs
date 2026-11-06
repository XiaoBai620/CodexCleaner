using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class AmbiguousPathTests
{
    private static void SetPath(Fixture fixture, string path)
    {
        using var database = fixture.Open("state_5.sqlite");
        Fixture.Exec(database, "UPDATE threads SET rollout_path=$p0 WHERE id=$p1", path, Fixture.Ids[0]);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("absolute-stream")]
    [InlineData("extended-stream")]
    [InlineData("win32-device")]
    [InlineData("nt-device")]
    [InlineData("nt-dos")]
    [InlineData("global-root")]
    public void DeviceAndStreamPathsCannotUseRecordOnlyCleanup(string kind)
    {
        using var fixture = new Fixture();
        var history = fixture.Data("history.jsonl");
        var path = kind switch
        {
            "stream" => "history.jsonl::$DATA",
            "absolute-stream" => history + ":retained-stream",
            "extended-stream" => @"\\?\" + history + "::$DATA",
            "win32-device" => @"\\.\" + history,
            "nt-device" => @"\Device\HarddiskVolume1\history.jsonl",
            "nt-dos" => @"\??\" + history,
            _ => @"\\?\GLOBALROOT\Device\HarddiskVolume1\history.jsonl",
        };
        SetPath(fixture, path);
        var before = Fixture.Checksum(history);
        var databaseBefore = Fixture.Checksum(fixture.Data("state_5.sqlite"));

        var chat = fixture.Cleaner.Snapshot().Chats.Single(c => c.Id == Fixture.Ids[0]);

        Assert.False(chat.CanDelete);
        Assert.False(chat.CanRemoveRecord);
        Assert.Null(chat.RelativePath);
        Assert.Contains("设备路径或数据流", chat.BlockedReason);
        Assert.Throws<CleanerException>(() => fixture.Cleaner.PlanCleanup(
            new Selection([Fixture.Ids[0]]) { AllowRecordOnly = true }));
        Assert.Equal(before, Fixture.Checksum(history));
        Assert.Equal(databaseBefore, Fixture.Checksum(fixture.Data("state_5.sqlite")));
        Assert.Equal(2, fixture.Rows("state_5.sqlite", "threads").Count);
        fixture.AssertNoTransactions();
    }

    [Fact]
    public void ExtendedDrivePathStillSupportsVerifiedSessionCleanup()
    {
        using var fixture = new Fixture();
        SetPath(fixture, @"\\?\" + fixture.RolloutPaths[0]);

        var chat = fixture.Cleaner.Snapshot().Chats.Single(c => c.Id == Fixture.Ids[0]);

        Assert.True(chat.CanDelete);
        Assert.False(chat.CanRemoveRecord);
        fixture.Cleaner.Execute(fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]])).Id);
        Assert.False(File.Exists(fixture.RolloutPaths[0]));
        Assert.True(File.Exists(fixture.RolloutPaths[1]));
        fixture.AssertNoTransactions();
    }

    [Theory]
    [InlineData(@"\\synthetic-server\synthetic-share\history.jsonl")]
    [InlineData(@"\\?\UNC\synthetic-server\synthetic-share\history.jsonl")]
    public void OrdinaryUncPathsRemainEligibleForRecordOnlyCleanupWithoutResolvingThem(string path)
    {
        using var fixture = new Fixture();
        SetPath(fixture, path);

        var chat = fixture.Cleaner.Snapshot().Chats.Single(c => c.Id == Fixture.Ids[0]);

        Assert.False(chat.CanDelete);
        Assert.True(chat.CanRemoveRecord);
        Assert.Null(chat.RelativePath);
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]) { AllowRecordOnly = true });
        Assert.DoesNotContain(plan.Files, file => file.Remove);
        fixture.Cleaner.Execute(plan.Id);
        Assert.Equal(Fixture.Ids[1], Assert.Single(fixture.Rows("state_5.sqlite", "threads"))["id"]);
        Assert.True(File.Exists(fixture.RolloutPaths[0]));
        fixture.AssertNoTransactions();
    }
}
