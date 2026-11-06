using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CodexCleaner;

internal interface IUpdateChecker
{
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);
}

internal sealed record AvailableUpdate(Version Version, Uri ReleaseUri);

internal sealed class UpdateChecker : IUpdateChecker
{
    private const string RepositoryUrl = "https://github.com/XiaoBai620/CodexCleaner";
    private const string LatestReleaseUrl = "https://api.github.com/repos/XiaoBai620/CodexCleaner/releases/latest";
    private const int MaximumResponseBytes = 256 * 1024;
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
        return new AvailableUpdate(version, new Uri($"{RepositoryUrl}/releases/tag/{tag}"));
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
