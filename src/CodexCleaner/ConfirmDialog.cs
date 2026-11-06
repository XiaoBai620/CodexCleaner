using CodexCleaner.Engine;

namespace CodexCleaner;

internal sealed class ConfirmDialog : Form
{
    private static readonly Color Ink = Color.FromArgb(25, 39, 62);
    private static readonly Color Muted = Color.FromArgb(101, 116, 139);
    private static readonly Color Accent = Color.FromArgb(55, 100, 235);
    private static readonly Color Canvas = Color.FromArgb(244, 247, 251);
    private static readonly Color Danger = Color.FromArgb(190, 55, 64);
    private readonly Func<ProcessState> processState;
    private readonly DateTimeOffset expiresAt;
    private readonly Button confirm;
    private readonly Label gate = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private bool checking;

    public ConfirmDialog(Plan plan, Func<ProcessState> processState)
    {
        this.processState = processState;
        expiresAt = plan.ExpiresAt;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = plan.Repair ? "核对失效引用修复" : "核对清理范围";
        ClientSize = new Size(960, 680);
        MinimumSize = new Size(820, 580);
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(24, 20, 24, 16), Margin = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "02  /  核对并执行", AutoSize = true, ForeColor = Accent, Font = new Font(Font.FontFamily, 9f, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        heading.Controls.Add(new Label { Text = plan.Repair ? "让本地引用恢复整洁" : "确认这次要清理的内容", AutoSize = true, Font = new Font(Font.FontFamily, 19f, FontStyle.Bold), Margin = Padding.Empty }, 0, 1);
        heading.Controls.Add(new Label { Text = "展开左侧明细，右侧可查看并复制完整说明。", AutoSize = true, ForeColor = Muted, Margin = Padding.Empty }, 0, 2);
        root.Controls.Add(heading, 0, 0);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 10, 0, 8) };
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        metrics.Controls.Add(Metric(plan.Repair ? "待修复聊天 ID" : "已选聊天", plan.Repair ? plan.Selection.ChatIds.Count : plan.Chats.Count), 0, 0);
        metrics.Controls.Add(Metric("涉及数据库记录", plan.DatabaseRows), 1, 0);
        metrics.Controls.Add(Metric("删除 / 更新的文件", plan.Files.Count), 2, 0);
        root.Controls.Add(metrics, 0, 1);

