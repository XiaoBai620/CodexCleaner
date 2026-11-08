using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class BackupSafetyTests
{
    [Fact]
    public void UnknownDatabaseVersionPreventsRestoreBeforeAnyDataChanges()
    {
        using var fixture = new Fixture();
        var backup = Delete(fixture);
        var future = fixture.Data("state_6.sqlite");
        File.Copy(fixture.Data("state_5.sqlite"), future);
        var futureHash = Fixture.Checksum(future);
        var before = DatabaseHashes(fixture);

        Assert.Contains("state_6.sqlite", Assert.Throws<CleanerException>(() => fixture.Cleaner.PreviewRestore(backup, [Fixture.Ids[0]])).Message);
        Assert.Contains("state_6.sqlite", Assert.Throws<CleanerException>(() => fixture.Cleaner.Restore(backup, [Fixture.Ids[0]])).Message);

        Assert.Equal(before, DatabaseHashes(fixture));
        Assert.Equal(futureHash, Fixture.Checksum(future));
        Assert.False(File.Exists(fixture.RolloutPaths[0]));
        fixture.AssertNoTransactions();
    }

    [Theory]
    [InlineData("-shm")]
    [InlineData("-wal")]
    public void DatabaseSidecarHardLinksAreRejectedWithoutChangingTheirOtherName(string suffix)
    {
        using var fixture = new Fixture();
        var backup = Delete(fixture);
        var sidecar = fixture.Data("state_5.sqlite" + suffix);
        File.Delete(sidecar);
        var outside = Fixture.PutAt(fixture.Base, "outside-sidecar.bin", new string('X', 32768));
        var outsideHash = Fixture.Checksum(outside);
        var before = DatabaseHashes(fixture);
        Assert.True(CreateHardLink(sidecar, outside, IntPtr.Zero));
        try
        {
            Assert.Throws<CleanerException>(() => fixture.Cleaner.PreviewRestore(backup, [Fixture.Ids[0]]));
            Assert.Throws<CleanerException>(() => fixture.Cleaner.Restore(backup, [Fixture.Ids[0]]));

            Assert.Equal(outsideHash, Fixture.Checksum(outside));
            Assert.Equal(before, DatabaseHashes(fixture));
            Assert.False(File.Exists(fixture.RolloutPaths[0]));
            fixture.AssertNoTransactions();
        }
        finally { File.Delete(sidecar); }
    }

    [Fact]
    public void NullBackupManifestFieldsAreReportedWithoutChangingData()
    {
        using var fixture = new Fixture();
        var backup = Delete(fixture);
        var manifestPath = Path.Combine(backup, "manifest.json");
        var original = File.ReadAllText(manifestPath);
        var before = DatabaseHashes(fixture);
        var cases = new List<Action<JsonNode>>();
        foreach (var name in new[] { "schemas", "migrations", "chats", "files", "indexes", "desktop", "completed", "rowsFingerprint", "dataRoot" })
            cases.Add(root => root[name] = null);
        cases.Add(root => root["chats"]![0] = null);
        cases.Add(root => root["chats"]![0]!["parents"] = null);
        cases.Add(root => root["files"]![0] = null);
        cases.Add(root => root["files"]![0]!["before"] = null);
        cases.Add(root => root["indexes"]![0] = null);
        cases.Add(root => root["indexes"]![0]!["lines"] = null);
        cases.Add(root => root["indexes"]![0]!["lines"]![0] = null);
        try
        {
            foreach (var change in cases)
            {
                var manifest = JsonNode.Parse(original)!;
                change(manifest);
                File.WriteAllText(manifestPath, manifest.ToJsonString());
                Assert.Throws<CleanerException>(() => fixture.Cleaner.PreviewRestore(backup, [Fixture.Ids[0]]));
                Assert.Throws<CleanerException>(() => fixture.Cleaner.Restore(backup, [Fixture.Ids[0]]));
                Assert.Equal(before, DatabaseHashes(fixture));
                Assert.False(File.Exists(fixture.RolloutPaths[0]));
                fixture.AssertNoTransactions();
            }
        }
        finally { File.WriteAllText(manifestPath, original); }
    }

    [Theory]
    [InlineData("session_index.jsonl")]
    [InlineData("history.jsonl")]
    public void FailedIndexWriteRestoresUnrelatedLinesUnderTheLockAndRecoveryPreservesLaterAppends(string relative)
    {
        using var fixture = new Fixture();
        fixture.Running = true;
        var retained = relative == "session_index.jsonl"
            ? JsonSerializer.Serialize(new { id = Fixture.Ids[1], thread_name = "Unrelated index entry", updated_at = "2026-11-09T00:00:00Z" })
            : JsonSerializer.Serialize(new { session_id = Fixture.Ids[1], ts = 2, text = "Unrelated history entry" });
        File.AppendAllText(fixture.Data(relative), retained + "\n");
        var before = File.ReadAllBytes(fixture.Data(relative));
        var plan = fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]]));
        var reached = false;
        fixture.Cleaner.Hooks.AfterIndexWrite = rel =>
        {
            if (rel != relative) return;
            using var second = new FileStream(fixture.Data(rel), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.Throws<IOException>(() => second.Lock(0, long.MaxValue));
            reached = true;
            throw new IOException("Injected failure after index bytes were written but before truncation");
        };

        Assert.Contains("Injected failure", Assert.Throws<CleanerException>(() => fixture.Cleaner.Execute(plan.Id)).Message);
        Assert.True(reached);
        Assert.Equal(before, File.ReadAllBytes(fixture.Data(relative)));
        var backup = Assert.Single(fixture.Cleaner.Backups());
        Assert.True(backup.NeedsRecovery);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(backup.Path, "indexes", relative + ".before")));
        var later = relative == "session_index.jsonl"
            ? JsonSerializer.Serialize(new { id = Fixture.Ids[1], thread_name = "Later unrelated entry", updated_at = "2026-11-09T01:00:00Z" })
            : JsonSerializer.Serialize(new { session_id = Fixture.Ids[1], ts = 3, text = "Later unrelated history" });
        File.AppendAllText(fixture.Data(relative), later + "\n");
        var expected = File.ReadAllBytes(fixture.Data(relative));
        fixture.Cleaner.Hooks.AfterIndexWrite = null;
        fixture.Running = false;

        fixture.Cleaner.RecoverBackup(backup.Path);

        Assert.Equal(expected, File.ReadAllBytes(fixture.Data(relative)));
        Assert.Equal(2, fixture.Rows("state_5.sqlite", "threads").Count);
        Assert.True(File.Exists(fixture.RolloutPaths[0]));
        Assert.False(Assert.Single(fixture.Cleaner.Backups()).NeedsRecovery);
        fixture.AssertNoTransactions();
    }

    private static string Delete(Fixture fixture) =>
        fixture.Cleaner.Execute(fixture.Cleaner.PlanCleanup(new Selection([Fixture.Ids[0]])).Id).BackupDirectory!;

    private static Dictionary<string, string> DatabaseHashes(Fixture fixture) =>
        Catalog.Schemas.Keys.ToDictionary(relative => relative, relative => Fixture.Checksum(fixture.Data(relative)));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
