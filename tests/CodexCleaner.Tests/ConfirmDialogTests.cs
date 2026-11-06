using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class ConfirmDialogTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Id = "55555555-5555-4555-8555-555555555555";

    [Fact]
    public void InitialConfirmationIsDisabledAndReturnIsTheSafeDefault() => OnSta(() =>
    {
        using var dialog = new ConfirmDialog(SyntheticPlan(), () => new ProcessState(false, []));
        Assert.False(Field<Button>(dialog, "confirm").Enabled);
        Assert.Null(dialog.AcceptButton);
        var cancel = Assert.IsType<Button>(dialog.CancelButton);
        Assert.Equal(DialogResult.Cancel, cancel.DialogResult);
        Assert.Same(cancel, dialog.ActiveControl);
        Assert.Equal(DialogResult.None, dialog.DialogResult);
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EitherRunningSignalPreventsConfirmation(bool running, bool hasProcess) => OnSta(() =>
    {
        using var dialog = new ConfirmDialog(SyntheticPlan(), () => new ProcessState(running, hasProcess ? ["Synthetic Codex (42)"] : []));
        UpdateGate(dialog);
        Assert.False(Field<Button>(dialog, "confirm").Enabled);
        Assert.Contains("退出 Codex", Field<Label>(dialog, "gate").Text);
    });

    [Fact]
    public void StoppedCodexAndValidPreviewEnableConfirmation() => OnSta(() =>
    {
        using var dialog = new ConfirmDialog(SyntheticPlan(), () => new ProcessState(false, []));
        UpdateGate(dialog);
        Assert.True(Field<Button>(dialog, "confirm").Enabled);
        Assert.Contains("可以执行", Field<Label>(dialog, "gate").Text);
        Assert.Equal(DialogResult.None, dialog.DialogResult);
    });

    [Fact]
    public void ExpiredPreviewRemainsBlockedAfterCodexStops() => OnSta(() =>
    {
        using var dialog = new ConfirmDialog(SyntheticPlan() with { ExpiresAt = DateTimeOffset.Now.AddMinutes(-1) },
            () => new ProcessState(false, []));
        UpdateGate(dialog);
        Assert.False(Field<Button>(dialog, "confirm").Enabled);
        Assert.Contains("预览已过期", Field<Label>(dialog, "gate").Text);
    });

    [Fact]
    public void DetectionFailureRevokesAnEarlierSuccessfulCheck() => OnSta(() =>
    {
        var fail = false;
        using var dialog = new ConfirmDialog(SyntheticPlan(), () => fail
            ? throw new IOException("Synthetic process detection failure") : new ProcessState(false, []));
        UpdateGate(dialog);
        Assert.True(Field<Button>(dialog, "confirm").Enabled);
        fail = true;
        UpdateGate(dialog);
        Assert.False(Field<Button>(dialog, "confirm").Enabled);
        Assert.Contains("无法确认", Field<Label>(dialog, "gate").Text);
        Assert.Equal("Synthetic process detection failure", Field<Label>(dialog, "gate").AccessibleDescription);
    });

    [Theory]
    [InlineData("legacy/旧版迁移/完整路径与处理原因必须可复制的会话文件.jsonl")]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordOnlyPreviewShowsCompleteReasonAndExplicitMissingPath(string originalPath) => OnSta(() =>
    {
        var plan = SyntheticPlan(originalPath);
        using var dialog = new ConfirmDialog(plan, () => new ProcessState(false, []));
        var controls = Descendants(dialog).ToList();
        var tree = Assert.Single(controls.OfType<TreeView>());
        var detail = Assert.Single(controls.OfType<TextBox>(), box => box.AccessibleName == "所选项目完整说明");
        Assert.True(detail.ReadOnly);
        Assert.True(detail.Multiline);

        // A native handle is enough to exercise AfterSelect; no window is shown.
        _ = tree.Handle;
        var kept = Assert.Single(tree.Nodes.Cast<TreeNode>(), node => node.Text.StartsWith("保留的会话文件", StringComparison.Ordinal));
        tree.SelectedNode = Assert.Single(kept.Nodes.Cast<TreeNode>());
        Assert.Contains(plan.Chats[0].Title, detail.Text);
        Assert.Contains(plan.Chats[0].BlockedReason!, detail.Text);
        Assert.Contains("不会按此路径", detail.Text);
        Assert.Contains("重新出现", detail.Text);
        Assert.Contains(string.IsNullOrWhiteSpace(originalPath) ? "未提供会话文件路径" : originalPath, detail.Text);

        var chats = Assert.Single(tree.Nodes.Cast<TreeNode>(), node => node.Text.StartsWith("选中的聊天", StringComparison.Ordinal));
        tree.SelectedNode = Assert.Single(chats.Nodes.Cast<TreeNode>());
        Assert.Contains(Id, detail.Text);
        Assert.Contains("仅清理本地记录", detail.Text);
        Assert.Contains(string.IsNullOrWhiteSpace(originalPath) ? "未提供会话文件路径" : originalPath, detail.Text);
        Assert.False(Field<Button>(dialog, "confirm").Enabled);
    });

    private static Plan SyntheticPlan(string path = "legacy/imported.jsonl") => new(
        "synthetic-preview", false, DateTimeOffset.Now.AddMinutes(10),
        new Selection([Id]) { AllowRecordOnly = true },
        [new ChatInfo(Id, "合成迁移记录：完整标题和处理范围", DateTimeOffset.UnixEpoch, false, null, false,
            "会话文件不在已验证目录。仅清理本地记录，不按此路径删除原会话文件。")
        { OriginalPath = path, CanRemoveRecord = true }],
        0, [], [], ["仅清理已明确选择的合成本地记录。"]);

    private static T Field<T>(ConfirmDialog dialog, string name) =>
        (T)typeof(ConfirmDialog).GetField(name, PrivateInstance)!.GetValue(dialog)!;

    private static void UpdateGate(ConfirmDialog dialog)
    {
        var task = (Task)typeof(ConfirmDialog).GetMethod("UpdateGate", PrivateInstance)!.Invoke(dialog, null)!;
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "The synthetic process check did not finish.");
        Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString());
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The isolated confirmation test did not finish.");
        failure?.Throw();
    }
}
