using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexCleaner.Engine;

namespace CodexCleaner.Tests;

public sealed class MainFormTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ProtectedReason = "此聊天是自动化 每周检查（synthetic-job）的目标，请先在 Codex 中处理该任务。完整限制原因必须可读。";

    [Fact]
    public void GlobalCleanupStartsUncheckedAndPreviewNeedsASelection() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        Assert.False(Field<CheckBox>(form, "clearMemories").Checked);
        Assert.False(Field<CheckBox>(form, "clearShared").Checked);
        Assert.False(Field<Button>(form, "preview").Enabled);
        Assert.Empty(Field<HashSet<string>>(form, "selected"));
    });

    [Fact]
    public void RecordOnlyCanBeCheckedWhileProtectedChatCannot() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        var grid = Field<DataGridView>(form, "list");

        CheckRow(grid, Fixture.Ids[1]);
        Assert.Contains(Fixture.Ids[1], Field<HashSet<string>>(form, "selected"));
        Assert.True((bool)Row(grid, Fixture.Ids[1]).Cells[0].Value!);
        Assert.Contains("仅清理本地记录", Field<Label>(form, "selectionCount").Text);
        Assert.True(Field<Button>(form, "preview").Enabled);

        CheckRow(grid, Fixture.Stale);
        Assert.DoesNotContain(Fixture.Stale, Field<HashSet<string>>(form, "selected"));
        Assert.False((bool)Row(grid, Fixture.Stale).Cells[0].Value!);
        Assert.Contains(ProtectedReason, Field<TextBox>(form, "details").Text);
    });

    [Fact]
    public void FilteringRetainsSelectionsAndClearlyCountsHiddenChats() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        Click(Field<Button>(form, "selectAll"));
        Assert.Equal(2, Field<HashSet<string>>(form, "selected").Count);
        Assert.DoesNotContain(Fixture.Stale, Field<HashSet<string>>(form, "selected"));

        Field<TextBox>(form, "search").Text = "界面设计";
        Assert.Single(Field<DataGridView>(form, "list").Rows.Cast<DataGridViewRow>());
        Assert.Contains("已选 2 条", Field<Label>(form, "summary").Text);
        Assert.Contains("筛选外仍有 1 条已选", Field<Label>(form, "selectionCount").Text);

        // The result toggle removes the visible selection only; the summary must
        // still disclose the hidden chat which would be included in a preview.
        Click(Field<Button>(form, "selectAll"));
        Assert.Equal(Fixture.Ids[1], Assert.Single(Field<HashSet<string>>(form, "selected")));
        Assert.Contains("已选 1 条", Field<Label>(form, "summary").Text);
        Assert.Contains("筛选外仍有 1 条已选", Field<Label>(form, "selectionCount").Text);
        Assert.True(Field<Button>(form, "preview").Enabled);

        Click(Field<Button>(form, "clearSelection"));
        Assert.Empty(Field<HashSet<string>>(form, "selected"));
        Assert.False(Field<Button>(form, "preview").Enabled);
        Assert.DoesNotContain("筛选外", Field<Label>(form, "selectionCount").Text);
    });

    [Fact]
    public void StatusFilterKeepsRecordOnlyAndProtectedChatsDistinct() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        var filter = Field<ComboBox>(form, "filter");
        var grid = Field<DataGridView>(form, "list");

        filter.SelectedItem = "仅清理记录";
        Assert.Equal(Fixture.Ids[1], ((ChatInfo)Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Tag!).Id);
        Click(Field<Button>(form, "selectAll"));
        filter.SelectedItem = "受保护";
        Assert.Equal(Fixture.Stale, ((ChatInfo)Assert.Single(grid.Rows.Cast<DataGridViewRow>()).Tag!).Id);
        Assert.False(Field<Button>(form, "selectAll").Enabled);
        Assert.Contains("筛选外仍有 1 条已选", Field<Label>(form, "selectionCount").Text);
        filter.SelectedItem = "可清理";
        Assert.Equal(2, grid.Rows.Count);
        Assert.DoesNotContain(grid.Rows.Cast<DataGridViewRow>(), row => ((ChatInfo)row.Tag!).Id == Fixture.Stale);
    });

    [Fact]
    public void ProtectedRowDetailsExposeTheFullReasonTitleAndPath() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        var grid = Field<DataGridView>(form, "list");
        var chat = (ChatInfo)Row(grid, Fixture.Stale).Tag!;
        CheckRow(grid, chat.Id);
        var details = Field<TextBox>(form, "details");
        Assert.Contains(chat.Title, details.Text);
        Assert.Contains(chat.Id, details.Text);
        Assert.Contains(chat.OriginalPath!, details.Text);
        Assert.Contains(ProtectedReason, details.Text);
        Assert.True(Field<Button>(form, "copyDetails").Enabled);
        Assert.Empty(Field<HashSet<string>>(form, "selected"));
        Assert.False(Field<Button>(form, "preview").Enabled);
    });

    [Fact]
    public void ChangingCurrentRowImmediatelyRefreshesTheCompleteDetails() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        var grid = Field<DataGridView>(form, "list");
        var details = Field<TextBox>(form, "details");
        string? previousId = null;

        // Exercise the native current-cell event sequence without invoking a
        // checkbox handler, which could otherwise refresh stale details itself.
        foreach (var id in new[] { Fixture.Ids[0], Fixture.Ids[1], Fixture.Stale, Fixture.Ids[0] })
        {
            var row = Row(grid, id);
            var chat = (ChatInfo)row.Tag!;
            grid.CurrentCell = row.Cells[1];

            Assert.Equal(id, ((ChatInfo)grid.CurrentRow!.Tag!).Id);
            Assert.Contains(chat.Id, details.Text);
            Assert.Contains(chat.Title, details.Text);
            Assert.Contains(chat.OriginalPath!, details.Text);
            if (chat.BlockedReason is not null) Assert.Contains(chat.BlockedReason, details.Text);
            if (previousId is not null) Assert.DoesNotContain(previousId, details.Text);
            if (id != Fixture.Stale) Assert.DoesNotContain(ProtectedReason, details.Text);
            previousId = id;
        }

        Assert.Empty(Field<HashSet<string>>(form, "selected"));
        Assert.False(Field<Button>(form, "preview").Enabled);
    });

    [Fact]
    public void ClearingAnUnmatchedSearchRestoresTheList() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        Field<TextBox>(form, "search").Text = "this synthetic search has no match";
        Assert.Empty(Field<DataGridView>(form, "list").Rows.Cast<DataGridViewRow>());
        Assert.Contains("没有匹配", Field<Label>(form, "emptyState").Text);
        Assert.False(Field<Button>(form, "selectAll").Enabled);
        Assert.False(Field<Button>(form, "preview").Enabled);
        Click(Field<Button>(form, "clearSearch"));
        Assert.Equal(3, Field<DataGridView>(form, "list").Rows.Count);
        Assert.Equal("", Field<TextBox>(form, "search").Text);
        Assert.False(Field<Button>(form, "clearSearch").Enabled);
    });

    [Fact]
    public void UnresolvedRecoveryDisablesCleanupAndRepairEvenWithSelection() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        CheckRow(Field<DataGridView>(form, "list"), Fixture.Ids[0]);
        var current = Field<Snapshot>(form, "state");
        SetField(form, "state", current with
        {
            RecoveryIssues = [new RecoveryIssue("synthetic-recovery-directory", "Synthetic interrupted transaction", true)],
        });
        Invoke(form, "RenderSelection");
        Assert.False(Field<Button>(form, "preview").Enabled);
        Assert.False(Field<Button>(form, "repair").Enabled);
        Assert.False(Field<CheckBox>(form, "clearMemories").Enabled);
        Assert.False(Field<CheckBox>(form, "clearShared").Enabled);
        Assert.True(Field<DataGridView>(form, "list").Enabled);
    });

    [Fact]
    public void FailedReloadDiscardsStaleSelectionsAndDisablesPreview() => OnSta(() =>
    {
        using var fixture = new Fixture();
        using var form = Create(fixture);
        CheckRow(Field<DataGridView>(form, "list"), Fixture.Ids[0]);
        Assert.True(Field<Button>(form, "preview").Enabled);
        fixture.Cleaner.Hooks.DuringRead = () => throw new IOException("Synthetic refresh failure");

        var refresh = (Task)Invoke(form, "LoadState")!;
        var watch = Stopwatch.StartNew();
        while (!refresh.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(refresh.IsCompleted, "The synthetic refresh did not finish.");
        Assert.True(refresh.IsFaulted);
        Assert.IsType<IOException>(refresh.Exception!.InnerException);
        Assert.Empty(Field<HashSet<string>>(form, "selected"));
        Assert.Empty(Field<DataGridView>(form, "list").Rows.Cast<DataGridViewRow>());
        Assert.False(Field<Button>(form, "preview").Enabled);
        Assert.Contains("暂时无法读取", Field<Label>(form, "emptyState").Text);
    });

    private static MainForm Create(Fixture fixture)
    {
        var form = new MainForm(fixture.Cleaner);
        var snapshot = fixture.Cleaner.Snapshot() with
        {
            Chats =
            [
                new ChatInfo(Fixture.Ids[0], "界面设计讨论", DateTimeOffset.UnixEpoch, false, "sessions/normal.jsonl", false, null)
                { OriginalPath = "sessions/normal.jsonl" },
                new ChatInfo(Fixture.Ids[1], "旧版迁移记录", DateTimeOffset.UnixEpoch, false, null, false,
                    "会话文件不在已验证目录。可仅清理本地记录，原会话文件保留。")
                { OriginalPath = "legacy/imported.jsonl", CanRemoveRecord = true },
                new ChatInfo(Fixture.Stale, "自动化任务：完整的长标题和限制原因需要在详情中完整呈现", DateTimeOffset.UnixEpoch, false,
                    "sessions/protected.jsonl", false, ProtectedReason)
                { OriginalPath = "sessions/a-long-synthetic-project-path/protected.jsonl" },
            ],
        };
        SetField(form, "state", snapshot);
        Invoke(form, "RenderChats");
        return form;
    }

    private static T Field<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form)!;

    private static void SetField(MainForm form, string name, object value) =>
        typeof(MainForm).GetField(name, PrivateInstance)!.SetValue(form, value);

    private static object? Invoke(MainForm form, string name) =>
        typeof(MainForm).GetMethod(name, PrivateInstance)!.Invoke(form, null);

    private static void Click(Button button)
    {
        Assert.True(button.Enabled, $"The button '{button.Text}' must be enabled before a user can click it.");
        typeof(Button).GetMethod("OnClick", PrivateInstance)!.Invoke(button, [EventArgs.Empty]);
    }

    private static DataGridViewRow Row(DataGridView grid, string id) =>
        grid.Rows.Cast<DataGridViewRow>().Single(row => ((ChatInfo)row.Tag!).Id == id);

    private static void CheckRow(DataGridView grid, string id)
    {
        Assert.True(grid.Enabled, "The conversation list must be enabled before a user can select a row.");
        typeof(DataGridView).GetMethod("OnCellClick", PrivateInstance)!.Invoke(grid, [new DataGridViewCellEventArgs(0, Row(grid, id).Index)]);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The isolated UI test did not finish.");
        failure?.Throw();
    }
}
