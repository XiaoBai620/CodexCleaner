using System.Reflection;
using CodexCleaner.Core;

namespace CodexCleaner;

internal sealed class MainForm : Form
{
    private readonly Cleaner cleaner;
    private readonly HashSet<string> selected = [];
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    private readonly Label alerts = new() { AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(150, 80, 0), Padding = new Padding(10, 6, 10, 6), Visible = false };
    private readonly TextBox search = new() { PlaceholderText = "搜索标题或 ID", Width = 280 };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true };
    private readonly CheckBox selectAll = new() { Text = "全选", AutoSize = true };
    private readonly CheckBox clearMemories = new() { Text = "同时清空全部本地记忆（合并记忆可能涉及多条聊天）", AutoSize = true };
    private readonly CheckBox clearShared = new() { Text = "同时清空共享输入历史与日志（影响所有聊天）", AutoSize = true };
    private readonly Label summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(DefaultFont, FontStyle.Bold) };
    private readonly Button refresh = new() { Text = "刷新", AutoSize = true };
    private readonly Button repair = new() { Text = "检查失效引用", AutoSize = true };
    private readonly Button preview = new() { Text = "预览清理范围…", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer processTimer = new() { Interval = 3000 };
    private Snapshot? state;
    private bool busy, rendering;

    public MainForm(Cleaner cleaner)
    {
        this.cleaner = cleaner;
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Text = $"Codex 本地清理台 {version.Major}.{version.Minor}.{version.Build}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        MinimumSize = new Size(760, 480);
        Size = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;

        list.Columns.Add("聊天", 420);
        list.Columns.Add("最近更新", 150);
        list.Columns.Add("状态", 260);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
        toolbar.Controls.AddRange([search, selectAll, refresh, repair]);
        selectAll.Margin = refresh.Margin = repair.Margin = new Padding(12, 3, 0, 3);

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8, 6, 8, 0) };
        options.Controls.AddRange([clearMemories, clearShared, new Label
        {
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 0),
            Text = "清理范围由你选择，不会定时或自动删除。项目、插件、技能、登录信息和云端记录不受影响。清空记忆不会关闭 Codex 的记忆功能。",
        }]);

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 8, 10, 10) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(summary, 0, 0);
        footer.Controls.Add(preview, 1, 0);

        var header = new Panel { Dock = DockStyle.Top, Height = 34 };
        header.Controls.Add(status);

        Controls.Add(list);
        Controls.Add(toolbar);
        Controls.Add(alerts);
        Controls.Add(header);
        Controls.Add(options);
        Controls.Add(footer);

        search.TextChanged += (_, _) => RenderChats();
        list.ItemCheck += OnItemCheck;
        list.ItemChecked += (_, e) => { if (!rendering) Toggle((string)e.Item.Tag!, e.Item.Checked); };
        selectAll.Click += (_, _) => { foreach (var chat in VisibleChats().Where(c => c.CanDelete)) if (selectAll.Checked) selected.Add(chat.Id); else selected.Remove(chat.Id); RenderChats(); };
        clearMemories.CheckedChanged += (_, _) => RenderSelection();
        clearShared.CheckedChanged += (_, _) => RenderSelection();
        refresh.Click += async (_, _) => await Reload();
        repair.Click += async (_, _) => await PreviewRepair();
        preview.Click += async (_, _) => await PreviewCleanup();
        processTimer.Tick += async (_, _) => { if (!busy) SetProcess(await Task.Run(cleaner.CheckProcesses)); };
        Shown += async (_, _) => { await Reload(); processTimer.Start(); };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "清理或修复正在进行，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

    private IEnumerable<ChatInfo> VisibleChats()
    {
        var q = search.Text.Trim();
        return (state?.Chats ?? []).Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (!rendering && e.NewValue == CheckState.Checked && list.Items[e.Index].Tag is string id && state?.Chats.FirstOrDefault(c => c.Id == id) is { CanDelete: false })
            e.NewValue = CheckState.Unchecked;
    }

    private void Toggle(string id, bool on)
    {
        if (on) selected.Add(id); else selected.Remove(id);
        RenderSelectAll();
        RenderSelection();
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        UseWaitCursor = true;
        RenderSelection();
        try { await action(); }
        catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            UseWaitCursor = false;
            RenderSelection();
        }
    }

    private Task Reload() => Run(LoadState);

    private async Task LoadState()
    {
        var (snapshot, processState) = await Task.Run(() => (cleaner.Snapshot(), cleaner.CheckProcesses()));
        state = snapshot;
        selected.IntersectWith(snapshot.Chats.Where(c => c.CanDelete).Select(c => c.Id));
        SetProcess(processState);
        var warnings = snapshot.Warnings.Concat(snapshot.RecoveryIssues.Select(i =>
            (i.BlocksWrites ? "上次操作异常中断，清理已锁定。故障材料：" : "上次操作的临时副本未能移除：") + i.Directory + (i.Error.Length > 0 ? "。" + i.Error : ""))).ToList();
        alerts.Text = string.Join(Environment.NewLine, warnings);
        alerts.Visible = warnings.Count > 0;
        RenderChats();
    }

    private void SetProcess(ProcessState state)
    {
        var ready = !state.Running;
        status.BackColor = ready ? Color.FromArgb(225, 245, 230) : Color.FromArgb(255, 240, 215);
        status.Text = ready
            ? $"Codex 已退出，可以清理。数据目录：{cleaner.DataRoot}"
            : $"Codex 正在运行（{string.Join("、", state.Processes)}），退出后才能执行清理；现在可以浏览和预览。";
    }

    private void RenderChats()
    {
        rendering = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var chat in VisibleChats())
        {
            var note = chat.BlockedReason ?? (chat.RolloutMissing ? "会话文件已缺失，可清除残留索引" : chat.Archived ? "已归档" : "本地记录");
            var item = new ListViewItem([chat.Title, chat.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—", note])
            {
                Tag = chat.Id,
                Checked = selected.Contains(chat.Id),
                ToolTipText = $"{chat.Title}\n{chat.Id}" + (chat.BlockedReason is null ? "" : "\n" + chat.BlockedReason),
                ForeColor = chat.CanDelete ? SystemColors.WindowText : SystemColors.GrayText,
            };
            list.Items.Add(item);
        }
        list.EndUpdate();
        rendering = false;
        RenderSelectAll();
        RenderSelection();
    }

    private void RenderSelectAll()
    {
        var selectable = VisibleChats().Where(c => c.CanDelete).ToList();
        selectAll.Enabled = selectable.Count > 0 && !busy;
        selectAll.Checked = selectable.Count > 0 && selectable.All(c => selected.Contains(c.Id));
    }

    private void RenderSelection()
    {
        var items = new List<string>();
        if (selected.Count > 0) items.Add($"{selected.Count} 个聊天");
        if (clearMemories.Checked) items.Add("全部本地记忆");
        if (clearShared.Checked) items.Add("共享输入历史与日志");
        summary.Text = items.Count > 0 ? "已选择：" + string.Join(" + ", items) : $"共 {state?.Chats.Count ?? 0} 条聊天，尚未选择清理内容";
        var caps = state?.Capabilities;
        preview.Enabled = !busy && items.Count > 0 && caps is not null && (selected.Count == 0 || caps.DeleteChats) && caps.ClearData;
        repair.Enabled = !busy && caps?.RepairReferences == true;
        refresh.Enabled = search.Enabled = clearMemories.Enabled = clearShared.Enabled = list.Enabled = !busy;
    }

    private Task PreviewCleanup() => Run(async () =>
    {
        var selection = new Selection([.. selected], clearMemories.Checked, clearShared.Checked);
        var plan = await Task.Run(() => cleaner.PlanCleanup(selection));
        if (!Confirm(plan)) return;
        var result = await Task.Run(() => cleaner.Execute(plan.Id));
        selected.Clear();
        clearMemories.Checked = clearShared.Checked = false;
        await Done(result, "所选内容已删除。");
    });

    private Task PreviewRepair() => Run(async () =>
    {
        var plan = await Task.Run(cleaner.PlanRepair);
        if (plan.IsEmpty || (plan.ReferenceRepairs == 0 && plan.Files.Count == 0 && plan.DatabaseRows == 0))
        {
            MessageBox.Show(this, "检查完成，没有发现可修复的失效引用。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Confirm(plan)) return;
        await Done(await Task.Run(() => cleaner.ExecuteRepair(plan.Id)), "失效引用已修复，可以重新启动 Codex 检查项目和归档。");
    });

    private bool Confirm(Plan plan)
    {
        using var dialog = new ConfirmDialog(plan, cleaner.CheckProcesses);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private async Task Done(ExecuteResult result, string message)
    {
        if (result.ResidualDirectory is not null) message += $"\n\n临时事务副本未能完全删除：{result.ResidualDirectory}\n{result.ResidualError}";
        await LoadState();
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, result.ResidualDirectory is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
