using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexCleaner.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v3.2.1", "3.2.0", true)]
    [InlineData("v3.10.0", "3.9.0", true)]
    [InlineData("v4.0.0", "3.99.99", true)]
    [InlineData("v3.2.0", "3.2.0", false)]
    [InlineData("v3.2.0", "3.2.0.0", false)]
    [InlineData("v3.1.0", "3.2.0", false)]
    [InlineData("v3.9.0", "3.10.0", false)]
    public async Task OnlyNewerNumericVersionsOfferAnUpdate(string tag, string current, bool newer)
    {
        using var client = Client(Release(tag));
        var result = await new UpdateChecker(client, Version.Parse(current)).CheckAsync(CancellationToken.None);
        Assert.Equal(newer, result is not null);
        if (newer)
        {
            Assert.Equal(Version.Parse(tag[1..]), result!.Version);
            Assert.Equal($"https://github.com/XiaoBai620/CodexCleaner/releases/tag/{tag}", result.ReleaseUri.AbsoluteUri);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DraftsAndPrereleasesAreIgnored(bool draft, bool prerelease)
    {
        using var client = Client(JsonSerializer.Serialize(new { tag_name = "v9.0.0-beta.1", draft, prerelease }));
        Assert.Null(await Checker(client).CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("v4.0.0-beta.1")]
    [InlineData("v4.0.0+build")]
    [InlineData("v4.0")]
    [InlineData("v4.0.0.0")]
    [InlineData("4.0.0")]
    [InlineData("v4.0.0/../../evil")]
    [InlineData("v2147483648.0.0")]
    [InlineData("v4.0. 0")]
    public async Task InvalidStableTagsAreUnavailableRatherThanTreatedAsCurrent(string tag)
    {
        using var client = Client(Release(tag));
        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(client).CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"v4.0.0\",\"draft\":\"false\",\"prerelease\":false}")]
    [InlineData("{\"tag_name\":4,\"draft\":false,\"prerelease\":false}")]
    public async Task UnexpectedJsonShapesAreUnavailable(string json)
    {
        using var client = Client(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(client).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJsonIsUnavailable()
    {
        using var client = Client("not json");
        await Assert.ThrowsAnyAsync<JsonException>(() => Checker(client).CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task ServerFailuresAreUnavailable(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Checker(client).CheckAsync(CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task MissingReleasesDoNotOfferAnUpdate()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await Checker(client).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestContainsOnlyPublicReleaseLookupAndProductHeaders()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/repos/XiaoBai620/CodexCleaner/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("CodexCleaner/3.2.0", request.Headers.UserAgent.ToString());
            Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Equal(3, request.Headers.Count());
            return Task.FromResult(Response(Release("v4.0.0", "file:///C:/unexpected.exe")));
        }));
        var update = await Checker(client).CheckAsync(CancellationToken.None);
        Assert.Equal("https://github.com/XiaoBai620/CodexCleaner/releases/tag/v4.0.0", update!.ReleaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task NetworkFailureIsPropagatedForTheUiToHandle()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Synthetic offline network")));
        await Assert.ThrowsAsync<HttpRequestException>(() => Checker(client).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsAnOutstandingRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Release("v4.0.0"));
        }));
        using var cancellation = new CancellationTokenSource();
        var check = Checker(client).CheckAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task DeadlineStopsAnUnresponsiveServer()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Release("v4.0.0"));
        }));
        var checker = new UpdateChecker(client, new Version(3, 2, 0), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeadlineAlsoStopsAResponseBodyThatNeverFinishes()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream()),
        })));
        var checker = new UpdateChecker(client, new Version(3, 2, 0), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedResponsesAreRejectedWithOrWithoutContentLength(bool knownLength)
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            HttpContent content = knownLength
                ? new StringContent(new string(' ', 256 * 1024 + 1))
                : new StreamContent(new NonSeekableStream(new byte[256 * 1024 + 1]));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(client).CheckAsync(CancellationToken.None));
    }

    private static UpdateChecker Checker(HttpClient client) => new(client, new Version(3, 2, 0));
    private static string Release(string tag, string htmlUrl = "https://untrusted.invalid/update") =>
        JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false, html_url = htmlUrl });
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    private static HttpClient Client(string json) => new(new Handler((_, _) => Task.FromResult(Response(json))));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
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

    private sealed class NonSeekableStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;
    }
}
