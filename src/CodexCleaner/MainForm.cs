using System.Reflection;
using CodexCleaner.Engine;

namespace CodexCleaner;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(31, 41, 55);
    private static readonly Color Muted = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Surface = Color.White;
    private static readonly Color Canvas = Color.FromArgb(246, 248, 251);

    private readonly Cleaner cleaner;
    private readonly HashSet<string> selected = [];
    private readonly Label processTitle = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), ForeColor = Ink };
    private readonly Label processDetail = new() { AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = Muted, AutoEllipsis = true };
    private readonly Label processDot = new() { AutoSize = true, Text = "●", Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(0, 2, 10, 0) };
    private readonly Panel processBanner = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };
    private readonly Label alerts = new() { AutoSize = true, MaximumSize = new Size(1100, 0), ForeColor = Color.FromArgb(146, 64, 14), BackColor = Color.FromArgb(255, 247, 237), Padding = new Padding(12, 8, 12, 8), Visible = false };
    private readonly TextBox search = new() { PlaceholderText = "搜索聊天标题或 ID", Width = 300, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9f) };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Ink, HideSelection = false };
    private readonly Label emptyState = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Microsoft YaHei UI", 10f), ForeColor = Muted, Visible = false };
    private readonly Button selectAll = new() { Text = "全选本页", AutoSize = true };
    private readonly CheckBox clearMemories = new() { Text = "同时清空全部本地记忆", AutoSize = true, Checked = true, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), ForeColor = Ink };
    private readonly CheckBox clearShared = new() { Text = "同时清空共享输入历史与日志", AutoSize = true, Checked = true, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), ForeColor = Ink };
    private readonly Label summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), ForeColor = Ink };
    private readonly Label selectionCount = new() { AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Microsoft YaHei UI", 8.5f), ForeColor = Muted };
    private readonly Button refresh = new() { Text = "刷新", AutoSize = true };
    private readonly Button repair = new() { Text = "检查失效引用", AutoSize = true };
    private readonly Button preview = new() { Text = "预览清理范围  →", AutoSize = true, Enabled = false };
    private readonly ToolTip processTip = new();
    private readonly System.Windows.Forms.Timer processTimer = new() { Interval = 3000 };
    private Snapshot? state;
    private bool busy, rendering;

    public MainForm(Cleaner cleaner)
    {
        this.cleaner = cleaner;
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Text = $"Codex 本地清理台 {version.Major}.{version.Minor}.{version.Build}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        MinimumSize = new Size(820, 520);
        Size = new Size(1080, 700);
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;

        list.Columns.Add("聊天", 520);
        list.Columns.Add("最近更新", 170);
        list.Columns.Add("状态", 220);
        list.Resize += (_, _) => ResizeChatColumn();

        StyleButton(selectAll, primary: false);
        StyleButton(refresh, primary: false);
        StyleButton(repair, primary: false);
        StyleButton(preview, primary: true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18, 12, 18, 12),
            BackColor = Canvas,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));

        var masthead = BuildMasthead(version);
        var statusStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Canvas,
            Margin = new Padding(0, 10, 0, 0),
        };
        statusStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        statusStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        processBanner.Dock = DockStyle.Fill;
        var processLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        processLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        processLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        processLayout.Controls.Add(processDot, 0, 0);
        var processText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        processText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        processText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        processText.Controls.Add(processTitle, 0, 0);
        processText.Controls.Add(processDetail, 0, 1);
        processLayout.Controls.Add(processText, 1, 0);
        processBanner.Controls.Add(processLayout);
        processBanner.Height = 56;
        statusStack.Controls.Add(processBanner, 0, 0);
        alerts.Dock = DockStyle.Fill;
        alerts.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        alerts.MaximumSize = new Size(1500, 0);
        alerts.Margin = new Padding(0, 7, 0, 0);
        statusStack.Controls.Add(alerts, 0, 1);

        var toolbar = BuildToolbar();
        var listFrame = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(1), Margin = new Padding(0, 2, 0, 8), BorderStyle = BorderStyle.FixedSingle };
        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        listHost.Controls.Add(list);
        listHost.Controls.Add(emptyState);
        emptyState.BringToFront();
        listFrame.Controls.Add(listHost);

        var bottom = BuildBottomPanel();
        root.Controls.Add(masthead, 0, 0);
        root.Controls.Add(statusStack, 0, 1);
        root.Controls.Add(toolbar, 0, 2);
        root.Controls.Add(listFrame, 0, 3);
        root.Controls.Add(bottom, 0, 4);
        Controls.Add(root);

        search.TextChanged += (_, _) => RenderChats();
        list.ItemCheck += OnItemCheck;
        list.ItemChecked += (_, e) => { if (!rendering) Toggle((string)e.Item.Tag!, e.Item.Checked); };
        selectAll.Click += (_, _) => ToggleVisibleChats();
        clearMemories.CheckedChanged += (_, _) => RenderSelection();
        clearShared.CheckedChanged += (_, _) => RenderSelection();
        refresh.Click += async (_, _) => await Reload();
        repair.Click += async (_, _) => await PreviewRepair();
        preview.Click += async (_, _) => await PreviewCleanup();
        processTimer.Tick += async (_, _) => { if (!busy) SetProcess(await Task.Run(cleaner.CheckProcesses)); };
        Shown += async (_, _) => { ResizeChatColumn(); await Reload(); processTimer.Start(); };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "清理或修复正在进行，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(primary ? 16 : 11, 7, primary ? 16 : 11, 7);
        button.Margin = new Padding(0, 0, 8, 0);
        button.Font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(209, 216, 226);
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : Ink;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private static Panel BuildMasthead(Version version)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Surface, Padding = new Padding(2, 5, 2, 5), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(231, 239, 255), Margin = new Padding(0, 0, 10, 0) };
        var glyph = new Label { Dock = DockStyle.Fill, Text = "C", TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 15f, FontStyle.Bold), ForeColor = Accent };
        brand.Controls.Add(glyph);
        var text = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        text.Controls.Add(new Label { Text = "Codex 本地清理台", AutoSize = true, Location = new Point(0, 0), Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold), ForeColor = Ink });
        text.Controls.Add(new Label { Text = "筛选聊天，核对范围后再执行", AutoSize = true, Location = new Point(1, 24), Font = new Font("Microsoft YaHei UI", 8f), ForeColor = Muted });
        var badge = new Label
        {
            Text = $"v{version.Major}.{version.Minor}.{version.Build}", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Muted, BackColor = Color.FromArgb(242, 244, 247),
            Padding = new Padding(7, 2, 7, 2), Margin = new Padding(0, 2, 0, 2), Font = new Font("Segoe UI", 8f, FontStyle.Bold),
        };
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(text, 1, 0);
        header.Controls.Add(badge, 2, 0);
        return header;
    }

    private Control BuildToolbar()
    {
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Canvas, Padding = new Padding(0, 5, 0, 7), Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var searchPanel = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Margin = Padding.Empty };
        search.Dock = DockStyle.Fill;
        search.Margin = new Padding(0, 0, 8, 0);
        searchPanel.Controls.Add(search);
        var heading = new Panel { Dock = DockStyle.Fill, Width = 208, BackColor = Canvas, Margin = new Padding(0, 0, 10, 0) };
        heading.Controls.Add(new Label { Text = "本地聊天", AutoSize = true, Location = new Point(0, 0), Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), ForeColor = Ink });
        heading.Controls.Add(new Label { Text = "选择需要清理的会话", AutoSize = true, Location = new Point(1, 23), Font = new Font("Microsoft YaHei UI", 8f), ForeColor = Muted });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Canvas, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0) };
        actions.Controls.Add(selectAll);
        actions.Controls.Add(refresh);
        actions.Controls.Add(repair);
        toolbar.Controls.Add(heading, 0, 0);
        toolbar.Controls.Add(searchPanel, 1, 0);
        toolbar.Controls.Add(actions, 2, 0);
        return toolbar;
    }

    private Control BuildBottomPanel()
    {
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Canvas, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var summaryBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Canvas, Margin = Padding.Empty };
        summaryBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryBar.Controls.Add(summary, 0, 0);
        summaryBar.Controls.Add(selectionCount, 1, 0);

        var optionsCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Surface, Padding = new Padding(12, 9, 12, 8), Margin = new Padding(0, 0, 0, 7) };
        optionsCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        optionsCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        optionsCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        optionsCard.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        clearMemories.Margin = new Padding(0, 2, 4, 0);
        clearShared.Margin = new Padding(0, 2, 4, 0);
        optionsCard.Controls.Add(clearMemories, 0, 0);
        optionsCard.Controls.Add(new Label { Text = "合并记忆可能涵盖多条聊天", AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left, Font = new Font("Microsoft YaHei UI", 8f) }, 1, 0);
        optionsCard.Controls.Add(clearShared, 0, 1);
        optionsCard.Controls.Add(new Label { Text = "会影响所有聊天", AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left, Font = new Font("Microsoft YaHei UI", 8f) }, 1, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Canvas, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            Text = "所有改动都会先预览确认 · 项目、插件、设置和云端记录不受影响",
            AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8f),
        }, 0, 0);
        preview.Margin = new Padding(8, 1, 0, 0);
        footer.Controls.Add(preview, 1, 0);

        bottom.Controls.Add(summaryBar, 0, 0);
        bottom.Controls.Add(new Label { Text = "附加清理范围", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold), ForeColor = Muted, Margin = Padding.Empty }, 0, 1);
        bottom.Controls.Add(optionsCard, 0, 2);
        bottom.Controls.Add(footer, 0, 3);
        return bottom;
    }

    private IEnumerable<ChatInfo> VisibleChats()
    {
        var q = search.Text.Trim();
        return (state?.Chats ?? []).Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void ResizeChatColumn()
    {
        if (list.Columns.Count < 3) return;
        var remaining = list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 4;
        list.Columns[0].Width = Math.Max(240, remaining);
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

    private void ToggleVisibleChats()
    {
        var selectable = VisibleChats().Where(c => c.CanDelete).ToList();
        var clear = selectable.Count > 0 && selectable.All(c => selected.Contains(c.Id));
        foreach (var chat in selectable)
        {
            if (clear) selected.Remove(chat.Id);
            else selected.Add(chat.Id);
        }
        RenderChats();
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

    private void SetProcess(ProcessState process)
    {
        var ready = !process.Running;
        processBanner.BackColor = ready ? Color.FromArgb(237, 247, 240) : Color.FromArgb(255, 247, 232);
        processDot.ForeColor = ready ? Color.FromArgb(22, 142, 82) : Color.FromArgb(204, 116, 19);
        processTitle.Text = ready ? "Codex 已退出，可以执行清理" : "Codex 正在运行";
        processDetail.Text = ready
            ? $"数据目录：{cleaner.DataRoot}"
            : "检测到 Codex 相关进程 · 退出后才能执行；现在可以浏览和预览。";
        processTip.SetToolTip(processBanner, ready ? cleaner.DataRoot : string.Join(Environment.NewLine, process.Processes));
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
                ForeColor = chat.CanDelete ? Ink : Color.FromArgb(156, 163, 175),
            };
            list.Items.Add(item);
        }
        list.EndUpdate();
        rendering = false;
        emptyState.Text = search.TextLength == 0 ? "没有可显示的聊天记录" : "没有匹配的聊天\n试试缩短标题或 ID 关键词";
        emptyState.Visible = list.Items.Count == 0;
        ResizeChatColumn();
        RenderSelectAll();
        RenderSelection();
    }

    private void RenderSelectAll()
    {
        var selectable = VisibleChats().Where(c => c.CanDelete).ToList();
        var allSelected = selectable.Count > 0 && selectable.All(c => selected.Contains(c.Id));
        selectAll.Enabled = selectable.Count > 0 && !busy;
        selectAll.Text = allSelected ? "取消本页选择" : "全选本页";
    }

    private void RenderSelection()
    {
        var items = new List<string>();
        if (selected.Count > 0) items.Add($"{selected.Count} 个聊天");
        if (clearMemories.Checked) items.Add("全部本地记忆");
        if (clearShared.Checked) items.Add("共享输入历史与日志");
        summary.Text = items.Count > 0 ? "本次清理：" + string.Join("  +  ", items) : $"共 {state?.Chats.Count ?? 0} 条聊天，尚未选择清理内容";
        var visible = VisibleChats().ToList();
        var deletableCount = visible.Count(c => c.CanDelete);
        selectionCount.Text = $"当前显示 {visible.Count} 条 · 可选 {deletableCount} 条 · 已选 {visible.Count(c => selected.Contains(c.Id))} 条";
        var caps = state?.Capabilities;
        preview.Enabled = !busy && items.Count > 0 && caps is not null && (selected.Count == 0 || caps.DeleteChats) && caps.ClearData;
        repair.Enabled = !busy && caps?.RepairReferences == true;
        refresh.Enabled = search.Enabled = clearMemories.Enabled = clearShared.Enabled = list.Enabled = !busy;
        selectAll.Enabled = !busy && deletableCount > 0;
    }

    private Task PreviewCleanup() => Run(async () =>
    {
        var selection = new Selection([.. selected], clearMemories.Checked, clearShared.Checked);
        var plan = await Task.Run(() => cleaner.PlanCleanup(selection));
        if (!Confirm(plan)) return;
        var result = await Task.Run(() => cleaner.Execute(plan.Id));
        selected.Clear();
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
