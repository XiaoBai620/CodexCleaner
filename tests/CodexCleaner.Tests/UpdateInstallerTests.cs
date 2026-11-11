using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexCleaner.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public void SuccessfulReplacementKeepsVerifiedOldCopyUntilStartupCleanup()
    {
        using var fixture = new InstallFixture();
        var restarted = false;
        UpdateInstaller.Apply(fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, request =>
        {
            Assert.Equal("new executable", File.ReadAllText(request.TargetPath));
            Assert.Equal("old executable", File.ReadAllText(UpdateInstaller.BackupPath(request)));
            restarted = true;
        });

        Assert.True(restarted);
        Assert.Equal("new executable", File.ReadAllText(fixture.Request.TargetPath));
        Assert.Equal("old executable", File.ReadAllText(UpdateInstaller.BackupPath(fixture.Request)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(fixture.Request)));
        Assert.Equal("new executable", File.ReadAllText(fixture.Source));
    }

    [Fact]
    public void RestartFailureRestoresOriginalAndRetainsDownloadForDiagnosis()
    {
        using var fixture = new InstallFixture();
        var error = Assert.Throws<UpdateInstallException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable,
            _ => throw new IOException("Synthetic start failure")));

        Assert.True(error.PreserveFiles);
        Assert.Contains("已恢复原版本", error.Message);
        Assert.Equal("old executable", File.ReadAllText(fixture.Request.TargetPath));
        Assert.False(File.Exists(UpdateInstaller.BackupPath(fixture.Request)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(fixture.Request)));
        Assert.True(File.Exists(fixture.Source));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedSourceOrTargetIsNeverReplaced(bool changeSource)
    {
        using var fixture = new InstallFixture();
        File.WriteAllText(changeSource ? fixture.Source : fixture.Request.TargetPath, "changed externally");
        var restarted = false;

        Assert.Throws<UpdateInstallException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ => restarted = true));

        Assert.False(restarted);
        Assert.Equal(changeSource ? "old executable" : "changed externally", File.ReadAllText(fixture.Request.TargetPath));
        Assert.False(File.Exists(UpdateInstaller.BackupPath(fixture.Request)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(fixture.Request)));
    }

    [Fact]
    public void LockedOriginalFailsBeforeReplacementAndRemovesOnlyOwnSibling()
    {
        using var fixture = new InstallFixture();
        using var locked = new FileStream(fixture.Request.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<UpdateInstallException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ => throw new Xunit.Sdk.XunitException("Must not restart")));

        Assert.Equal("old executable", File.ReadAllText(fixture.Request.TargetPath));
        Assert.False(File.Exists(UpdateInstaller.NewPath(fixture.Request)));
        Assert.False(File.Exists(UpdateInstaller.BackupPath(fixture.Request)));
    }

    [Fact]
    public void ExistingBackupIsPreservedWithoutChangingOriginal()
    {
        using var fixture = new InstallFixture();
        File.WriteAllText(UpdateInstaller.BackupPath(fixture.Request), "unrelated backup");

        Assert.Throws<UpdateInstallException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ => throw new Xunit.Sdk.XunitException("Must not restart")));

        Assert.Equal("unrelated backup", File.ReadAllText(UpdateInstaller.BackupPath(fixture.Request)));
        Assert.Equal("old executable", File.ReadAllText(fixture.Request.TargetPath));
    }

    [Fact]
    public void HardLinkedOriginalCannotBeUpdated()
    {
        using var fixture = new InstallFixture();
        var alias = Path.Combine(fixture.Root, "retained.exe");
        Assert.True(CreateHardLink(alias, fixture.Request.TargetPath, IntPtr.Zero));

        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ => throw new Xunit.Sdk.XunitException("Must not restart")));

        Assert.Equal("old executable", File.ReadAllText(alias));
        Assert.Equal("old executable", File.ReadAllText(fixture.Request.TargetPath));
    }

    [Fact]
    public void UnknownChangedInstalledFilePreventsUnsafeRollback()
    {
        using var fixture = new InstallFixture();
        var error = Assert.Throws<UpdateInstallException>(() => UpdateInstaller.Apply(
            fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, request =>
            {
                File.WriteAllText(request.TargetPath, "unknown external replacement");
                throw new IOException("Synthetic start failure");
            }));

        Assert.True(error.PreserveFiles);
        Assert.Contains("无法确认回退结果", error.Message);
        Assert.Equal("unknown external replacement", File.ReadAllText(fixture.Request.TargetPath));
        Assert.Equal("old executable", File.ReadAllText(UpdateInstaller.BackupPath(fixture.Request)));
    }

    [Fact]
    public async Task ConcurrentInstallerCannotReplaceTargetWhileFirstHoldsLock()
    {
        using var fixture = new InstallFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => UpdateInstaller.Apply(fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<IOException>(() => UpdateInstaller.Apply(
                fixture.Request, fixture.OwnsDirectory, fixture.ValidateExecutable, _ => throw new Xunit.Sdk.XunitException("Must not restart twice")));
        }
        finally { release.Set(); }
        await first;
        Assert.Equal("new executable", File.ReadAllText(fixture.Request.TargetPath));
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-directory")]
    [InlineData("oversized")]
    public void ManifestMustBeBoundedAndMatchItsStageAndNonce(string kind)
    {
        using var fixture = new InstallFixture();
        var path = Path.Combine(fixture.Request.Directory, "install.json");
        var request = kind == "wrong-directory" ? fixture.Request with { Directory = fixture.Root } : fixture.Request;
        File.WriteAllText(path, kind == "oversized" ? new string(' ', 17 * 1024) : JsonSerializer.Serialize(request));

        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ReadRequest(path,
            kind == "wrong-nonce" ? Guid.NewGuid().ToString("N") : fixture.Request.Nonce, fixture.OwnsDirectory));
        Assert.Equal("old executable", File.ReadAllText(fixture.Request.TargetPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class InstallFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexCleaner-installer-test-" + Guid.NewGuid().ToString("N"));
        public UpdateInstallRequest Request { get; }
        public string Source => Path.Combine(Request.Directory, "CodexCleaner.exe");

        public InstallFixture()
        {
            var application = Path.Combine(Root, "application");
            var stage = Path.Combine(Root, "updates", "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(application);
            Directory.CreateDirectory(stage);
            var target = Path.Combine(application, "CodexCleaner.exe");
            File.WriteAllText(target, "old executable");
            File.WriteAllText(Path.Combine(stage, "CodexCleaner.exe"), "new executable");
            Request = new UpdateInstallRequest(stage, target, Hash("old executable"), Hash("new executable"), "3.5.0",
                application, 1, 1, Guid.NewGuid().ToString("N"));
        }

        public bool OwnsDirectory(string directory) => string.Equals(directory, Request.Directory, StringComparison.OrdinalIgnoreCase);
        public void ValidateExecutable(string path, Version expectedVersion)
        {
            Assert.Equal(new Version(3, 5, 0), expectedVersion);
            Assert.Equal("new executable", File.ReadAllText(path));
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
