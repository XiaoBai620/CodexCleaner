using System.Diagnostics;
using CodexCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace CodexCleaner;

internal sealed class BackupDialog : Form
{
    private readonly Cleaner cleaner;
    private readonly ListBox backupList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "备份列表" };
    private readonly CheckedListBox chatList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, HorizontalScrollbar = true, CheckOnClick = true, AccessibleName = "备份中的聊天" };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, AccessibleName = "备份说明与限制" };
    private readonly CheckBox memories = new() { Text = "恢复全部记忆", AutoSize = true, ForeColor = DesktopTheme.Ink, AccessibleName = "恢复本次清理的全部记忆" };
    private readonly CheckBox shared = new() { Text = "恢复共享历史与日志", AutoSize = true, ForeColor = DesktopTheme.Ink, AccessibleName = "恢复本次清理的共享历史与日志" };
    private readonly Label status = DesktopTheme.Label("");
    private readonly Button selectAll = DesktopTheme.Button("全选可恢复聊天");
    private readonly Button refresh = DesktopTheme.Button("刷新");
    private readonly Button openFolder = DesktopTheme.Button("打开备份目录");
    private readonly Button remove = DesktopTheme.Button("删除此备份");
    private readonly Button recover = DesktopTheme.Button("处理未完成清理");
    private readonly Button restore = DesktopTheme.Button("预览恢复", primary: true);
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 500 };
    private readonly Stopwatch busyClock = new();
    private IReadOnlyList<BackupInfo> items = [];
    private bool busy, rendering;
    private int operation;
    private string activity = "";

    public bool Changed { get; private set; }

    public BackupDialog(Cleaner cleaner)
    {
        this.cleaner = cleaner;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "备份与恢复";
        ShowIcon = false;
        ClientSize = new Size(1020, 660);
        MinimumSize = new Size(860, 580);
        BackColor = DesktopTheme.Canvas;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        backupList.BackColor = chatList.BackColor = details.BackColor = DesktopTheme.Canvas;
        backupList.ForeColor = chatList.ForeColor = details.ForeColor = DesktopTheme.Ink;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(20, 16, 20, 14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var heading = DesktopTheme.Label("删除前保留的备份", 10f, true);
        heading.Dock = DockStyle.Fill;
        heading.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(heading, 0, 0);
        var rightHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        rightHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var chatsHeading = DesktopTheme.Label("选择要恢复的聊天", 10f, true);
        chatsHeading.Dock = DockStyle.Fill;
        chatsHeading.TextAlign = ContentAlignment.MiddleLeft;
        selectAll.Anchor = AnchorStyles.Right;
        rightHeader.Controls.Add(chatsHeading, 0, 0);
        rightHeader.Controls.Add(selectAll, 1, 0);
        root.Controls.Add(rightHeader, 1, 0);
        root.Controls.Add(Frame(backupList, new Padding(0, 0, 12, 0)), 0, 1);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.Controls.Add(Frame(chatList, Padding.Empty), 0, 0);
        var extras = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
        memories.Margin = new Padding(0, 0, 16, 0);
        shared.Margin = Padding.Empty;
        extras.Controls.AddRange([memories, shared]);
        right.Controls.Add(extras, 0, 1);
        root.Controls.Add(right, 1, 1);
        root.Controls.Add(Frame(details, new Padding(0, 12, 0, 0)), 0, 2);
        root.SetColumnSpan(root.GetControlFromPosition(0, 2)!, 2);
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        status.AccessibleName = "备份操作状态";
        root.Controls.Add(status, 0, 3);
        root.SetColumnSpan(status, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        foreach (var button in new[] { restore, recover, remove, openFolder, refresh })
        {
            button.Margin = new Padding(8, 0, 0, 0);
            actions.Controls.Add(button);
        }
        root.Controls.Add(actions, 0, 4);
        root.SetColumnSpan(actions, 2);
        Controls.Add(root);

        backupList.SelectedIndexChanged += (_, _) => RenderChats();
        chatList.SelectedIndexChanged += (_, _) => RenderDetails();
        chatList.ItemCheck += (_, e) =>
        {
            if (!rendering && (busy || !Restorable().Contains(e.Index))) e.NewValue = CheckState.Unchecked;
            if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { if (!IsDisposed) RenderButtons(); });
        };
        memories.CheckedChanged += (_, _) => RenderButtons();
        shared.CheckedChanged += (_, _) => RenderButtons();
        selectAll.Click += (_, _) => ToggleAll();
        refresh.Click += async (_, _) => await HandleOperation(() => Reload(Current?.Path));
        restore.Click += async (_, _) => await HandleOperation(() => Restore(false));
        recover.Click += async (_, _) => await HandleOperation(() => Restore(true));
        remove.Click += async (_, _) => await HandleOperation(DeleteBackup);
        openFolder.Click += async (_, _) => await HandleOperation(() =>
        {
            if (!Directory.Exists(cleaner.BackupRoot)) throw new CleanerException("备份目录尚未创建；首次清理完成备份后即可打开。");
            Process.Start(new ProcessStartInfo(cleaner.BackupRoot) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        busyTimer.Tick += (_, _) => RenderActivity();
        Shown += async (_, _) => await HandleOperation(() => Reload());
        FormClosing += (_, e) => e.Cancel = busy;
        RenderButtons();
    }

    private static Control Frame(Control inner, Padding margin)
    {
        var frame = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = margin, CornerRadius = 12 };
        frame.Controls.Add(inner);
        return frame;
    }

    private BackupInfo? Current => backupList.SelectedIndex >= 0 && backupList.SelectedIndex < items.Count ? items[backupList.SelectedIndex] : null;

    private async Task HandleOperation(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or SqliteException or System.ComponentModel.Win32Exception)
        {
            if (IsDisposed) return;
            status.Text = "操作未完成，请查看具体原因。";
            status.ForeColor = DesktopTheme.Warning;
            details.Text = e.Message;
            MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task Run(string label, Func<IProgress<string>, Task> work)
    {
        if (busy) return;
        busy = true;
        var current = ++operation;
        activity = label;
        busyClock.Restart();
        busyTimer.Start();
        UseWaitCursor = true;
        RenderButtons();
        var progress = new Progress<string>(value =>
        {
            if (!busy || current != operation || IsDisposed) return;
            activity = value;
            RenderActivity();
        });
        try { await work(progress); }
        finally
        {
            busyTimer.Stop();
            busyClock.Stop();
            busy = false;
            if (!IsDisposed) { UseWaitCursor = false; RenderButtons(); }
        }
    }

    private void RenderActivity()
    {
        if (!busy) return;
        status.Text = $"{activity}…  {(int)busyClock.Elapsed.TotalSeconds} 秒";
        status.ForeColor = DesktopTheme.Muted;
    }

    private async Task Reload(string? keep = null)
    {
        IReadOnlyList<BackupInfo> loaded = [];
        await Run("正在读取备份", async _ => loaded = await Task.Run(cleaner.Backups));
        if (IsDisposed) return;
        items = loaded;
        backupList.BeginUpdate();
        backupList.Items.Clear();
        foreach (var backup in items)
            backupList.Items.Add($"{backup.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {backup.Chats.Count} 条 · {MainForm.FormatBytes(backup.Bytes)}" + (backup.NeedsRecovery ? " · 未完成" : backup.Problem is not null ? " · 无法恢复" : ""));
        backupList.EndUpdate();
        if (items.Count > 0) backupList.SelectedIndex = Math.Max(0, items.ToList().FindIndex(b => b.Path == keep));
        RenderChats();
    }

    private void RenderChats()
    {
        rendering = true;
        try
        {
            chatList.BeginUpdate();
            chatList.Items.Clear();
            memories.Checked = shared.Checked = false;
            if (Current is { } backup)
                foreach (var chat in backup.Chats)
                    chatList.Items.Add($"{chat.Title} · {chat.Messages:N0} 条消息" + (chat.BlockedReason is { } reason ? $"（{reason}）" : ""));
            chatList.EndUpdate();
        }
        finally { rendering = false; }
        RenderDetails();
        RenderButtons();
    }

    private List<int> Restorable() => Current is { Problem: null, NeedsRecovery: false } backup
        ? backup.Chats.Select((chat, index) => (chat, index)).Where(x => x.chat.BlockedReason is null).Select(x => x.index).ToList() : [];

    private void ToggleAll()
    {
        if (busy) return;
        var targets = Restorable();
        var check = !targets.All(chatList.GetItemChecked);
        chatList.BeginUpdate();
        foreach (var index in targets) chatList.SetItemChecked(index, check);
        chatList.EndUpdate();
        RenderButtons();
    }

    private void RenderDetails()
    {
        if (Current is not { } backup)
        {
            details.Text = $"备份目录：{cleaner.BackupRoot}\r\n删除前自动保存，清理完成后保留；可在此选择聊天恢复或删除旧备份。";
            return;
        }
        var lines = new List<string>
        {
            $"{backup.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {backup.Chats.Count} 条聊天 · 占用 {MainForm.FormatBytes(backup.Bytes)} · {(backup.Live ? "运行中清理" : "退出后清理")}",
            "恢复前请退出 Codex。恢复聊天、会话文件和对应索引；置顶、草稿及窗口状态不会自动恢复。",
        };
        if (backup.NeedsRecovery) lines.Add("这次清理尚未完成。请使用“处理未完成清理”核对并补回已移除的数据；此备份暂不可删除。");
        if (backup.ClearMemories || backup.ClearSharedHistory) lines.Add("附加清理内容需要单独勾选，按整组恢复；不会随聊天选择自动恢复。");
        if (backup.Problem is { } problem) lines.Add(problem);
        if (chatList.SelectedIndex is var index && index >= 0 && index < backup.Chats.Count)
        {
            var chat = backup.Chats[index];
            lines.Add($"当前聊天：{chat.Title}\r\n{chat.Id}");
            if (chat.BlockedReason is { } reason) lines.Add(reason);
        }
        lines.Add(backup.Path);
        details.Text = string.Join("\r\n", lines);
    }

    private void RenderButtons()
    {
        if (rendering) return;
        var backup = Current;
        var targets = Restorable();
        var count = targets.Count(chatList.GetItemChecked);
        var available = !busy && backup is { Problem: null, NeedsRecovery: false };
        backupList.Enabled = chatList.Enabled = refresh.Enabled = openFolder.Enabled = !busy;
        memories.Enabled = available && backup!.ClearMemories;
        shared.Enabled = available && backup!.ClearSharedHistory;
        restore.Enabled = available && (count > 0 || memories.Checked || shared.Checked);
        recover.Enabled = !busy && backup is { Problem: null, NeedsRecovery: true };
        remove.Enabled = !busy && backup is { NeedsRecovery: false };
        selectAll.Enabled = !busy && targets.Count > 0;
        selectAll.Text = targets.Count > 0 && count == targets.Count ? "取消全选" : "全选可恢复聊天";
        if (busy) { RenderActivity(); return; }
        status.Text = items.Count == 0 ? "还没有备份。"
            : backup?.Problem is not null ? "此备份暂时无法恢复，请查看具体原因。"
            : backup?.NeedsRecovery == true ? "请先退出 Codex，再处理未完成清理。"
            : $"已勾选 {count} 条可恢复聊天 · 已存在的聊天会跳过";
        status.ForeColor = backup?.Problem is not null || backup?.NeedsRecovery == true ? DesktopTheme.Warning : DesktopTheme.Muted;
    }

    private async Task Restore(bool incomplete)
    {
        if (Current is not { } backup) return;
        var ids = incomplete ? backup.Chats.Select(c => c.Id).ToList() : Restorable().Where(chatList.GetItemChecked).Select(i => backup.Chats[i].Id).ToList();
        var restoreMemories = incomplete ? backup.ClearMemories : memories.Checked;
        var restoreShared = incomplete ? backup.ClearSharedHistory : shared.Checked;
        BackupRestorePreview preview = null!;
        await Run("正在核对恢复范围", async _ => preview = await Task.Run(() => cleaner.PreviewRestore(backup.Path, ids, restoreMemories, restoreShared, incomplete)));
        if (!ConfirmPreview(preview)) return;
        BackupRestoreResult result = null!;
        await Run(incomplete ? "正在处理未完成清理" : "正在恢复", async progress => result = await Task.Run(() => incomplete
            ? cleaner.RecoverBackup(backup.Path, progress)
            : cleaner.Restore(backup.Path, ids, restoreMemories, restoreShared, progress)));
        Changed = true;
        await Reload(backup.Path);
        var groups = new List<string>();
        if (result.RestoredMemories) groups.Add("全部记忆");
        if (result.RestoredSharedHistory) groups.Add("共享历史与日志");
        var text = $"已恢复 {result.Restored} 条聊天" + (result.Skipped.Count > 0 ? $"，跳过 {result.Skipped.Count} 条已存在的聊天" : "") + "。";
        if (groups.Count > 0) text += "\n已恢复：" + string.Join("、", groups) + "。";
        text += "\n备份继续保留，可在不再需要时手动删除。";
        MessageBox.Show(this, text, "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool ConfirmPreview(BackupRestorePreview preview)
    {
        using var dialog = new Form
        {
            Text = preview.RecoverIncomplete ? "处理未完成清理" : "确认恢复范围", ShowIcon = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(720, 500), MinimumSize = new Size(600, 400),
            Font = Font, BackColor = DesktopTheme.Canvas, MinimizeBox = false, MaximizeBox = false,
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(20) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        var lines = new List<string>
        {
            $"将恢复 {preview.Chats.Count} 条聊天、{preview.FileCount} 个文件（{MainForm.FormatBytes(preview.Bytes)}）。",
            "Codex 必须保持退出。已存在聊天不会覆盖；置顶、草稿和窗口状态不自动恢复。",
        };
        if (preview.RecoverIncomplete) lines.Add("这是未完成清理的故障恢复，会核对并补回本次操作移除的数据。");
        if (preview.RestoreMemories) lines.Add("附加范围：全部记忆。");
        if (preview.RestoreSharedHistory) lines.Add("附加范围：共享历史与日志。");
        lines.AddRange(preview.Warnings);
        lines.Add("");
        lines.AddRange(preview.Chats.Select(chat => $"• {chat.Title}（{chat.Id}）"));
        if (preview.Skipped.Count > 0) lines.Add($"\r\n跳过 {preview.Skipped.Count} 条已存在聊天：\r\n{string.Join("\r\n", preview.Skipped)}");
        var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Text = string.Join("\r\n", lines), BackColor = DesktopTheme.Canvas, ForeColor = DesktopTheme.Ink, AccessibleName = "恢复范围" };
        root.Controls.Add(content, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var confirm = DesktopTheme.Button("确认恢复", primary: true);
        confirm.DialogResult = DialogResult.OK;
        var cancel = DesktopTheme.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0, 0, 8, 0);
        actions.Controls.AddRange([confirm, cancel]);
        root.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(root);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private async Task DeleteBackup()
    {
        if (Current is not { NeedsRecovery: false } backup) return;
        if (MessageBox.Show(this, $"永久删除 {backup.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 的备份？\n包含 {backup.Chats.Count} 条聊天，占用 {MainForm.FormatBytes(backup.Bytes)}。删除后无法再从此备份恢复。\n\n{backup.Path}", "删除备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await Run("正在删除备份", _ => Task.Run(() => cleaner.DeleteBackup(backup.Path)));
        await Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { busyTimer.Dispose(); busyClock.Stop(); }
        base.Dispose(disposing);
    }
}
