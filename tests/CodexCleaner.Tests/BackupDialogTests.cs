using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class BackupDialogTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SelectAllChecksOnlyRestorableChatsAndBlockedRowsStayUnchecked() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var dialog = Create(fixture, new BackupInfo("synthetic-backup", DateTimeOffset.UnixEpoch, 4096,
            [new(Fixture.Ids[0], "可恢复聊天", 3, null), new(Fixture.Ids[1], "已有聊天", 2, "当前已存在")], false, false, false, null, false));
        var chats = Field<CheckedListBox>(dialog, "chatList");
        var all = Field<Button>(dialog, "selectAll");
        Assert.Equal(2, chats.Items.Count);
        Assert.False(Field<Button>(dialog, "restore").Enabled);

        Click(all);
        Assert.Equal([0], chats.CheckedIndices.Cast<int>());
        Assert.Equal("取消全选", all.Text);
        Assert.True(Field<Button>(dialog, "restore").Enabled);
        chats.SetItemChecked(1, true);
        Assert.False(chats.GetItemChecked(1));

        Click(all);
        Assert.Empty(chats.CheckedIndices.Cast<int>());
        Assert.Equal("全选可恢复聊天", all.Text);
        Assert.False(Field<Button>(dialog, "restore").Enabled);
        chats.SelectedIndex = 1;
        Assert.Contains("当前已存在", Field<TextBox>(dialog, "details").Text);
    });

    [Fact]
    public void IncompleteBackupHasSeparateRecoveryAndCannotBeDeleted() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var dialog = Create(fixture, new BackupInfo("synthetic-incomplete", DateTimeOffset.UnixEpoch, 1024,
            [new(Fixture.Ids[0], "未完成聊天", 3, null)], true, true, true, null, true));
        Assert.True(Field<Button>(dialog, "recover").Enabled);
        Assert.False(Field<Button>(dialog, "restore").Enabled);
        Assert.False(Field<Button>(dialog, "remove").Enabled);
        Assert.False(Field<Button>(dialog, "selectAll").Enabled);
        Assert.False(Field<CheckBox>(dialog, "memories").Enabled);
        Assert.False(Field<CheckBox>(dialog, "shared").Enabled);
        var chats = Field<CheckedListBox>(dialog, "chatList");
        chats.SetItemChecked(0, true);
        Assert.False(chats.GetItemChecked(0));
        Assert.Contains("未完成", Field<TextBox>(dialog, "details").Text);
        Assert.Contains("退出 Codex", Field<Label>(dialog, "status").Text);
    });

    [Fact]
    public void GlobalGroupsRequireSeparateSelectionAndInvalidBackupsStayBlocked() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var dialog = Create(fixture, new BackupInfo("synthetic-global", DateTimeOffset.UnixEpoch, 2048, [], true, true, false, null, false));
        var memories = Field<CheckBox>(dialog, "memories");
        var shared = Field<CheckBox>(dialog, "shared");
        Assert.True(memories.Enabled);
        Assert.True(shared.Enabled);
        Assert.False(memories.Checked);
        Assert.False(shared.Checked);
        Assert.False(Field<Button>(dialog, "restore").Enabled);
        memories.Checked = true;
        Assert.True(Field<Button>(dialog, "restore").Enabled);
        memories.Checked = false;
        Assert.False(Field<Button>(dialog, "restore").Enabled);
        shared.Checked = true;
        Assert.True(Field<Button>(dialog, "restore").Enabled);

        SetItems(dialog, [new BackupInfo("synthetic-invalid", DateTimeOffset.UnixEpoch, 2048, [], true, true, false, "数据库结构不同", false)]);
        Assert.False(memories.Enabled);
        Assert.False(shared.Enabled);
        Assert.False(Field<Button>(dialog, "restore").Enabled);
        Assert.False(Field<Button>(dialog, "recover").Enabled);
        Assert.Contains("数据库结构不同", Field<TextBox>(dialog, "details").Text);
    });

    [Fact]
    public void BackgroundOperationKeepsMessageLoopResponsiveAndDisablesActions() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var dialog = Create(fixture, new BackupInfo("synthetic-backup", DateTimeOffset.UnixEpoch, 4096,
            [new(Fixture.Ids[0], "可恢复聊天", 3, null)], false, false, false, null, false));
        Click(Field<Button>(dialog, "selectAll"));
        using var release = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var workerThread = 0;
        var ticks = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        Func<IProgress<string>, Task> work = progress => Task.Run(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            progress.Report("正在恢复会话文件");
            release.Wait(TimeSpan.FromSeconds(5));
        });
        var task = (Task)typeof(BackupDialog).GetMethod("Run", PrivateInstance)!.Invoke(dialog, ["正在恢复", work])!;
        try
        {
            Assert.False(task.IsCompleted);
            Assert.False(Field<Button>(dialog, "restore").Enabled);
            Assert.False(Field<Button>(dialog, "remove").Enabled);
            Assert.False(Field<Button>(dialog, "refresh").Enabled);
            Assert.False(Field<CheckedListBox>(dialog, "chatList").Enabled);
            PumpUntil(() => ticks >= 3 && Field<Label>(dialog, "status").Text.Contains("会话文件"));
            Assert.NotEqual(uiThread, workerThread);
            Assert.Contains("秒", Field<Label>(dialog, "status").Text);
        }
        finally { release.Set(); }
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Assert.True(Field<Button>(dialog, "restore").Enabled);
        Assert.True(Field<Button>(dialog, "refresh").Enabled);
        Assert.False(Field<System.Windows.Forms.Timer>(dialog, "busyTimer").Enabled);
    });

    [Fact]
    public void FailedOperationStopsTimerAndRestoresControls() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var dialog = Create(fixture, new BackupInfo("synthetic-backup", DateTimeOffset.UnixEpoch, 4096,
            [new(Fixture.Ids[0], "可恢复聊天", 3, null)], false, false, false, null, false));
        Click(Field<Button>(dialog, "selectAll"));
        Func<IProgress<string>, Task> work = _ => Task.FromException(new IOException("Synthetic read failure"));
        var task = (Task)typeof(BackupDialog).GetMethod("Run", PrivateInstance)!.Invoke(dialog, ["正在读取", work])!;
        Assert.Throws<IOException>(() => task.GetAwaiter().GetResult());
        Assert.True(Field<Button>(dialog, "restore").Enabled);
        Assert.True(Field<Button>(dialog, "refresh").Enabled);
        Assert.False(Field<System.Windows.Forms.Timer>(dialog, "busyTimer").Enabled);
        Assert.False(dialog.UseWaitCursor);
    });

    [Fact]
    public void MainWindowKeepsBackupEntryAvailableForRecoveryButDisablesItWhileBusy() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = new MainForm(fixture.Cleaner);
        var backups = (Button)typeof(MainForm).GetField("backups", PrivateInstance)!.GetValue(form)!;
        Assert.IsType<ThemeButton>(backups);
        Assert.Equal("备份与恢复", backups.Text);
        Assert.True(backups.Enabled);
        typeof(MainForm).GetField("busy", PrivateInstance)!.SetValue(form, true);
        typeof(MainForm).GetMethod("RenderSelection", PrivateInstance)!.Invoke(form, null);
        Assert.False(backups.Enabled);
        typeof(MainForm).GetField("busy", PrivateInstance)!.SetValue(form, false);
        typeof(MainForm).GetMethod("RenderSelection", PrivateInstance)!.Invoke(form, null);
        Assert.True(backups.Enabled);
    });

    [Fact]
    public void CleanupConfirmationShowsBackupSpaceAndSeparateGlobalRestoreScope() => OnSta(() =>
    {
        var plan = new Plan("synthetic-preview", false, DateTimeOffset.Now.AddMinutes(10), new Selection([Fixture.Ids[0]], true, true),
            [new ChatInfo(Fixture.Ids[0], "待清理聊天", DateTimeOffset.UnixEpoch, false, "sessions/example.jsonl", false, null)], 0, [], [], []);
        using var dialog = new ConfirmDialog(plan, () => new ProcessState(false, []), 1024 * 1024);
        var controls = Descendants(dialog).ToList();
        var text = string.Join("\n", controls.OfType<Label>().Select(label => label.Text));
        Assert.Contains("预计新增 1 MB", text);
        Assert.Contains("退出 Codex", text);
        Assert.Contains("附加组", text);
        Assert.DoesNotContain("不提供撤销", text);
        var tree = Assert.Single(controls.OfType<TreeView>());
        var scope = Assert.Single(tree.Nodes.Cast<TreeNode>(), node => node.Text.StartsWith("本次范围", StringComparison.Ordinal));
        var backup = Assert.Single(scope.Nodes.Cast<TreeNode>(), node => node.Text == "删除前备份");
        Assert.Contains("峰值空间", (string)backup.Tag!);
        Assert.Contains("置顶、草稿和窗口状态不会自动恢复", (string)backup.Tag!);
    });

    private static BackupDialog Create(Fixture fixture, BackupInfo backup)
    {
        var dialog = new BackupDialog(fixture.Cleaner);
        _ = dialog.Handle;
        SetItems(dialog, [backup]);
        return dialog;
    }

    private static void SetItems(BackupDialog dialog, IReadOnlyList<BackupInfo> items)
    {
        typeof(BackupDialog).GetField("items", PrivateInstance)!.SetValue(dialog, items);
        var backups = Field<ListBox>(dialog, "backupList");
        backups.Items.Clear();
        foreach (var item in items) backups.Items.Add(item.Path);
        backups.SelectedIndex = 0;
    }

    private static void Click(Button button)
    {
        Assert.True(button.Enabled);
        typeof(Control).GetMethod("OnClick", PrivateInstance)!.Invoke(button, [EventArgs.Empty]);
    }

    private static T Field<T>(BackupDialog dialog, string name) => (T)typeof(BackupDialog).GetField(name, PrivateInstance)!.GetValue(dialog)!;

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
        Assert.True(condition(), "The UI operation did not complete in time.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The isolated UI test did not finish within two minutes.");
        failure?.Throw();
    }
}
