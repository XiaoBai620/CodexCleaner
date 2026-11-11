using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexCleaner;

internal interface IUpdateChecker
{
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);
}

internal sealed record UpdatePackage(Uri ExecutableUri, Uri ChecksumUri, long Size, string? Sha256 = null);
internal sealed record AvailableUpdate(Version Version, Uri ReleaseUri, string Notes = "", UpdatePackage? Package = null);

internal sealed class UpdateChecker : IUpdateChecker
{
    private const string RepositoryUrl = "https://github.com/XiaoBai620/CodexCleaner";
    private const string LatestReleaseUrl = "https://api.github.com/repos/XiaoBai620/CodexCleaner/releases/latest";
    private const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumNotesCharacters = 16 * 1024;
    private const long MaximumExecutableBytes = 64 * 1024 * 1024;
    private const long MaximumChecksumBytes = 4 * 1024;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
    });
    private readonly HttpClient client;
    private readonly Version currentVersion;
    private readonly TimeSpan timeout;

    internal static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version!);

    public UpdateChecker() : this(SharedClient, CurrentVersion) { }

    internal UpdateChecker(HttpClient client, Version currentVersion, TimeSpan? timeout = null)
    {
        this.client = client;
        this.currentVersion = Normalize(currentVersion);
        this.timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd($"CodexCleaner/{currentVersion.ToString(3)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("更新信息过大，请稍后重试。");

        // The deadline also covers a slow response body, including chunked responses.
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (body.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("更新信息过大，请稍后重试。");
            body.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
        var release = json.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || !IsBoolean(draft) ||
            !release.TryGetProperty("prerelease", out var prerelease) || !IsBoolean(prerelease))
            throw new InvalidDataException("无法识别 GitHub 返回的版本信息。");
        if (draft.GetBoolean() || prerelease.GetBoolean()) return null;
        if (!release.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("无法识别 GitHub 返回的版本号。");
        var tag = tagValue.GetString()!;
        if (!TryParseStableVersion(tag, out var version))
            throw new InvalidDataException("无法识别 GitHub 返回的正式版本号。");
        if (version <= currentVersion) return null;

        // Never open a URL supplied by release metadata: only this repository's tag page.
        return new AvailableUpdate(version, new Uri($"{RepositoryUrl}/releases/tag/{tag}"),
            ReadNotes(release), ReadPackage(release, tag, version));
    }

    private static string ReadNotes(JsonElement release)
    {
        if (!release.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String) return "";
        var text = body.GetString()!.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new StringBuilder(Math.Min(text.Length, MaximumNotesCharacters));
        foreach (var character in text)
        {
            if ((char.IsControl(character) && character is not '\n' and not '\t') ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;
            if (result.Length == MaximumNotesCharacters) break;
            result.Append(character);
        }
        // This text is displayed literally; it is never interpreted as HTML or used for navigation.
        return result.ToString().Trim();
    }

    private static UpdatePackage? ReadPackage(JsonElement release, string tag, Version version)
    {
        // The downloader derives the same canonical tag from Version, so noncanonical tags stay manual.
        if (tag != $"v{version.ToString(3)}" ||
            !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        JsonElement? executable = null, checksum = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            if (name.GetString() == "CodexCleaner.exe")
            {
                if (executable is not null) return null;
                executable = asset;
            }
            else if (name.GetString() == "SHA256SUMS.txt")
            {
                if (checksum is not null) return null;
                checksum = asset;
            }
        }
        if (executable is null || checksum is null) return null;
        var executableUri = new Uri($"{RepositoryUrl}/releases/download/{tag}/CodexCleaner.exe");
        var checksumUri = new Uri($"{RepositoryUrl}/releases/download/{tag}/SHA256SUMS.txt");
        if (!ReadAsset(executable.Value, executableUri, MaximumExecutableBytes, out var size, out var digest) ||
            !ReadAsset(checksum.Value, checksumUri, MaximumChecksumBytes, out _, out _)) return null;
        return new UpdatePackage(executableUri, checksumUri, size, digest);
    }

    private static bool ReadAsset(JsonElement asset, Uri expectedUri, long maximumBytes, out long size, out string? digest)
    {
        size = 0;
        digest = null;
        if (!asset.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded" ||
            !asset.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out size) ||
            size <= 0 || size > maximumBytes ||
            !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            !string.Equals(uri.AbsoluteUri, expectedUri.AbsoluteUri, StringComparison.Ordinal)) return false;
        if (!asset.TryGetProperty("digest", out var hash) || hash.ValueKind == JsonValueKind.Null) return true;
        if (hash.ValueKind != JsonValueKind.String) return false;
        var value = hash.GetString()!;
        if (!value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        value = value[7..];
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character))) return false;
        digest = value.ToLowerInvariant();
        return true;
    }

    private static bool TryParseStableVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (tag.Length is < 6 or > 64 || tag[0] != 'v') return false;
        var parts = tag[1..].Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9')))
            return false;
        if (!Version.TryParse(tag[1..], out var parsed)) return false;
        version = parsed;
        return true;
    }

    private static bool IsBoolean(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}
