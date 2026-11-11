using System.ComponentModel;
using System.Net.Http;

namespace CodexCleaner;

internal sealed class UpdateDialog : Form
{
    private readonly AvailableUpdate update;
    private readonly IAppUpdater updater;
    private readonly IUpdateInstaller installer;
    private readonly Action<PreparedUpdate> discard;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken updateToken;
    private readonly Button install = DesktopTheme.Button("下载并重启", primary: true);
    private readonly Button cancel = DesktopTheme.Button("稍后");
    private readonly Button manual = DesktopTheme.Button("手动下载");
    private readonly Label status = DesktopTheme.Label("准备好后，点击「下载并重启」安装更新。", 9f);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Visible = false };
    private bool working, installing, closeAfterCancel, handedOff, preservePrepared;

    internal UpdateDialog(AvailableUpdate update, Action<Uri> openReleasePage, IAppUpdater? updater = null,
        IUpdateInstaller? installer = null, Action<PreparedUpdate>? discard = null)
    {
        this.update = update;
        updateToken = cancellation.Token;
        this.updater = updater ?? new AppUpdater();
        this.installer = installer ?? new UpdateInstaller();
        this.discard = discard ?? AppUpdater.Discard;
        Text = "更新 CodexCleaner";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(660, 520);
        MinimumSize = new Size(540, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DesktopTheme.Canvas;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimizeBox = MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(20), BackColor = DesktopTheme.Canvas };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var heading = DesktopTheme.Label($"更新到 v{update.Version}  ·  当前 v{UpdateChecker.CurrentVersion}", 11f, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
            BackColor = DesktopTheme.Canvas, ForeColor = DesktopTheme.Ink, Font = Font,
            Text = string.IsNullOrWhiteSpace(update.Notes) ? "此版本未提供更新说明，可点击「手动下载」查看发布页。" : update.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            AccessibleName = "本次更新内容，可选择并复制", Margin = new Padding(0, 6, 0, 8),
        };
        layout.Controls.Add(notes, 0, 1);
        var detail = DesktopTheme.Label(update.Package is { } package
            ? $"下载约 {package.Size / 1048576d:0.0} MB，校验通过后重启本程序。\n聊天数据和清理备份不受更新影响。"
            : "此版本未提供可在程序内安装的完整附件，请使用「手动下载」。", 8.5f);
        detail.Dock = DockStyle.Fill;
        detail.ForeColor = DesktopTheme.Muted;
        layout.Controls.Add(detail, 0, 2);
        status.Dock = DockStyle.Fill;
        status.AutoEllipsis = true;
        status.AccessibleName = "更新状态";
        layout.Controls.Add(status, 0, 3);
        progress.Margin = new Padding(0, 0, 0, 4);
        progress.AccessibleName = "更新下载进度";
        layout.Controls.Add(progress, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
        buttons.Controls.Add(install);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(manual);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        install.Enabled = update.Package is not null;
        Shown += (_, _) => { if (install.Enabled) install.Focus(); else manual.Focus(); };
        install.Click += async (_, _) => await InstallUpdate();
        cancel.Click += (_, _) => Close();
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
            closeAfterCancel = true;
            cancellation.Cancel();
            cancel.Enabled = false;
            status.Text = "正在取消下载…";
        };
        CancelButton = cancel;
    }

    private async Task InstallUpdate()
    {
        if (working || update.Package is null) return;
        preservePrepared = false;
        working = true;
        install.Enabled = manual.Enabled = false;
        cancel.Text = "取消下载";
        progress.Visible = true;
        progress.Value = 0;
        status.Text = "正在连接下载…";
        PreparedUpdate? prepared = null;
        try
        {
            var updates = new Progress<UpdateProgress>(ReportProgress);
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
        { if (!IsDisposed) status.Text = cancellation.IsCancellationRequested ? "下载已取消。" : "下载超时，请检查网络后重试。"; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or Win32Exception or InvalidOperationException or ArgumentException)
        {
            preservePrepared = error is UpdateInstallException { PreserveFiles: true };
            if (!IsDisposed) status.Text = $"更新未完成：{error.Message}";
        }
        finally
        {
            if (!handedOff && !preservePrepared && prepared is not null)
            {
                try { discard(prepared); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                { if (!IsDisposed) status.Text += " 下载临时文件未能清除，可稍后重试。"; }
            }
            working = installing = false;
            if (!IsDisposed && !handedOff)
            {
                install.Text = "重试更新";
                install.Enabled = !cancellation.IsCancellationRequested;
                manual.Enabled = cancel.Enabled = true;
                cancel.Text = "关闭";
                if (closeAfterCancel) Close();
            }
        }
    }

    private void ReportProgress(UpdateProgress value)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ReportProgress(value)); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
            return;
        }
        if (!working || installing || cancellation.IsCancellationRequested) return;
        status.Text = value.TotalBytes > 0
            ? $"{value.Stage}  {value.ReceivedBytes / 1048576d:0.0} / {value.TotalBytes / 1048576d:0.0} MB"
            : value.Stage;
        progress.Value = value.TotalBytes > 0
            ? (int)Math.Clamp(value.ReceivedBytes * 1000 / value.TotalBytes, 0, 1000) : 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { cancellation.Cancel(); cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
