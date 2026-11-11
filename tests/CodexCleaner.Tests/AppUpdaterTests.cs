using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexCleaner.Tests;

public sealed class AppUpdaterTests
{
    private static readonly Version NewVersion = new(4, 0, 0);
    private static readonly byte[] Executable = Encoding.UTF8.GetBytes("Synthetic downloaded application, never executed.");
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Executable));
    private const string AssetBase = "https://github.com/XiaoBai620/CodexCleaner/releases/download/v4.0.0/";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteDownloadChecksManifestAndOptionalDigestBeforePreparingAnUpdate(bool digest)
    {
        using var fixture = new DownloadFixture();
        var requests = new List<string>();
        using var client = Client((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.StartsWith("CodexCleaner/", request.Headers.UserAgent.ToString());
            return Task.FromResult(IsChecksum(request) ? Manifest() : Bytes(Executable));
        });
        var validationCalls = 0;
        var updater = fixture.Updater(client, validate: (path, version) =>
        {
            validationCalls++;
            Assert.Equal(NewVersion, version);
            Assert.Equal(Executable, File.ReadAllBytes(path));
        });
        var progress = new ProgressRecorder();
        var prepared = await updater.DownloadAsync(Update(digest ? Hash : null), progress, CancellationToken.None);
        Assert.Equal(new[] { AssetBase + "SHA256SUMS.txt", AssetBase + "CodexCleaner.exe" }, requests);
        Assert.Equal(1, validationCalls);
        Assert.Equal(Hash, prepared.Sha256);
        Assert.Equal(NewVersion, prepared.Version);
        Assert.Equal(fixture.Root, Path.GetDirectoryName(prepared.Directory));
        Assert.True(AppUpdater.IsOwnedDirectory(prepared.Directory));
        Assert.Equal(Executable, File.ReadAllBytes(prepared.ExecutablePath));
        Assert.All(progress.Values, value => Assert.Equal(Executable.LongLength, value.TotalBytes));
        Assert.Equal(Executable.LongLength, progress.Values[^1].ReceivedBytes);
        Assert.Contains("校验通过", progress.Values[^1].Stage);
        AppUpdater.Discard(prepared);
        Assert.False(Directory.Exists(prepared.Directory));
        Assert.Empty(fixture.Entries);
    }

    [Theory]
    [InlineData("release-assets.githubusercontent.com")]
    [InlineData("objects.githubusercontent.com")]
    public async Task DownloadsFollowOnlyExplicitTrustedAssetRedirects(string host)
    {
        using var fixture = new DownloadFixture();
        var redirected = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.Host == "github.com")
                return Task.FromResult(Redirect($"https://{host}/synthetic/{(IsChecksum(request) ? "checksum" : "exe")}?signature=synthetic"));
            redirected++;
            Assert.Equal(host, request.RequestUri.Host);
            Assert.Equal("?signature=synthetic", request.RequestUri.Query);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("checksum", StringComparison.Ordinal) ? Manifest() : Bytes(Executable));
        });
        var prepared = await fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None);
        Assert.Equal(2, redirected);
        AppUpdater.Discard(prepared);
    }

    [Theory]
    [InlineData("https://untrusted.invalid/file")]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com:444/file")]
    [InlineData("https://unexpected@release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com/file#fragment")]
    [InlineData("https://release-assets.githubusercontent.com.evil.invalid/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://github.com/Other/Repository/releases/download/v4.0.0/CodexCleaner.exe")]
    [InlineData("file:///C:/unexpected.exe")]
    public async Task UnsafeRedirectIsRejectedWithoutRequestingItsDestination(string location)
    {
        using var fixture = new DownloadFixture();
        var requests = 0;
        using var client = Client((_, _) => { requests++; return Task.FromResult(Redirect(location)); });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Equal(1, requests);
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public async Task RepeatedRedirectsHaveABoundedRequestCount()
    {
        using var fixture = new DownloadFixture();
        var requests = 0;
        using var client = Client((_, _) =>
        {
            requests++;
            return Task.FromResult(Redirect($"https://release-assets.githubusercontent.com/file/{requests}"));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Equal(6, requests);
    }

    [Fact]
    public async Task SilentRedirectsByAnInjectedClientCannotBypassTheAddressChecks()
    {
        using var fixture = new DownloadFixture();
        using var client = Client((_, _) =>
        {
            var response = Manifest();
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.invalid/file");
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PartialAndFailedHttpResponsesAreRejected(HttpStatusCode status)
    {
        using var fixture = new DownloadFixture();
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-name")]
    [InlineData("path")]
    [InlineData("bad-hash")]
    [InlineData("bad-utf8")]
    [InlineData("empty")]
    public async Task AmbiguousOrMalformedManifestsNeverStartTheExecutableDownload(string fault)
    {
        using var fixture = new DownloadFixture();
        var valid = Hash + "  CodexCleaner.exe\n";
        var bytes = fault switch
        {
            "duplicate" => Encoding.UTF8.GetBytes(valid + valid),
            "wrong-name" => Encoding.UTF8.GetBytes(Hash + "  Other.exe\n"),
            "path" => Encoding.UTF8.GetBytes(Hash + "  ../CodexCleaner.exe\n"),
            "bad-hash" => Encoding.UTF8.GetBytes(new string('z', 64) + "  CodexCleaner.exe\n"),
            "bad-utf8" => [0xc3, 0x28],
            "empty" => [],
            _ => throw new InvalidOperationException(fault),
        };
        using var client = Client((request, _) => { Assert.True(IsChecksum(request)); return Task.FromResult(Bytes(bytes)); });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManifestSizeLimitAlsoAppliesWhenTheResponseLengthIsUnknown(bool knownLength)
    {
        using var fixture = new DownloadFixture();
        using var client = Client((_, _) => Task.FromResult(Bytes(new byte[4097], knownLength)));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public async Task ConflictingGithubAndManifestHashesStopBeforeDownloadingTheExecutable()
    {
        using var fixture = new DownloadFixture();
        using var client = Client((request, _) => { Assert.True(IsChecksum(request)); return Task.FromResult(Manifest()); });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(new string('0', 64)), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Theory]
    [InlineData("https://untrusted.invalid/CodexCleaner.exe", 20, null)]
    [InlineData(AssetBase + "CodexCleaner.exe?unexpected=1", 20, null)]
    [InlineData(AssetBase + "CodexCleaner.exe", 0, null)]
    [InlineData(AssetBase + "CodexCleaner.exe", 64 * 1024 * 1024 + 1, null)]
    [InlineData(AssetBase + "CodexCleaner.exe", 20, "sha256:bad")]
    public async Task InvalidMetadataCannotChooseAnArbitraryDownload(string executableUrl, long size, string? digest)
    {
        using var fixture = new DownloadFixture();
        using var client = Client((_, _) => throw new InvalidOperationException("No network access expected."));
        var update = Update() with { Package = new UpdatePackage(new Uri(executableUrl), new Uri(AssetBase + "SHA256SUMS.txt"), size, digest) };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(update, null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Theory]
    [InlineData("checksum-url")]
    [InlineData("missing-package")]
    [InlineData("nonstable-version")]
    public async Task UnsupportedPackageMetadataFailsBeforeAnyRequest(string fault)
    {
        using var fixture = new DownloadFixture();
        using var client = Client((_, _) => throw new InvalidOperationException("No network access expected."));
        var update = fault switch
        {
            "checksum-url" => Update() with { Package = Update().Package! with { ChecksumUri = new Uri("https://untrusted.invalid/SHA256SUMS.txt") } },
            "missing-package" => Update() with { Package = null },
            "nonstable-version" => Update() with { Version = new Version(4, 0, 0, 1) },
            _ => throw new InvalidOperationException(fault),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(update, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    public async Task ExecutableLengthMismatchRemovesTheOwnedStagingDirectory(int difference, bool knownLength)
    {
        using var fixture = new DownloadFixture();
        var bytes = new byte[Executable.Length + difference];
        using var client = Client((request, _) => Task.FromResult(IsChecksum(request) ? Manifest() : Bytes(bytes, knownLength)));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public async Task SameSizeContentWithTheWrongHashIsRemoved()
    {
        using var fixture = new DownloadFixture();
        using var client = Client((request, _) => Task.FromResult(IsChecksum(request) ? Manifest() : Bytes(new byte[Executable.Length])));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public async Task FailedBinaryValidationRemovesTheDownloadedProgram()
    {
        using var fixture = new DownloadFixture();
        using var client = GoodClient();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client, validate: (_, _) => throw new InvalidDataException("Synthetic invalid product."))
            .DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public async Task CancellationDuringTheExecutableRequestRemovesItsStagingDirectory()
    {
        using var fixture = new DownloadFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (request, token) =>
        {
            if (IsChecksum(request)) return Manifest();
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Bytes(Executable);
        });
        using var cancellation = new CancellationTokenSource();
        var download = fixture.Updater(client).DownloadAsync(Update(), null, cancellation.Token);
        await started.Task;
        Assert.Single(fixture.Entries);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(fixture.Entries);
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("checksum-body")]
    [InlineData("executable-body")]
    public async Task DeadlineCoversHeadersAndBothResponseBodies(string slowPart)
    {
        using var fixture = new DownloadFixture();
        using var client = Client(async (request, token) =>
        {
            if (slowPart == "headers") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (slowPart == "checksum-body" || slowPart == "executable-body" && !IsChecksum(request))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) };
            return Manifest();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Updater(client, TimeSpan.FromMilliseconds(100))
            .DownloadAsync(Update(), null, CancellationToken.None));
        Assert.Empty(fixture.Entries);
    }

    [Fact]
    public void BinaryValidatorAcceptsTheBuiltProductAndRejectsTheWrongVersionAndDll()
    {
        using var fixture = new DownloadFixture();
        var source = Path.Combine(AppContext.BaseDirectory, "CodexCleaner.exe");
        Assert.True(File.Exists(source), "The application host should be copied with the project reference.");
        var executable = Path.Combine(fixture.Base, "CodexCleaner.exe");
        File.Copy(source, executable);
        AppUpdater.ValidateExecutable(executable, UpdateChecker.CurrentVersion);
        Assert.Throws<InvalidDataException>(() => AppUpdater.ValidateExecutable(executable, new Version(UpdateChecker.CurrentVersion.Major + 1, 0, 0)));
        var dll = Path.Combine(fixture.Base, "wrong.dll");
        File.Copy(typeof(AppUpdater).Assembly.Location, dll);
        Assert.Throws<InvalidDataException>(() => AppUpdater.ValidateExecutable(dll, UpdateChecker.CurrentVersion));
        var malformed = Path.Combine(fixture.Base, "malformed.exe");
        File.WriteAllBytes(malformed, Executable);
        Assert.Throws<InvalidDataException>(() => AppUpdater.ValidateExecutable(malformed, NewVersion));
    }

    [Fact]
    public async Task CleanupRejectsAnUnknownFileBeforeDeletingAnyOwnedFile()
    {
        using var fixture = new DownloadFixture();
        using var client = GoodClient();
        var prepared = await fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None);
        var unknown = Path.Combine(prepared.Directory, "user-file.txt");
        File.WriteAllText(unknown, "Synthetic unrelated file.");
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(prepared));
        Assert.Equal(Executable, File.ReadAllBytes(prepared.ExecutablePath));
        Assert.True(File.Exists(unknown));
        File.Delete(unknown);
        AppUpdater.Discard(prepared);
    }

    [Fact]
    public async Task CleanupNeverRecursesIntoAnUnexpectedDirectory()
    {
        using var fixture = new DownloadFixture();
        using var client = GoodClient();
        var prepared = await fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None);
        var child = Path.Combine(prepared.Directory, "helper.exe");
        Directory.CreateDirectory(child);
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(prepared));
        Assert.True(File.Exists(prepared.ExecutablePath));
        Assert.True(Directory.Exists(child));
        Directory.Delete(child);
        AppUpdater.Discard(prepared);
    }

    [Fact]
    public async Task OwnershipMarkerMustMatchTheDirectoryAndRemainAnIndependentFile()
    {
        using var fixture = new DownloadFixture();
        using var client = GoodClient();
        var prepared = await fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None);
        var marker = Path.Combine(prepared.Directory, ".download-owner");
        var original = File.ReadAllBytes(marker);
        File.WriteAllText(marker, "A different operation.");
        Assert.False(AppUpdater.IsOwnedDirectory(prepared.Directory));
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(prepared));
        var outside = Path.Combine(fixture.Base, "marker-outside");
        File.WriteAllBytes(outside, original);
        File.Delete(marker);
        Assert.True(CreateHardLink(marker, outside, IntPtr.Zero));
        Assert.False(AppUpdater.IsOwnedDirectory(prepared.Directory));
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(prepared));
        Assert.Equal(original, File.ReadAllBytes(outside));
        File.Delete(marker);
        File.WriteAllBytes(marker, original);
        AppUpdater.Discard(prepared);
    }

    [Fact]
    public async Task HardLinkedExecutableIsRejectedBeforeCleanupCanAffectItsOtherName()
    {
        using var fixture = new DownloadFixture();
        using var client = GoodClient();
        var prepared = await fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None);
        var outside = Path.Combine(fixture.Base, "outside.exe");
        File.WriteAllBytes(outside, Executable);
        File.Delete(prepared.ExecutablePath);
        Assert.True(CreateHardLink(prepared.ExecutablePath, outside, IntPtr.Zero));
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(prepared));
        Assert.Equal(Executable, File.ReadAllBytes(outside));
        File.Delete(prepared.ExecutablePath);
        AppUpdater.Discard(prepared);
    }

    [Fact]
    public async Task StagingRootCannotBeAJunctionToAnotherDirectory()
    {
        using var fixture = new DownloadFixture();
        var outside = Path.Combine(fixture.Base, "outside");
        Directory.CreateDirectory(outside);
        using (var create = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{fixture.Root}\" \"{outside}\"")
               { UseShellExecute = false, CreateNoWindow = true })!)
        {
            create.WaitForExit();
            Assert.Equal(0, create.ExitCode);
        }
        try
        {
            using var client = GoodClient();
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(fixture.Root); }
    }

    [Fact]
    public async Task ReplacedStagingDirectoryIsRejectedBeforeWritingTheExecutable()
    {
        using var fixture = new DownloadFixture();
        var outside = Path.Combine(fixture.Base, "outside");
        var savedStage = Path.Combine(fixture.Base, "saved-stage");
        Directory.CreateDirectory(outside);
        string? stage = null;
        using var client = Client((request, _) =>
        {
            if (IsChecksum(request)) return Task.FromResult(Manifest());
            stage = Assert.Single(fixture.Entries);
            Directory.Move(stage, savedStage);
            using (var create = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{stage}\" \"{outside}\"")
                   { UseShellExecute = false, CreateNoWindow = true })!)
            {
                create.WaitForExit();
                Assert.Equal(0, create.ExitCode);
            }
            return Task.FromResult(Bytes(Executable));
        });
        try
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Updater(client).DownloadAsync(Update(), null, CancellationToken.None));
            Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(File.Exists(Path.Combine(savedStage, ".download-owner")));
        }
        finally
        {
            if (stage is not null)
            {
                Directory.Delete(stage);
                Directory.Move(savedStage, stage);
                AppUpdater.Discard(new PreparedUpdate(stage, Path.Combine(stage, "CodexCleaner.exe"), Hash, NewVersion));
            }
        }
    }

    [Fact]
    public void CleanupRefusesArbitraryUnmarkedDirectories()
    {
        using var fixture = new DownloadFixture();
        var unrelated = Path.Combine(fixture.Base, "unrelated");
        Directory.CreateDirectory(unrelated);
        Assert.False(AppUpdater.IsOwnedDirectory(unrelated));
        Assert.Throws<InvalidDataException>(() => AppUpdater.Discard(new PreparedUpdate(unrelated, Path.Combine(unrelated, "CodexCleaner.exe"), Hash, NewVersion)));
        Assert.True(Directory.Exists(unrelated));
    }

    private static AvailableUpdate Update(string? digest = null) => new(NewVersion,
        new Uri("https://github.com/XiaoBai620/CodexCleaner/releases/tag/v4.0.0"), "Synthetic release notes.",
        new UpdatePackage(new Uri(AssetBase + "CodexCleaner.exe"), new Uri(AssetBase + "SHA256SUMS.txt"), Executable.LongLength, digest));
    private static bool IsChecksum(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal);
    private static HttpResponseMessage Manifest() => Bytes(Encoding.UTF8.GetBytes(Hash + "  CodexCleaner.exe\n"));
    private static HttpResponseMessage Bytes(byte[] bytes, bool knownLength = true) => new(HttpStatusCode.OK)
    {
        Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekableStream(bytes)),
    };
    private static HttpResponseMessage Redirect(string uri) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(uri) } };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpClient GoodClient() => Client((request, _) => Task.FromResult(IsChecksum(request) ? Manifest() : Bytes(Executable)));

    private sealed class DownloadFixture : IDisposable
    {
        internal string Base { get; } = Directory.CreateTempSubdirectory("codex-updater-test-").FullName;
        internal string Root => Path.Combine(Base, "updates");
        internal string[] Entries => Directory.Exists(Root) ? Directory.GetFileSystemEntries(Root) : [];
        internal AppUpdater Updater(HttpClient client, TimeSpan? timeout = null, Action<string, Version>? validate = null) =>
            new(client, Root, timeout, validate ?? ((_, _) => { }));
        public void Dispose()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), Path.GetDirectoryName(Base));
            Assert.StartsWith("codex-updater-test-", Path.GetFileName(Base));
            if (Directory.Exists(Base)) Directory.Delete(Base, recursive: true);
        }
    }

    private sealed class ProgressRecorder : IProgress<UpdateProgress>
    {
        internal List<UpdateProgress> Values { get; } = [];
        public void Report(UpdateProgress value) => Values.Add(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
