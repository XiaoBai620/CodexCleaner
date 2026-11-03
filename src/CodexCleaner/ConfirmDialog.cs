using CodexCleaner.Engine;

namespace CodexCleaner;

internal sealed class ConfirmDialog : Form
{
    private static readonly Color Ink = Color.FromArgb(31, 41, 55);
    private static readonly Color Muted = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Danger = Color.FromArgb(185, 55, 55);
    private readonly Func<ProcessState> processState;
    private readonly Button confirm;
    private readonly Label gate = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };

    public ConfirmDialog(Plan plan, Func<ProcessState> processState)
    {
        this.processState = processState;
        Text = plan.Repair ? "修复失效引用" : "确认清理范围";
        Size = new Size(760, 500);
        MinimumSize = new Size(560, 380);
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var tree = new TreeView { Dock = DockStyle.Fill, ShowNodeToolTips = true, BackColor = Color.White, ForeColor = Ink, BorderStyle = BorderStyle.None, FullRowSelect = true, ItemHeight = 21, Font = new Font("Microsoft YaHei UI", 9f), Padding = new Padding(8, 5, 8, 5) };
        TreeNode Section(string title, IEnumerable<string> items, bool expand)
        {
            var node = new TreeNode($"{title} · {items.Count()}");
            foreach (var item in items) node.Nodes.Add(new TreeNode(item) { ToolTipText = item });
            if (expand) node.Expand();
            return node;
        }
        if (!plan.Repair) tree.Nodes.Add(Section("选中的聊天", plan.Chats.Select(c => $"{c.Title}（{c.Id}）"), expand: true));
        if (plan.Repair) tree.Nodes.Add(Section("失效的聊天 ID", plan.Selection.ChatIds, expand: false));
        tree.Nodes.Add(Section("涉及的数据库", plan.Databases.Select(d =>
            $"{d.Path} · {d.RowCount} 条 · " + string.Join("；", d.Tables.Select(t => $"{t.Name}{(t.ClearsReference ? "（解除关联，保留记录）" : "")}: {t.Rows}"))), expand: false));
        tree.Nodes.Add(Section("涉及的文件", plan.Files.Select(f => $"{f.Path} · {(f.Remove ? "删除" : "更新关联记录")}{(f.Purpose.Length > 0 ? " · " + f.Purpose : "")}"), expand: false));
        tree.Nodes.Add(Section("清理说明", plan.Warnings, expand: true));

        var parts = new List<string>();
        if (plan.Repair) parts.Add($"{plan.ReferenceRepairs} 处界面引用");
        else
        {
            parts.Add($"{plan.Chats.Count} 个聊天");
            if (plan.Selection.ClearMemories) parts.Add("全部本地记忆");
            if (plan.Selection.ClearSharedHistory) parts.Add("共享输入历史与日志");
        }
        if (plan.DatabaseRows > 0) parts.Add($"{plan.DatabaseRows} 条数据库记录");
        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 12, 16, 12), BackColor = Color.White, Margin = Padding.Empty };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.Controls.Add(new Label
        {
            Text = plan.Repair ? "请核对修复范围" : "请核对清理范围",
            AutoSize = true, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 5),
        }, 0, 0);
        head.Controls.Add(new Label
        {
            Text = string.Join("  +  ", parts),
            AutoSize = true, ForeColor = Accent, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 5),
        }, 0, 1);
        head.Controls.Add(new Label
        {
            Text = (plan.Repair
                ? "清除项目迁移、聊天列表和归档索引中已失效的本机聊天引用，保留有效聊天和项目。"
                : "确认后将直接删除所选内容，此操作无法通过本工具撤销。")
                + Environment.NewLine + $"预览有效至 {plan.ExpiresAt.ToLocalTime():HH:mm}；数据发生变化时需要重新预览。",
            AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = plan.Repair ? Muted : Danger, Font = new Font("Microsoft YaHei UI", 8.5f), Margin = Padding.Empty,
        }, 0, 2);

        confirm = new Button { Text = plan.Repair ? "确认修复" : "确认删除", AutoSize = true, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = plan.Repair ? Accent : Danger, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), Padding = new Padding(13, 7, 13, 7), UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        confirm.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", 9f), Padding = new Padding(12, 7, 12, 7), UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        cancel.FlatAppearance.BorderSize = 1;
        cancel.FlatAppearance.BorderColor = Color.FromArgb(209, 216, 226);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12), BackColor = Color.FromArgb(246, 248, 251) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(gate, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(confirm, 2, 0);

        Controls.Add(tree);
        Controls.Add(head);
        Controls.Add(footer);
        AcceptButton = null;
        CancelButton = cancel;
        ActiveControl = cancel;

        timer.Tick += (_, _) => UpdateGate();
        Shown += (_, _) => { UpdateGate(); timer.Start(); };
        FormClosed += (_, _) => timer.Dispose();
    }

    private void UpdateGate()
    {
        var state = processState();
        confirm.Enabled = !state.Running;
        gate.Text = state.Running ? "请先完全退出 Codex，状态会自动更新。" : "Codex 已退出，可以确认执行。";
        gate.ForeColor = state.Running ? Color.FromArgb(170, 90, 0) : Color.FromArgb(20, 120, 50);
    }
}
