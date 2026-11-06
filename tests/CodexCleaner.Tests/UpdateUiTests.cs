using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace CodexCleaner.Tests;

public sealed class UpdateUiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly AvailableUpdate NewRelease = new(new Version(3, 3, 0), new Uri("https://github.com/XiaoBai620/CodexCleaner/releases/tag/v3.3.0"));

    [Fact]
    public void StartupCheckDoesNotBlockLocalUseAndNewReleaseStaysVisible() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var response = new TaskCompletionSource<AvailableUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new StubChecker(_ => response.Task);
        Uri? opened = null;
        using var form = Create(fixture, checker, uri => opened = uri);
        form.Show();
        PumpUntil(() => checker.Calls == 1 && Field<Button>(form, "refresh").Enabled);
        Assert.False(Field<Button>(form, "checkUpdates").Enabled);
        Assert.Null(opened);
        response.SetResult(NewRelease);
        PumpUntil(() => Field<Panel>(form, "updateBanner").Visible);
        Assert.Contains("v3.3.0", Field<Label>(form, "updateNotice").Text);
        Assert.Contains($"v{UpdateChecker.CurrentVersion}", Field<Label>(form, "updateNotice").Text);
        Assert.Null(opened);

        foreach (var size in new[] { new Size(1180, 780), new Size(960, 680) })
        {
            form.Size = size;
            form.PerformLayout();
            Application.DoEvents();
            var banner = Field<Panel>(form, "updateBanner");
            var download = Field<Button>(form, "downloadUpdate");
            Assert.True(banner.Height >= download.Height);
            Assert.True(download.Parent!.ClientRectangle.Contains(download.Bounds));
            Assert.True(Field<Button>(form, "preview").Width > 0);
            if (Environment.GetEnvironmentVariable("CODEXCLEANER_TEST_SCREENSHOTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(directory, $"update-{size.Width}.png"));
            }
        }

        Field<Button>(form, "downloadUpdate").PerformClick();
        Assert.Equal(NewRelease.ReleaseUri, opened);
        form.Close();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCheckAllowsRetryAndDoesNotDisableLocalControls(bool invalidResponse) => OnSta(() =>
    {
        using var fixture = new Fixture();
        var checker = new StubChecker(_ => Task.FromException<AvailableUpdate?>(invalidResponse
            ? new InvalidDataException("Malformed release metadata") : new HttpRequestException("Offline")));
        using var form = Create(fixture, checker);
        form.Show();
        PumpUntil(() => checker.Calls == 1 && Field<Button>(form, "refresh").Enabled);
        var button = Field<Button>(form, "checkUpdates");
        Assert.True(button.Enabled);
        Assert.Equal("检查更新", button.Text);
        button.PerformClick();
        PumpUntil(() => checker.Calls == 2 && button.Enabled);
        Assert.Equal("检查失败，重试", button.Text);
        Assert.True(Field<TextBox>(form, "search").Enabled);
        Assert.False(Field<Panel>(form, "updateBanner").Visible);

        checker.Response = _ => Task.FromResult<AvailableUpdate?>(null);
        button.PerformClick();
        PumpUntil(() => checker.Calls == 3 && button.Enabled);
        Assert.Equal("已是最新版本", button.Text);
        Assert.False(Field<Panel>(form, "updateBanner").Visible);
        form.Close();
    });

    [Fact]
    public void DisposingWindowCancelsPendingCheckAndIgnoresLateResult() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var response = new TaskCompletionSource<AvailableUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var checker = new StubChecker(token => { received = token; return response.Task; });
        var form = Create(fixture, checker);
        var pending = (Task)typeof(MainForm).GetMethod("CheckForUpdates", PrivateInstance)!.Invoke(form, [false])!;
        Assert.False(pending.IsCompleted);
        form.Dispose();
        Assert.True(received.IsCancellationRequested);
        response.SetResult(NewRelease);
        PumpUntil(() => pending.IsCompleted);
        pending.GetAwaiter().GetResult();
    });

    [Fact]
    public void RepeatedCheckDoesNotStartConcurrentRequests() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var response = new TaskCompletionSource<AvailableUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new StubChecker(_ => response.Task);
        using var form = Create(fixture, checker);
        var method = typeof(MainForm).GetMethod("CheckForUpdates", PrivateInstance)!;
        var first = (Task)method.Invoke(form, [true])!;
        var second = (Task)method.Invoke(form, [true])!;
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, checker.Calls);
        response.SetResult(null);
        PumpUntil(() => first.IsCompleted);
        first.GetAwaiter().GetResult();
    });

    private static MainForm Create(Fixture fixture, IUpdateChecker checker, Action<Uri>? open = null) =>
        new(fixture.Cleaner, checker, open ?? (_ => throw new InvalidOperationException("Unexpected browser launch")))
        { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form)!;

    private static void PumpUntil(Func<bool> completed)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(completed(), "The synthetic UI operation did not finish.");
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Include the synthetic SQLite setup/cleanup budget on hosted Windows runners.
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The isolated UI test did not finish within two minutes.");
        failure?.Throw();
    }

    private sealed class StubChecker(Func<CancellationToken, Task<AvailableUpdate?>> response) : IUpdateChecker
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<AvailableUpdate?>> Response { get; set; } = response;
        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Response(cancellationToken);
        }
    }
}
