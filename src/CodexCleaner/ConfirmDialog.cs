using CodexCleaner.Core;

namespace CodexCleaner;

internal sealed class ConfirmDialog : Form
{
    private readonly Func<ProcessState> processState;
    private readonly Button confirm;
    private readonly Label gate = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };

    public ConfirmDialog(Plan plan, Func<ProcessState> processState)
    {
        this.processState = processState;
        Text = plan.Repair ? "修复失效引用" : "确认清理范围";
        Size = new Size(760, 560);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var tree = new TreeView { Dock = DockStyle.Fill, ShowNodeToolTips = true };
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
        var head = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 8), MaximumSize = new Size(720, 0),
            Text = string.Join(" + ", parts) + Environment.NewLine + (plan.Repair
                ? "清除项目迁移、聊天列表和归档索引中已失效的本机聊天引用，保留有效聊天和项目。"
                : "确认后直接删除所选内容，无法通过本工具撤销。")
                + Environment.NewLine + $"预览有效至 {plan.ExpiresAt.ToLocalTime():HH:mm}；数据发生变化时需要重新预览。",
            ForeColor = plan.Repair ? SystemColors.ControlText : Color.FromArgb(170, 30, 30),
        };

        confirm = new Button { Text = plan.Repair ? "确认修复" : "确认删除", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
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
