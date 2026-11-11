using System.ComponentModel;
using System.Net.Http;

namespace CodexCleaner;

internal sealed class UpdateDialog : Form
{
    private readonly AvailableUpdate update;
    private readonly IAppUpdater updater;
    private readonly IUpdateInstaller installer;
    private readonly Action<PreparedUpdate> discard;
    private CancellationTokenSource? downloadCancellation;
    private readonly Button install = DesktopTheme.Button("下载并重启", primary: true);
    private readonly Button cancel = DesktopTheme.Button("稍后");
    private readonly Button manual = DesktopTheme.Button("手动下载");
    private readonly Button errorDetails = DesktopTheme.Button("错误详情");
    private readonly Label status = DesktopTheme.Label("准备就绪", 9f);
    private readonly ProgressBar progress = new ThemedProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Visible = false };
    private bool working, installing, closeAfterCancel, handedOff, preservePrepared;
    private int attemptNumber;
    private string fullError = "";

    internal UpdateDialog(AvailableUpdate update, Action<Uri> openReleasePage, IAppUpdater? updater = null,
        IUpdateInstaller? installer = null, Action<PreparedUpdate>? discard = null)
    {
        this.update = update;
        this.updater = updater ?? new AppUpdater();
        this.installer = installer ?? new UpdateInstaller();
        this.discard = discard ?? AppUpdater.Discard;
        Text = "更新 CodexCleaner";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(680, 560);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DesktopTheme.Canvas;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimizeBox = MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(24), BackColor = DesktopTheme.Canvas };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = DesktopTheme.Label($"更新至 v{update.Version}", 14f, true);
        heading.Dock = DockStyle.Fill;
        var version = DesktopTheme.Label($"当前版本 v{UpdateChecker.CurrentVersion}" +
            (update.Package is { } package ? $"  ·  下载大小 {package.Size / 1048576d:0.0} MB" : ""), 9f);
        version.Dock = DockStyle.Fill;
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(version, 0, 1);
        layout.Controls.Add(header, 0, 0);
        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font(Font.FontFamily, 9.5f),
            AccessibleName = "本次更新内容，可选择并复制", Margin = Padding.Empty,
        };
        SetReleaseNotes(notes, update.Notes);
        layout.Controls.Add(ReadingSurface(notes), 0, 1);
        var detail = DesktopTheme.Label(update.Package is not null
            ? "校验通过后自动重启。聊天数据和清理备份会保留。"
            : "此版本未提供可在程序内安装的完整附件，请使用「手动下载」。", 8.5f);
        detail.Dock = DockStyle.Fill;
        detail.TextAlign = ContentAlignment.MiddleLeft;
        detail.ForeColor = DesktopTheme.Muted;
        layout.Controls.Add(detail, 0, 2);
        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        status.AccessibleName = "更新状态";
        layout.Controls.Add(status, 0, 3);
        progress.Margin = new Padding(0, 2, 0, 6);
        progress.AccessibleName = "更新下载进度";
        layout.Controls.Add(progress, 0, 4);
        foreach (var button in new[] { install, cancel, manual, errorDetails })
            button.MinimumSize = new Size(0, 38);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(0, 16, 0, 0), Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var secondary = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        manual.Margin = new Padding(0, 0, 8, 0);
        secondary.Controls.Add(manual);
        errorDetails.Visible = false;
        secondary.Controls.Add(errorDetails);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        install.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(install);
        actions.Controls.Add(cancel);
        buttons.Controls.Add(secondary, 0, 0);
        buttons.Controls.Add(actions, 1, 0);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        install.Enabled = update.Package is not null;
        if (update.Package is null) status.Text = "此版本请使用「手动下载」获取更新。";
        Shown += (_, _) => { if (install.Enabled) install.Focus(); else manual.Focus(); };
        install.Click += async (_, _) => await InstallUpdate();
        cancel.Click += (_, _) => { if (working) CancelDownload(close: false); else Close(); };
        errorDetails.Click += (_, _) => { using var dialog = CreateErrorDetails(); dialog.ShowDialog(this); };
        manual.Click += (_, _) =>
        {
            try { openReleasePage(update.ReleaseUri); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { status.Text = "无法打开浏览器，请稍后重试。"; }
        };
        FormClosing += (_, e) =>
        {
            if (!working) return;
            e.Cancel = true;
            if (installing) { status.Text = "正在准备重启更新，请稍候…"; return; }
            CancelDownload(close: true);
        };
        CancelButton = cancel;
        cancel.DialogResult = DialogResult.None;
    }

    private async Task InstallUpdate()
    {
        if (working || update.Package is null) return;
        using var cancellation = new CancellationTokenSource();
        downloadCancellation = cancellation;
        var updateToken = cancellation.Token;
        var currentAttempt = ++attemptNumber;
        closeAfterCancel = false;
        preservePrepared = false;
        fullError = "";
        errorDetails.Visible = false;
        status.ForeColor = DesktopTheme.Ink;
        working = true;
        install.Enabled = manual.Enabled = false;
        cancel.Text = "取消下载";
        progress.Visible = true;
        progress.Value = 0;
        status.Text = "正在连接下载…";
        PreparedUpdate? prepared = null;
        var failed = false;
        try
        {
            var updates = new Progress<UpdateProgress>(value => ReportProgress(value, currentAttempt));
            prepared = await updater.DownloadAsync(update, updates, updateToken);
            updateToken.ThrowIfCancellationRequested();
            installing = true;
            cancel.Enabled = false;
            status.Text = "校验通过，正在准备重启更新…";
            progress.Value = progress.Maximum;
            await Task.Run(() => installer.Start(prepared));
            handedOff = true;
            working = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        { if (!IsDisposed) status.Text = cancellation.IsCancellationRequested ? "下载已取消，可重新下载。" : "下载超时，请检查网络后重试。"; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or Win32Exception or InvalidOperationException or ArgumentException)
        {
            failed = true;
            preservePrepared = error is UpdateInstallException { PreserveFiles: true };
            ShowFailure(error.Message);
        }
        finally
        {
            if (!handedOff && !preservePrepared && prepared is not null)
            {
                try { discard(prepared); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed = true;
                    ShowFailure(fullError + "\n下载临时文件未能清除。\n" + error.Message);
                }
            }
            downloadCancellation = null;
            working = installing = false;
            if (!IsDisposed && !handedOff)
            {
                progress.Visible = false;
                install.Text = cancellation.IsCancellationRequested && !failed ? "重新下载" : "重试更新";
                install.Enabled = true;
                manual.Enabled = cancel.Enabled = true;
                cancel.Text = "关闭";
                if (closeAfterCancel && !failed) Close();
            }
        }
    }

    private void CancelDownload(bool close)
    {
        if (installing) return;
        closeAfterCancel |= close;
        downloadCancellation?.Cancel();
        cancel.Enabled = false;
        status.Text = "正在取消下载…";
    }

    private void ShowFailure(string message)
    {
        fullError = message.Trim();
        if (IsDisposed) return;
        status.Text = "更新未完成，可重试或查看错误详情。";
        status.ForeColor = DesktopTheme.Danger;
        errorDetails.Visible = true;
    }

    private Form CreateErrorDetails()
    {
        var dialog = new Form
        {
            Text = "更新错误详情", Size = new Size(620, 380), MinimumSize = new Size(480, 300),
            StartPosition = FormStartPosition.CenterParent, BackColor = DesktopTheme.Canvas,
            ForeColor = DesktopTheme.Ink, Font = Font, MinimizeBox = false, MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(24), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var heading = DesktopTheme.Label("错误说明", 11f, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        var text = new TextBox
        {
            Text = fullError.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, BorderStyle = BorderStyle.None,
            ForeColor = DesktopTheme.Ink, Font = new Font(Font.FontFamily, 9.5f), AccessibleName = "完整错误说明，可选择并复制",
        };
        layout.Controls.Add(ReadingSurface(text), 0, 1);
        var close = DesktopTheme.Button("关闭");
        close.MinimumSize = new Size(72, 38);
        close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        close.DialogResult = DialogResult.Cancel;
        layout.Controls.Add(close, 0, 2);
        dialog.Controls.Add(layout);
        dialog.CancelButton = close;
        dialog.Shown += (_, _) => { text.SelectAll(); text.Focus(); };
        return dialog;
    }

    private static SurfacePanel ReadingSurface(Control content)
    {
        var surface = new SurfacePanel { Dock = DockStyle.Fill, CornerRadius = 14,
            Padding = new Padding(16), Margin = Padding.Empty };
        surface.Controls.Add(content);
        return surface;
    }

    private static void SetReleaseNotes(TextBox view, string notes)
    {
        var text = string.IsNullOrWhiteSpace(notes) ? "此版本未提供更新说明，可点击「手动下载」查看发布页。" : notes;
        var lines = new List<string>();
        var codeFence = '\0';
        var codeFenceLength = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            var indentedCode = indent > 3 || line.StartsWith('\t');
            var fenceLength = !indentedCode && trimmed.Length > 0 && trimmed[0] is '`' or '~'
                ? trimmed.TakeWhile(character => character == trimmed[0]).Count() : 0;
            var fenceLine = fenceLength >= 3;
            var inCodeBlock = codeFence != '\0';
            var heading = !inCodeBlock && !indentedCode && !fenceLine && trimmed.StartsWith('#') && trimmed.TrimStart('#').StartsWith(' ');
            var rendered = heading ? trimmed.TrimStart('#').TrimStart() :
                !inCodeBlock && !indentedCode && (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                    ? line[..indent] + "• " + trimmed[2..] : line;
            lines.Add(rendered);
            if (fenceLine && !inCodeBlock) { codeFence = trimmed[0]; codeFenceLength = fenceLength; }
            else if (fenceLine && trimmed[0] == codeFence && fenceLength >= codeFenceLength && string.IsNullOrWhiteSpace(trimmed[fenceLength..]))
                codeFence = '\0';
        }
        view.Text = string.Join(Environment.NewLine, lines);
        view.Select(0, 0);
    }

    private void ReportProgress(UpdateProgress value, int currentAttempt)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ReportProgress(value, currentAttempt)); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
            return;
        }
        if (!working || installing || currentAttempt != attemptNumber || downloadCancellation?.IsCancellationRequested != false) return;
        var percentage = value.TotalBytes > 0 ? (int)Math.Clamp(value.ReceivedBytes * 100 / value.TotalBytes, 0, 100) : 0;
        status.Text = value.TotalBytes > 0
            ? $"{value.Stage}  {percentage}% · {value.ReceivedBytes / 1048576d:0.0} / {value.TotalBytes / 1048576d:0.0} MB"
            : value.Stage;
        progress.Value = value.TotalBytes > 0
            ? (int)Math.Clamp(value.ReceivedBytes * 1000 / value.TotalBytes, 0, 1000) : 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) downloadCancellation?.Cancel();
        base.Dispose(disposing);
    }
}
