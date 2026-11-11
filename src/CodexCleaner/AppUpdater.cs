using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using CodexCleaner.Engine;

namespace CodexCleaner;

internal interface IAppUpdater
{
    Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
}

internal sealed record UpdateProgress(long ReceivedBytes, long TotalBytes, string Stage);
internal sealed record PreparedUpdate(string Directory, string ExecutablePath, string Sha256, Version Version);

internal sealed class AppUpdater : IAppUpdater
{
    private const long MaximumExecutableSize = 64 * 1024 * 1024;
    private const int MaximumChecksumSize = 4 * 1024;
    private const string OwnerFile = ".download-owner";
    private const string DirectoryPrefix = "update-";
    private const string ExecutableName = "CodexCleaner.exe";
    private const string RepositoryUrl = "https://github.com/XiaoBai620/CodexCleaner";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly ConcurrentDictionary<string, string> CreatedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OwnedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        OwnerFile, ExecutableName, "helper.exe", "install.json", "ready", "complete", "commit", "cancel",
    };
    private readonly HttpClient client;
    private readonly string stagingRoot;
    private readonly TimeSpan timeout;
    private readonly Action<string, Version> validateExecutable;

    internal static string StagingRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexCleaner", "Updates");

    public AppUpdater() : this(SharedClient, StagingRoot) { }

    internal AppUpdater(HttpClient client, string stagingRoot, TimeSpan? timeout = null, Action<string, Version>? validateExecutable = null)
    {
        this.client = client;
        this.stagingRoot = NormalizeRoot(stagingRoot);
        this.timeout = timeout ?? TimeSpan.FromMinutes(5);
        if (this.timeout <= TimeSpan.Zero || this.timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.validateExecutable = validateExecutable ?? ValidateExecutable;
    }

    public async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var package = ValidatePackage(update);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        string? directory = null;
        try
        {
            progress?.Report(new UpdateProgress(0, package.Size, "正在读取更新校验信息…"));
            var expectedHash = ParseChecksum(await ReadChecksumAsync(package.ChecksumUri, token).ConfigureAwait(false));
            if (package.Sha256 is not null && !string.Equals(package.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件的校验信息不一致，请稍后重试。");

            token.ThrowIfCancellationRequested();
            directory = CreateOwnedDirectory();
            var executable = Path.Combine(directory, ExecutableName);
            var hash = await DownloadExecutableAsync(package, executable, progress, token).ConfigureAwait(false);
            if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("更新文件校验失败，请重新下载。");

            progress?.Report(new UpdateProgress(package.Size, package.Size, "正在验证更新程序…"));
            token.ThrowIfCancellationRequested();
            validateExecutable(executable, update.Version);
            token.ThrowIfCancellationRequested();
            if (!IsOwnedDirectory(directory)) throw new InvalidDataException("更新暂存目录已发生变化。");
            EnsureOrdinaryFile(executable);
            progress?.Report(new UpdateProgress(package.Size, package.Size, "下载完成，校验通过。"));
            return new PreparedUpdate(directory, executable, hash, update.Version);
        }
        catch (Exception failure)
        {
            if (directory is not null)
            {
                try { Discard(new PreparedUpdate(directory, Path.Combine(directory, ExecutableName), "", update.Version)); }
                catch (Exception cleanup)
                {
                    throw new IOException("更新下载未完成，暂存文件未能安全清理。", new AggregateException(failure, cleanup));
                }
            }
            throw;
        }
    }

    private static UpdatePackage ValidatePackage(AvailableUpdate update)
    {
        if (update.Version.Build < 0 || update.Version.Revision > 0 || update.Package is not { } package)
            throw new InvalidDataException("此版本未提供可在程序内安装的更新文件。");
        var downloadBase = $"{RepositoryUrl}/releases/download/v{update.Version.ToString(3)}/";
        if (!CanonicalAsset(package.ExecutableUri, downloadBase + ExecutableName) ||
            !CanonicalAsset(package.ChecksumUri, downloadBase + "SHA256SUMS.txt") ||
            package.Size is <= 0 or > MaximumExecutableSize ||
            package.Sha256 is not null && !IsHash(package.Sha256))
            throw new InvalidDataException("更新文件的信息不完整或来源不正确。");
        return package;
    }

    private static bool CanonicalAsset(Uri uri, string expected) =>
        uri.IsAbsoluteUri && string.Equals(uri.AbsoluteUri, expected, StringComparison.Ordinal);

    private async Task<byte[]> ReadChecksumAsync(Uri uri, CancellationToken token)
    {
        using var response = await OpenAssetAsync(uri, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is <= 0 or > MaximumChecksumSize)
            throw new InvalidDataException("更新校验文件大小不正确。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[MaximumChecksumSize + 1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumChecksumSize) throw new InvalidDataException("更新校验文件过大。");
            output.Write(buffer, 0, read);
        }
        if (response.Content.Headers.ContentLength is { } declared && output.Length != declared)
            throw new InvalidDataException("更新校验文件未完整下载。");
        return output.ToArray();
    }

    private async Task<string> DownloadExecutableAsync(UpdatePackage package, string destination, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var response = await OpenAssetAsync(package.ExecutableUri, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } declared && declared != package.Size)
            throw new InvalidDataException("更新文件的下载大小与发布信息不一致。");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        if (!IsOwnedDirectory(Path.GetDirectoryName(destination)!))
            throw new InvalidDataException("更新暂存目录在下载时发生了变化。");
        EnsureSafeAncestors(destination, includeLeaf: false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        long received = 0;
        var lastReport = Stopwatch.GetTimestamp();
        progress?.Report(new UpdateProgress(0, package.Size, "正在下载更新…"));
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            received += read;
            if (received > package.Size || received > MaximumExecutableSize)
                throw new InvalidDataException("更新文件超过了发布的大小。");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(100))
            {
                progress?.Report(new UpdateProgress(received, package.Size, "正在下载更新…"));
                lastReport = Stopwatch.GetTimestamp();
            }
        }
        if (received != package.Size) throw new InvalidDataException("更新文件未完整下载。");
        await target.FlushAsync(token).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<HttpResponseMessage> OpenAssetAsync(Uri initialUri, CancellationToken token)
    {
        var uri = initialUri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"CodexCleaner/{UpdateChecker.CurrentVersion.ToString(3)}");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                // Injected clients must not silently bypass the redirect checks either.
                if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
                    throw new InvalidDataException("更新下载发生了未经确认的跳转。");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                    HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location)
                        throw new InvalidDataException("更新下载的跳转次数或地址不正确。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!SafeRedirect(next)) throw new InvalidDataException("更新下载跳转到了不受信任的地址。");
                    uri = next;
                    response.Dispose();
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException("更新下载失败，请稍后重试。", null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Count > 0 || response.Content.Headers.ContentRange is not null)
                    throw new InvalidDataException("更新服务器返回了不支持的文件格式。");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static bool SafeRedirect(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string ParseChecksum(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("更新校验文件的编码不正确。", error); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || lines[0].Length != 64 + 2 + ExecutableName.Length ||
            !IsHash(lines[0][..64]) || lines[0][64] != ' ' || lines[0][65] is not (' ' or '*') ||
            lines[0][66..] != ExecutableName)
            throw new InvalidDataException("更新校验文件没有唯一、有效的程序校验值。");
        return lines[0][..64].ToLowerInvariant();
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    internal static void ValidateExecutable(string path, Version expectedVersion)
    {
        EnsureSafeAncestors(path, includeLeaf: false);
        EnsureOrdinaryFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
                (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0 ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0 || headers.CorHeader is not null)
                throw new InvalidDataException("更新文件不是适用于本程序的 Windows 64 位应用。");
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.ProductName != "CodexCleaner" || info.FileMajorPart != expectedVersion.Major ||
                info.FileMinorPart != expectedVersion.Minor || info.FileBuildPart != expectedVersion.Build || info.FilePrivatePart != 0)
                throw new InvalidDataException("更新文件的产品名称或版本与发布信息不一致。");
        }
        catch (BadImageFormatException error) { throw new InvalidDataException("更新文件不是有效的 Windows 应用。", error); }
    }

    private string CreateOwnedDirectory()
    {
        EnsureSafeAncestors(stagingRoot, includeLeaf: true);
        Directory.CreateDirectory(stagingRoot);
        EnsureSafeAncestors(stagingRoot, includeLeaf: true);
        var directory = Path.Combine(stagingRoot, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        if (File.Exists(directory) || Directory.Exists(directory)) throw new IOException("更新暂存目录已存在。");
        Directory.CreateDirectory(directory);
        EnsureSafeAncestors(directory, includeLeaf: true);
        CreatedDirectories[directory] = stagingRoot;
        try
        {
            using var marker = new FileStream(Path.Combine(directory, OwnerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Write(Encoding.UTF8.GetBytes(OwnerText(directory)));
            marker.Flush(flushToDisk: true);
            return directory;
        }
        catch (Exception failure)
        {
            // Only these paths were created here; never recurse after a failed marker write.
            try
            {
                EnsureSafeAncestors(directory, includeLeaf: true);
                var entries = Directory.GetFileSystemEntries(directory);
                foreach (var file in entries)
                {
                    if (Path.GetFileName(file) != OwnerFile) throw new InvalidDataException("更新目录出现未知文件。");
                    EnsureOrdinaryFile(file);
                }
                foreach (var file in entries) File.Delete(file);
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception cleanup) { throw new IOException("更新目录创建失败，暂存材料已保留。", new AggregateException(failure, cleanup)); }
            CreatedDirectories.TryRemove(directory, out _);
            throw;
        }
    }

    internal static bool IsOwnedDirectory(string path)
    {
        try
        {
            var full = SafePaths.Normal(path);
            if (!RecognizedDirectory(full) || !Directory.Exists(full)) return false;
            EnsureSafeAncestors(full, includeLeaf: true);
            var marker = Path.Combine(full, OwnerFile);
            EnsureOrdinaryFile(marker);
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 128) return false;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            return string.Equals(reader.ReadToEnd(), OwnerText(full), StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                     NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static void Discard(PreparedUpdate prepared)
    {
        var directory = SafePaths.Normal(prepared.Directory);
        if (!RecognizedDirectory(directory) || !SafePaths.Same(prepared.ExecutablePath, Path.Combine(directory, ExecutableName)))
            throw new InvalidDataException("拒绝清理来源不明的更新目录。");
        EnsureSafeAncestors(directory, includeLeaf: true);
        if (!Directory.Exists(directory)) { CreatedDirectories.TryRemove(directory, out _); return; }
        if (!IsOwnedDirectory(directory)) throw new InvalidDataException("更新暂存目录的所有权校验失败。");
        var entries = Directory.GetFileSystemEntries(directory);
        foreach (var file in entries)
        {
            if (!OwnedFiles.Contains(Path.GetFileName(file))) throw new InvalidDataException("更新暂存目录中存在未知文件，已保留该目录。");
            EnsureOrdinaryFile(file);
        }
        // Do not recurse into directories or follow links; keep the marker until all other files are gone.
        foreach (var file in entries.Where(file => !Path.GetFileName(file).Equals(OwnerFile, StringComparison.Ordinal)))
        {
            EnsureSafeAncestors(directory, includeLeaf: true);
            EnsureOrdinaryFile(file);
            File.Delete(file);
        }
        EnsureSafeAncestors(directory, includeLeaf: true);
        if (!IsOwnedDirectory(directory)) throw new InvalidDataException("更新暂存目录在清理时发生了变化。");
        if (Directory.GetFileSystemEntries(directory).Any(file => Path.GetFileName(file) != OwnerFile))
            throw new InvalidDataException("更新暂存目录在清理时出现了未知文件，已保留该目录。");
        File.Delete(Path.Combine(directory, OwnerFile));
        Directory.Delete(directory, recursive: false);
        CreatedDirectories.TryRemove(directory, out _);
    }

    private static bool RecognizedDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) || name.Length != DirectoryPrefix.Length + 32 ||
            !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _)) return false;
        var parent = Path.GetDirectoryName(path);
        return parent is not null && (SafePaths.Same(parent, NormalizeRoot(StagingRoot)) ||
            CreatedDirectories.TryGetValue(path, out var registeredRoot) && SafePaths.Same(parent, registeredRoot));
    }

    private static string OwnerText(string directory) => "CodexCleaner update staging v1\n" + Path.GetFileName(directory);

    private static string NormalizeRoot(string path)
    {
        var full = SafePaths.Normal(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            SafePaths.Same(full, (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar)))
            throw new InvalidDataException("更新目录必须位于本机的独立文件夹内。");
        return full;
    }

    private static void EnsureSafeAncestors(string path, bool includeLeaf)
    {
        var current = includeLeaf ? SafePaths.Normal(path) : Path.GetDirectoryName(SafePaths.Normal(path));
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("更新目录包含链接或非目录路径。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsureOrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || FileLinks.Count(path) != 1)
            throw new InvalidDataException("更新文件必须是独立的普通文件。");
    }
}
