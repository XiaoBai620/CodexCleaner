using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace CodexCleaner.Tests;

public sealed class UpdateDialogTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly AvailableUpdate Release = new(new Version(9, 0, 0),
        new Uri("https://github.com/XiaoBai620/CodexCleaner/releases/tag/v9.0.0"),
        "## 本次更新\n- 在程序内下载、校验并重启更新。\n- 下载时显示进度，并支持取消。\n- 更新失败时保留原程序。",
        new UpdatePackage(new Uri("https://github.com/XiaoBai620/CodexCleaner/releases/download/v9.0.0/CodexCleaner.exe"),
            new Uri("https://github.com/XiaoBai620/CodexCleaner/releases/download/v9.0.0/SHA256SUMS.txt"), 4 * 1048576));

    [Fact]
    public void DetailsAreReadableAndMissingPackageOffersManualDownload() => OnSta(() =>
    {
        Uri? opened = null;
        using var dialog = Create(Release with { Package = null }, uri => opened = uri);
        dialog.Show();
        Assert.False(Field<Button>(dialog, "install").Enabled);
        Assert.Contains("手动下载", Field<Label>(dialog, "status").Text);
        Assert.DoesNotContain("点击「下载并重启」", Field<Label>(dialog, "status").Text);
        var notes = Descendants(dialog).OfType<TextBoxBase>().Single();
        Assert.Contains("在程序内下载", notes.Text);
        Assert.Contains("本次更新", notes.Text);
        Assert.DoesNotContain("## ", notes.Text);
        Assert.True(notes.ReadOnly);
        Field<Button>(dialog, "manual").PerformClick();
        Assert.Equal(Release.ReleaseUri, opened);
        dialog.Close();
    });

    [Fact]
    public void ReleaseNotesKeepNestedListsAndCodeReadable() => OnSta(() =>
    {
        var notes = "## 新增\n- 普通条目\n  - 子项\n    # 缩进代码\n    - 原样条目\n~~~~ text\n## 围栏代码\n~~~\n- 仍在代码内\n~~~~\n- 代码结束";
        using var dialog = Create(Release with { Notes = notes });
        dialog.Show();
        var text = Descendants(dialog).OfType<TextBoxBase>().Single().Text;
        Assert.DoesNotContain("## 新增", text);
        Assert.Contains("• 普通条目", text);
        Assert.Contains("  • 子项", text);
        Assert.Contains("    # 缩进代码", text);
        Assert.Contains("    - 原样条目", text);
        Assert.Contains("## 围栏代码", text);
        Assert.Contains("- 仍在代码内", text);
        Assert.Contains("• 代码结束", text);
        dialog.Close();
    });

    [Fact]
    public void DownloadReportsProgressAndHandsOffOnlyOnce() => OnSta(() =>
    {
        var pending = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<UpdateProgress>? updates = null;
        var updater = new StubUpdater((progress, _) => { updates = progress; return pending.Task; });
        var uiThread = Environment.CurrentManagedThreadId;
        var installThread = uiThread;
        var installer = new StubInstaller(_ => installThread = Environment.CurrentManagedThreadId);
        var discarded = 0;
        using var dialog = Create(Release, updater: updater, installer: installer, discard: _ => discarded++);
        dialog.Show();
        Save(dialog, "update-dialog-details");
        var normalSize = dialog.Size;
        dialog.Size = dialog.MinimumSize;
        dialog.PerformLayout();
        Application.DoEvents();
        Save(dialog, "update-dialog-minimum");
        foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Visible))
            Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds), $"{button.Text}: {button.Bounds} outside {button.Parent.ClientRectangle}");
        dialog.Size = normalSize;
        var operation = Start(dialog);
        Assert.True(Start(dialog).IsCompletedSuccessfully);
        Assert.Equal(1, updater.Calls);
        Assert.False(Field<Button>(dialog, "install").Enabled);
        updates!.Report(new UpdateProgress(2 * 1048576, 4 * 1048576, "正在下载更新…"));
        PumpUntil(() => Field<ProgressBar>(dialog, "progress").Value == 500 &&
            Field<Label>(dialog, "status").Text.Contains("2.0 / 4.0 MB", StringComparison.Ordinal));
        Assert.Contains("2.0 / 4.0 MB", Field<Label>(dialog, "status").Text);
        Assert.Contains("50%", Field<Label>(dialog, "status").Text);
        Save(dialog, "update-dialog-downloading");
        pending.SetResult(Prepared(1));
        PumpUntil(() => operation.IsCompleted);
        operation.GetAwaiter().GetResult();
        Assert.Equal(1, installer.Calls);
        Assert.NotEqual(uiThread, installThread);
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        Assert.Equal(0, discarded);
    });

    [Fact]
    public void ClosingDuringDownloadCancelsWithoutInstalling() => OnSta(() =>
    {
        CancellationToken received = default;
        var updater = new StubUpdater(async (_, token) =>
        {
            received = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Prepared(1);
        });
        var installer = new StubInstaller(_ => { });
        using var dialog = Create(Release, updater: updater, installer: installer);
        dialog.Show();
        var operation = Start(dialog);
        dialog.Close();
        PumpUntil(() => operation.IsCompleted && dialog.IsDisposed);
        operation.GetAwaiter().GetResult();
        Assert.True(received.IsCancellationRequested);
        Assert.Equal(0, installer.Calls);
    });

    [Fact]
    public void DownloadFailureAllowsRetryWithoutClosing() => OnSta(() =>
    {
        var error = "下载校验失败，请重试。\n" + new string('长', 300) + "\n保留材料：C:\\synthetic\\update-test";
        var updater = new StubUpdater((_, _) => Task.FromException<PreparedUpdate>(new InvalidDataException(error)));
        using var dialog = Create(Release, updater: updater);
        dialog.Show();
        var operation = Start(dialog);
        PumpUntil(() => operation.IsCompleted);
        operation.GetAwaiter().GetResult();
        Assert.False(dialog.IsDisposed);
        Assert.True(Field<Button>(dialog, "install").Enabled);
        Assert.True(Field<Button>(dialog, "errorDetails").Visible);
        var normalSize = dialog.Size;
        dialog.Size = dialog.MinimumSize;
        dialog.PerformLayout();
        Application.DoEvents();
        foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Visible))
            Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds), $"{button.Text}: {button.Bounds} outside {button.Parent.ClientRectangle}");
        Save(dialog, "update-dialog-error-minimum");
        dialog.Size = normalSize;
        using var details = (Form)typeof(UpdateDialog).GetMethod("CreateErrorDetails", PrivateInstance)!.Invoke(dialog, null)!;
        details.Show();
        Assert.Equal(error.Replace("\n", Environment.NewLine), Descendants(details).OfType<TextBox>().Single().Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        Assert.True(Descendants(details).OfType<TextBox>().Single().ReadOnly);
        Save(details, "update-dialog-error-details");
        details.Close();
        Save(dialog, "update-dialog-error");
        dialog.Close();
    });

    [Fact]
    public void CancelButtonKeepsDetailsVisibleAndAllowsAnotherDownload() => OnSta(() =>
    {
        var second = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<IProgress<UpdateProgress>>();
        var updater = new StubUpdater(async (progress, token) =>
        {
            reports.Add(progress!);
            if (reports.Count == 2) return await second.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Prepared(1);
        });
        var installer = new StubInstaller(_ => { });
        using var dialog = Create(Release, updater: updater, installer: installer);
        ExceptionDispatchInfo? failure = null;
        var completed = false;
        // Production uses a modal message loop. Keep it active across awaits and
        // window teardown instead of closing a modeless form between DoEvents calls.
        dialog.Shown += async (_, _) =>
        {
            try
            {
                var first = Start(dialog);
                Field<Button>(dialog, "cancel").PerformClick();
                await first;
                Assert.False(dialog.IsDisposed);
                Assert.True(dialog.Visible);
                Assert.Contains("已取消", Field<Label>(dialog, "status").Text);
                Assert.Equal("重新下载", Field<Button>(dialog, "install").Text);
                Assert.True(Field<Button>(dialog, "install").Enabled);
                Save(dialog, "update-dialog-cancelled");
                Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
                var retry = Start(dialog);
                reports[0].Report(new UpdateProgress(3 * 1048576, 4 * 1048576, "上一轮延迟进度"));
                await Task.Yield();
                Assert.DoesNotContain("上一轮", Field<Label>(dialog, "status").Text);
                reports[1].Report(new UpdateProgress(1048576, 4 * 1048576, "正在下载更新…"));
                var watch = Stopwatch.StartNew();
                while (!Field<Label>(dialog, "status").Text.Contains("25%", StringComparison.Ordinal) && watch.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(10);
                Assert.Contains("25%", Field<Label>(dialog, "status").Text);
                second.SetException(new IOException("模拟网络中断。"));
                await retry;
                completed = true;
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally { dialog.Close(); }
        };
        dialog.ShowDialog();
        failure?.Throw();
        Assert.True(completed, "The cancellation and retry scenario did not complete.");
        Assert.Equal(2, updater.Calls);
        Assert.Equal(0, installer.Calls);
    });

    [Fact]
    public void CancelButtonKeepsModalDialogOpenForRetry() => OnSta(() =>
    {
        var updater = new StubUpdater(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Prepared(1);
        });
        using var dialog = Create(Release, updater: updater);
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        Task? operation = null;
        ExceptionDispatchInfo? failure = null;
        var checkedAfterCancellation = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (operation is null)
                {
                    operation = Start(dialog);
                    Field<Button>(dialog, "cancel").PerformClick();
                }
                else if (operation.IsCompleted)
                {
                    operation.GetAwaiter().GetResult();
                    Assert.True(dialog.Visible);
                    Assert.Contains("已取消", Field<Label>(dialog, "status").Text);
                    Assert.Equal("重新下载", Field<Button>(dialog, "install").Text);
                    Assert.True(Field<Button>(dialog, "install").Enabled);
                    checkedAfterCancellation = true;
                    dialog.Close();
                }
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); dialog.Close(); }
        };
        dialog.Shown += (_, _) => timer.Start();
        dialog.ShowDialog();
        timer.Stop();
        failure?.Throw();
        Assert.True(checkedAfterCancellation, "The modal dialog closed before cancellation could be retried.");
    });

    [Fact]
    public void ClosingDuringCancellationCleanupFailureKeepsTheErrorAvailable() => OnSta(() =>
    {
        var updater = new StubUpdater(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { throw new IOException("取消后的临时文件无法清除，请保留材料。" ); }
            return Prepared(1);
        });
        using var dialog = Create(Release, updater: updater);
        dialog.Show();
        var operation = Start(dialog);
        dialog.Close();
        PumpUntil(() => operation.IsCompleted);
        operation.GetAwaiter().GetResult();
        Assert.False(dialog.IsDisposed);
        Assert.True(Field<Button>(dialog, "errorDetails").Visible);
        dialog.Close();
    });

    [Fact]
    public void FailedInstallPreservesOnlyItsOwnUncertainHandoffAndRetryCanSucceed() => OnSta(() =>
    {
        var download = 0;
        var updater = new StubUpdater((_, _) => Task.FromResult(Prepared(++download)));
        var install = 0;
        var installer = new StubInstaller(_ =>
        {
            install++;
            if (install == 1) throw new UpdateInstallException("安装交接未完成，保留材料。", preserveFiles: true);
            if (install == 2) throw new IOException("原程序目录不可写。");
        });
        var discarded = new List<PreparedUpdate>();
        using var dialog = Create(Release, updater: updater, installer: installer, discard: discarded.Add);
        dialog.Show();
        var first = Start(dialog);
        PumpUntil(() => first.IsCompleted);
        first.GetAwaiter().GetResult();
        Assert.Empty(discarded);
        Assert.False(dialog.IsDisposed);
        var second = Start(dialog);
        PumpUntil(() => second.IsCompleted);
        second.GetAwaiter().GetResult();
        Assert.Equal(Prepared(2), Assert.Single(discarded));
        var third = Start(dialog);
        PumpUntil(() => third.IsCompleted);
        third.GetAwaiter().GetResult();
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        Assert.Single(discarded);
    });

    [Fact]
    public void PreparingInstallPreventsClosingUntilHandoffCompletes() => OnSta(() =>
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new StubUpdater((_, _) => Task.FromResult(Prepared(1)));
        var installer = new StubInstaller(_ => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); });
        using var dialog = Create(Release, updater: updater, installer: installer);
        dialog.Show();
        var operation = Start(dialog);
        try
        {
            PumpUntil(() => entered.Task.IsCompleted);
            dialog.Close();
            Assert.False(dialog.IsDisposed);
            Assert.False(Field<Button>(dialog, "cancel").Enabled);
            Assert.Contains("准备重启更新", Field<Label>(dialog, "status").Text);
        }
        finally { release.Set(); }
        PumpUntil(() => operation.IsCompleted);
        operation.GetAwaiter().GetResult();
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    });

    private static PreparedUpdate Prepared(int index) => new($"synthetic-{index}", $"synthetic-{index}/CodexCleaner.exe", new string('a', 64), Release.Version);

    private static UpdateDialog Create(AvailableUpdate update, Action<Uri>? open = null, IAppUpdater? updater = null,
        IUpdateInstaller? installer = null, Action<PreparedUpdate>? discard = null) =>
        new(update, open ?? (_ => throw new InvalidOperationException("Unexpected browser launch")),
            updater ?? new StubUpdater((_, _) => throw new InvalidOperationException("Unexpected download")),
            installer ?? new StubInstaller(_ => throw new InvalidOperationException("Unexpected installation")), discard ?? (_ => { }))
        { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static T Field<T>(UpdateDialog dialog, string name) => (T)typeof(UpdateDialog).GetField(name, PrivateInstance)!.GetValue(dialog)!;
    private static Task Start(UpdateDialog dialog) => (Task)typeof(UpdateDialog).GetMethod("InstallUpdate", PrivateInstance)!.Invoke(dialog, null)!;

    private static void Save(Form dialog, string name)
    {
        if (Environment.GetEnvironmentVariable("CODEXCLEANER_TEST_SCREENSHOTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); }
        Assert.True(completed(), "The synthetic update operation did not finish.");
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The update dialog test did not finish.");
        failure?.Throw();
    }

    private sealed class StubUpdater(Func<IProgress<UpdateProgress>?, CancellationToken, Task<PreparedUpdate>> download) : IAppUpdater
    {
        public int Calls { get; private set; }
        public Task<PreparedUpdate> DownloadAsync(AvailableUpdate update, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        { Calls++; return download(progress, cancellationToken); }
    }

    private sealed class StubInstaller(Action<PreparedUpdate> start) : IUpdateInstaller
    {
        public int Calls { get; private set; }
        public void Start(PreparedUpdate update) { Calls++; start(update); }
    }
}
