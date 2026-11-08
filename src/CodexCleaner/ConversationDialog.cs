using CodexCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace CodexCleaner;

// Read-only transcript window, ported from DevinCleaner.
internal sealed class ConversationDialog : Form
{
    private readonly ChatView chat = new() { Dock = DockStyle.Fill };
    private readonly Label meta = DesktopTheme.Label("正在读取…", 8.5f);
    private readonly CheckBox thinking = new PillToggle("思考摘要") { Enabled = false };
    private readonly CheckBox system = new PillToggle("系统消息") { Enabled = false };
    private readonly Button copyAll = DesktopTheme.Button("复制全部");

    public ConversationDialog(Cleaner cleaner, ChatInfo session)
    {
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"对话 · {session.Title}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Size = new Size(1000, 800);
        MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        ShowInTaskbar = false;
        KeyPreview = true;

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 4, RowCount = 2, Padding = new Padding(20, 10, 16, 8), BackColor = Color.White };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var title = DesktopTheme.Label(session.Title, 11f, true);
        title.Dock = DockStyle.Fill;
        title.AutoEllipsis = true;
        title.TextAlign = ContentAlignment.MiddleLeft;
        meta.Dock = DockStyle.Fill;
        meta.AutoEllipsis = true;
        meta.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(meta, 0, 1);
        var col = 1;
        foreach (var c in new Control[] { thinking, system, copyAll })
        {
            c.Anchor = AnchorStyles.Right;
            c.Margin = new Padding(8, 0, 0, 0);
            header.Controls.Add(c, col++, 0);
            header.SetRowSpan(c, 2);
        }
        copyAll.Enabled = false;
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = DesktopTheme.Border };

        Controls.Add(chat);
        Controls.Add(divider);
        Controls.Add(header);

        var tip = new ToolTip { AutoPopDelay = 15000 };
        tip.SetToolTip(chat, "拖动选择文字，Ctrl+C 复制 · 双击选中整条 · 点击工具调用行展开命令和输出 · 右键更多");
        tip.SetToolTip(copyAll, "按当前显示设置复制整段对话为纯文本");
        tip.SetToolTip(thinking, "显示 Codex 记录的思考摘要");
        tip.SetToolTip(system, "显示上下文压缩等系统事件");
        thinking.CheckedChanged += (_, _) => chat.ShowThinking = thinking.Checked;
        system.CheckedChanged += (_, _) => chat.ShowSystem = system.Checked;
        copyAll.Click += (_, _) => ChatView.Copy(chat.Transcript());
        chat.OpenFull += ShowFull;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        FormClosed += (_, _) => tip.Dispose();

        Shown += async (_, _) =>
        {
            chat.Focus();
            try
            {
                var messages = await Task.Run(() => cleaner.Conversation(session.Id));
                if (IsDisposed) return;
                chat.SetMessages(messages);
                var users = messages.Count(m => m.Role == ChatRole.User);
                var tools = messages.Where(m => m.Role == ChatRole.Assistant).Sum(m => m.ToolCalls.Count);
                meta.Text = $"{session.UpdatedAt?.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {users} 次提问 · {tools} 次工具调用  ·  只读";
                tip.SetToolTip(meta, meta.Text);
                if (users == 0 && chat.TurnCount == 0) chat.Placeholder = "这个聊天没有可显示的对话记录。";
                thinking.Enabled = system.Enabled = copyAll.Enabled = true;
            }
            catch (Exception e) when (e is CleanerException or SqliteException or IOException or System.Text.Json.JsonException)
            {
                if (IsDisposed) return;
                meta.Text = "读取失败";
                chat.Placeholder = $"无法读取对话：{e.Message}";
            }
        };
    }

    private void ShowFull(string title, string content)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(860, 620), MinimumSize = new Size(480, 320), Font = Font, BackColor = Color.White, MinimizeBox = false, ShowInTaskbar = false, ShowIcon = false, Padding = new Padding(16) };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, BorderStyle = BorderStyle.None, MaxLength = 0, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Consolas", 10f), Text = ChatView.Crlf(content) };
        dialog.Controls.Add(text);
        dialog.KeyPreview = true;
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) dialog.Close(); };
        dialog.Shown += (_, _) => { text.SelectionStart = 0; text.SelectionLength = 0; };
        dialog.ShowDialog(this);
    }
}