        var recordOnly = plan.Chats.Where(c => c.CanRemoveRecord).ToList();
        var notice = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(12, 9, 12, 9), Margin = new Padding(0, 2, 0, 10),
            BackColor = recordOnly.Count > 0 ? Color.FromArgb(255, 244, 222) : Color.FromArgb(234, 240, 253),
            ForeColor = recordOnly.Count > 0 ? Color.FromArgb(146, 88, 20) : Ink,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = recordOnly.Count > 0
                ? $"其中 {recordOnly.Count} 条仅清理本地记录与关联数据，原会话文件会保留。\n这些文件以后可能被重新导入；请查看“保留的会话文件”。"
                : plan.Repair ? "仅修复已失效的本机引用，保留有效聊天和项目。" : "确认后会直接删除所列内容，本工具不提供撤销。请核对下方的附加清理范围。",
        };
        root.Controls.Add(notice, 0, 2);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        var tree = new TreeView
        {
            Dock = DockStyle.Fill, ShowNodeToolTips = true, BackColor = Color.White, ForeColor = Ink,
            BorderStyle = BorderStyle.None, FullRowSelect = true, HideSelection = false,
            ItemHeight = 30, Indent = 18, Font = Font, AccessibleName = "清理项目明细",
        };
        var detail = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Color.White, ForeColor = Ink, Font = Font, AccessibleName = "所选项目完整说明",
        };
        var treeFrame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10), Margin = new Padding(0, 0, 10, 0) };
        treeFrame.Controls.Add(tree);
        var detailFrame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(16), Margin = Padding.Empty };
        detailFrame.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        detailFrame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailFrame.Controls.Add(new Label { Text = "完整说明", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Muted, Margin = Padding.Empty }, 0, 0);
        detailFrame.Controls.Add(detail, 0, 1);
        body.Controls.Add(treeFrame, 0, 0);
        body.Controls.Add(detailFrame, 1, 0);
        root.Controls.Add(body, 0, 3);

        TreeNode Section(string title, IEnumerable<(string Label, string Detail)> entries, bool expand = false)
        {
            var items = entries.ToList();
            var node = new TreeNode($"{title}  ·  {items.Count}") { Tag = $"{title}\r\n\r\n" + (items.Count == 0 ? "本次没有涉及此类内容。" : string.Join("\r\n\r\n", items.Select(i => i.Detail))) };
            foreach (var item in items) node.Nodes.Add(new TreeNode(item.Label) { Tag = item.Detail, ToolTipText = item.Detail });
            tree.Nodes.Add(node);
            if (expand) node.Expand();
            return node;
        }

        var scope = new List<(string, string)>
        {
            (plan.Repair ? "修复失效引用" : $"清理 {plan.Chats.Count} 条聊天", plan.Repair ? "清除已不存在的本机聊天引用。现有聊天不在本次修复范围。" : $"已选择 {plan.Chats.Count} 条聊天，其中 {recordOnly.Count} 条仅清理记录。\r\n\r\n清理包括相应本地数据库、索引和已识别的关联数据。"),
            (plan.Selection.ClearMemories ? "全部本地记忆：清空" : "全部本地记忆：保留", plan.Selection.ClearMemories ? "已主动选择清空全部本地记忆。该操作可能影响所有聊天产生的合并记忆。" : "未选择全局记忆清空。删除聊天时仍会清理与该聊天直接关联的记忆记录。"),
            (plan.Selection.ClearSharedHistory ? "共享历史与日志：清空" : "共享历史与日志：保留", plan.Selection.ClearSharedHistory ? "已主动选择清空共享输入历史与日志。这会影响所有聊天。" : "未选择全局清空；仅处理所选聊天已识别的关联记录。"),
        };
        var overview = Section("本次范围", scope, true);
        if (!plan.Repair)
            Section("选中的聊天", plan.Chats.Select(c => (c.Title + (c.CanRemoveRecord ? " · 仅记录" : ""), $"{c.Title}\r\n\r\n聊天 ID\r\n{c.Id}\r\n\r\n处理方式\r\n{(c.CanRemoveRecord ? "仅清理本地记录与关联数据，保留原会话文件。" : c.RolloutMissing ? "原会话文件已缺失，清理剩余记录。" : "清理聊天记录及经过验证的会话文件。")}\r\n\r\n原会话路径\r\n{(string.IsNullOrWhiteSpace(c.OriginalPath) ? c.RelativePath ?? "未提供会话文件路径" : c.OriginalPath)}")));
        else Section("失效的聊天 ID", plan.Selection.ChatIds.Select(id => (id, id)));
        if (recordOnly.Count > 0)
            Section("保留的会话文件", recordOnly.Select(c => (c.Title, $"{c.Title}\r\n\r\n{c.BlockedReason}\r\n\r\n原路径\r\n{(string.IsNullOrWhiteSpace(c.OriginalPath) ? "未提供会话文件路径" : c.OriginalPath)}\r\n\r\n本次不会按此路径读取、改写或删除会话文件。若文件被 Codex 再次导入，该记录可能重新出现。")), true);
        Section("数据库变更", plan.Databases.Select(d => (d.Path + $" · {d.RowCount} 条", $"{d.Path}\r\n\r\n" + string.Join("\r\n", d.Tables.Select(t => $"{t.Name}：{t.Rows} 条{(t.ClearsReference ? "（解除关联，保留记录）" : "")}")))));
        Section("文件变更", plan.Files.Select(f => ((f.Remove ? "删除 · " : "更新 · ") + Path.GetFileName(f.Path), $"{f.Path}\r\n\r\n操作：{(f.Remove ? "删除文件" : "更新关联记录")}\r\n{f.Purpose}")));
        Section("清理说明", plan.Warnings.Select((w, i) => ($"说明 {i + 1}", w)));
        tree.AfterSelect += (_, e) => detail.Text = e.Node?.Tag as string ?? "";
        tree.SelectedNode = overview;

        confirm = ActionButton(plan.Repair ? "确认修复" : "确认清理", plan.Repair ? Accent : Danger, Color.White);
        confirm.DialogResult = DialogResult.OK;
        confirm.Enabled = false;
        var cancel = ActionButton("返回调整", Color.White, Ink);
        cancel.DialogResult = DialogResult.Cancel;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gate.Text = "正在检查执行条件…";
        gate.Font = new Font(Font.FontFamily, 8.5f);
        footer.Controls.Add(gate, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(confirm, 2, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        AcceptButton = null;
        CancelButton = cancel;
        ActiveControl = cancel;

        timer.Tick += async (_, _) => await UpdateGate();
        Shown += async (_, _) => { await UpdateGate(); if (!IsDisposed) timer.Start(); };
        FormClosed += (_, _) => timer.Stop();
    }

    private static Control Metric(string label, long count)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, 8, 0) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Muted }, 0, 0);
        card.Controls.Add(new Label { Text = count.ToString("N0"), AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Ink, Font = new Font("Segoe UI", 22f, FontStyle.Bold) }, 1, 0);
        return card;
    }

    private static Button ActionButton(string title, Color back, Color fore)
    {
        var button = new Button
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold), Padding = new Padding(18, 10, 18, 10),
            Margin = new Padding(10, 0, 0, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = back == Color.White ? 1 : 0;
        button.FlatAppearance.BorderColor = Color.FromArgb(218, 225, 235);
        return button;
    }

    private async Task UpdateGate()
    {
        if (checking || IsDisposed) return;
        checking = true;
        try
        {
            var process = await Task.Run(processState);
            if (IsDisposed) return;
            var expired = DateTimeOffset.Now >= expiresAt;
            var running = process.Running || process.Processes.Count > 0;
            confirm.Enabled = !expired && !running;
            gate.Text = expired ? "预览已过期，请返回重新预览。"
                : running ? "请先退出 Codex，退出后将自动允许确认。"
                : $"可以执行 · 此预览有效至 {expiresAt.ToLocalTime():HH:mm}";
            gate.ForeColor = expired || running ? Color.FromArgb(152, 94, 29) : Color.FromArgb(29, 123, 90);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (IsDisposed) return;
            confirm.Enabled = false;
            gate.Text = "暂时无法确认 Codex 状态，请稍后重试。";
            gate.AccessibleDescription = e.Message;
            gate.ForeColor = Danger;
        }
        finally { checking = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
