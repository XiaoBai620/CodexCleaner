using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexCleaner.Engine;

namespace CodexCleaner;

internal sealed class MainForm : Form
{
    private readonly Cleaner cleaner;
    private readonly IUpdateChecker updateChecker;
    private readonly Action<Uri> openReleasePage;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly Button checkUpdates = DesktopTheme.Button("检查更新");
    private readonly Button downloadUpdate = DesktopTheme.Button("前往更新");
    private readonly Label updateNotice = DesktopTheme.Label("", 9.5f, true);
    private readonly Panel updateBanner = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 14, 6), Margin = new Padding(0, 8, 0, 0), BackColor = DesktopTheme.Hover, Visible = false };
    private readonly RowStyle updateRowStyle = new(SizeType.Absolute, 0);
    private AvailableUpdate? availableUpdate;
    private bool checkingUpdate;
    private readonly HashSet<string> selected = [];
    private readonly Label processTitle = DesktopTheme.Label("正在检查本地状态", 9f);
    private readonly Label processDetail = DesktopTheme.Label("请稍候…", 8.5f);
    private readonly Label processDot = DesktopTheme.Label("●", 14f, true);
    private readonly Panel processBanner = new() { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = new Padding(24, 0, 12, 0), BackColor = Color.Transparent };
    private readonly Button diagnosticsButton = DesktopTheme.Button("查看检查详情");
    private readonly TextBox search = new() { PlaceholderText = "搜索标题或会话 ID", BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 9.5f), AccessibleName = "搜索标题或会话 ID" };
    private readonly Button clearSearch = new ClearSearchButton();
    private readonly ComboBox filter = new FilterComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "筛选会话状态" };
    private readonly DataGridView list = new BufferedGrid();
    private readonly Label listCount = DesktopTheme.Label("读取中", 8.5f);
    private readonly Label emptyState = DesktopTheme.Label("正在读取会话…", 10f);
    private readonly Button selectAll = DesktopTheme.Button("全选结果");
    private readonly Button clearSelection = DesktopTheme.Button("清空选择");
    private readonly Button refresh = DesktopTheme.Button("刷新");
    private readonly Button backups = DesktopTheme.Button("备份与恢复");
    private readonly Button repair = DesktopTheme.Button("检查失效引用");
    private readonly Button preview = DesktopTheme.Button("预览清理范围  →", primary: true);
    private readonly Label summary = DesktopTheme.Label("尚未选择清理内容", 11f, true);
    private readonly Label selectionCount = DesktopTheme.Label("勾选会话后，先核对范围，再确认清理。", 8.5f);
    private readonly Label detailStatus = DesktopTheme.Label("点击会话查看详情", 9f, true);
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9f), AccessibleName = "会话完整详情，可选择并复制" };
    private readonly Button copyDetails = DesktopTheme.Button("复制");
    private readonly Button expandDetails = DesktopTheme.Button("展开");
    private readonly Button viewConversation = DesktopTheme.Button("查看对话");
    private bool codexRunning;
    private readonly CheckBox clearMemories = new() { Text = "全部本地记忆", AutoSize = true, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), AccessibleDescription = "可选，会清空所有聊天的合并记忆" };
    private readonly CheckBox clearShared = new() { Text = "共享输入历史与日志", AutoSize = true, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), AccessibleDescription = "可选，会影响所有聊天的共享历史与日志" };
    private readonly ToolTip tip = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    private readonly System.Windows.Forms.Timer processTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 500 };
    private readonly Stopwatch busyClock = new();
    private Snapshot? state;
    private List<string> diagnostics = [];
    private string? focusedChatId;
    private bool busy, rendering, checkingProcess;
    private string activity = "正在处理";

    public MainForm(Cleaner cleaner, IUpdateChecker? updateChecker = null, Action<Uri>? openReleasePage = null)
    {
        this.cleaner = cleaner;
        this.updateChecker = updateChecker ?? new UpdateChecker();
        this.openReleasePage = openReleasePage ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        var version = UpdateChecker.CurrentVersion;
        Text = $"Codex 本地清理台 {version.Major}.{version.Minor}.{version.Build}";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(960, 680);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = DesktopTheme.Canvas;
        ForeColor = DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        KeyPreview = true;

        var root = CreateLayout(1, 4);
        root.Padding = new Padding(20, 8, 20, 14);
        root.BackColor = DesktopTheme.Canvas;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(updateRowStyle);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.Controls.Add(BuildMasthead(version), 0, 0);
        root.Controls.Add(BuildUpdateBanner(), 0, 1);
        var body = CreateLayout(2, 1);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildListPanel(), 0, 0);
        body.Controls.Add(BuildSidebar(), 1, 0);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);

        filter.Items.AddRange(["全部会话", "可清理", "仅清理记录", "受保护", "使用中", "已归档"]);
        filter.SelectedIndex = 0;
        search.TextChanged += (_, _) => RenderChats();
        clearSearch.Click += (_, _) => { search.Clear(); search.Focus(); };
        filter.SelectedIndexChanged += (_, _) => RenderChats();
        list.CellClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) ToggleRow(e.RowIndex); };
        list.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0) ShowConversation(); };
        viewConversation.Click += (_, _) => ShowConversation();
        list.CurrentCellChanged += (_, _) => { if (!rendering) RenderDetails(); };
        list.CellPainting += PaintStatus;
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || list.CurrentRow is null) return;
            e.Handled = e.SuppressKeyPress = true;
            ToggleRow(list.CurrentRow.Index);
        };
        selectAll.Click += (_, _) => ToggleVisibleChats();
        clearSelection.Click += (_, _) => { selected.Clear(); RenderChats(); };
        clearMemories.CheckedChanged += (_, _) => RenderSelection();
        clearShared.CheckedChanged += (_, _) => RenderSelection();
        refresh.Click += async (_, _) => await Reload();
        backups.Click += async (_, _) => await ShowBackups();
        checkUpdates.Click += async (_, _) => await CheckForUpdates(manual: true);
        downloadUpdate.Click += (_, _) => OpenUpdate();
        repair.Click += async (_, _) => await PreviewRepair();
        preview.Click += async (_, _) => await PreviewCleanup();
        diagnosticsButton.Click += (_, _) => ShowDiagnostics();
        expandDetails.Click += (_, _) => ShowTextDialog("会话完整详情", details.Text);
        copyDetails.Click += (_, _) =>
        {
            try { if (details.TextLength > 0) Clipboard.SetText(details.Text); }
            catch (ExternalException) { MessageBox.Show(this, "剪贴板暂时被其他程序占用，请稍后重试。", "无法复制", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        processTimer.Tick += async (_, _) => await RefreshProcessState();
        busyTimer.Tick += (_, _) => RenderActivity();
        Shown += async (_, _) =>
        {
            ApplyGridMetrics();
            var updateTask = CheckForUpdates(manual: false);
            await Reload();
            if (!IsDisposed) { ActiveControl = search; processTimer.Start(); }
            await updateTask;
        };
        DpiChanged += (_, _) => BeginInvoke(() =>
        {
            ApplyGridMetrics();
            if (availableUpdate is not null) updateRowStyle.Height = 52 * DeviceDpi / 96f;
        });
        KeyDown += async (_, e) =>
        {
            if (busy) return;
            if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; search.Focus(); search.SelectAll(); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await Reload(); }
            else if (e.KeyCode == Keys.Escape && search.ContainsFocus) { e.SuppressKeyPress = true; search.Clear(); }
        };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "正在处理本地数据，请等待完成后再关闭。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        tip.SetToolTip(search, "搜索标题或会话 ID · Ctrl+F");
        tip.SetToolTip(refresh, "重新读取本地记录 · F5");
        tip.SetToolTip(backups, "查看删除前保留的备份；退出 Codex 后可恢复聊天，也可删除旧备份。");
        tip.SetToolTip(clearSearch, "清除搜索");
        tip.SetToolTip(repair, "检查已删除会话留下的本地界面引用；先预览，再确认修复。");
        tip.SetToolTip(expandDetails, "在独立窗口查看完整标题、路径和处理原因");
        tip.SetToolTip(viewConversation, "只读查看这条聊天的对话记录 · 也可双击列表行");
        tip.SetToolTip(checkUpdates, "启动时自动检查 GitHub 正式版；也可点击重新检查。不发送本地会话数据。");
        RenderSelection();
    }

    private static TableLayoutPanel CreateLayout(int columns, int rows)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = Padding.Empty, BackColor = Color.Transparent };
        if (columns == 1) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    private Control BuildMasthead(Version version)
    {
        var header = CreateLayout(5, 1);
        header.Padding = new Padding(0, 0, 0, 8);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logo = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 4, 8, 4), BackColor = Color.Transparent, Image = DesktopTheme.Logo(64) };
        header.Controls.Add(logo, 0, 0);
        var title = DesktopTheme.Label("CodexCleaner", 11.5f, true);
        title.AutoSize = true;
        title.Anchor = AnchorStyles.Left;
        header.Controls.Add(title, 1, 0);
        var badge = DesktopTheme.Label($"v{version.Major}.{version.Minor}.{version.Build}", 8f);
        badge.AutoSize = true;
        badge.Anchor = AnchorStyles.Left;
        badge.Margin = new Padding(10, 0, 0, 0);
        header.Controls.Add(badge, 2, 0);
        header.Controls.Add(BuildProcessBanner(), 3, 0);
        checkUpdates.Anchor = AnchorStyles.Right;
        checkUpdates.Margin = Padding.Empty;
        header.Controls.Add(checkUpdates, 4, 0);
        return header;
    }

    private Control BuildUpdateBanner()
    {
        var row = CreateLayout(2, 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        updateNotice.Dock = DockStyle.Fill;
        updateNotice.TextAlign = ContentAlignment.MiddleLeft;
        updateNotice.AutoEllipsis = true;
        updateNotice.ForeColor = DesktopTheme.Accent;
        downloadUpdate.Anchor = AnchorStyles.Right;
        row.Controls.Add(updateNotice, 0, 0);
        row.Controls.Add(downloadUpdate, 1, 0);
        updateBanner.Controls.Add(row);
        return updateBanner;
    }

    private async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdate || IsDisposed || updateCancellation.IsCancellationRequested) return;
        checkingUpdate = true;
        checkUpdates.Enabled = false;
        checkUpdates.Text = "检查中…";
        try
        {
            var update = await updateChecker.CheckAsync(updateCancellation.Token);
            if (IsDisposed || updateCancellation.IsCancellationRequested) return;
            availableUpdate = update;
            updateBanner.Visible = update is not null;
            updateRowStyle.Height = update is null ? 0 : 52 * DeviceDpi / 96f;
            if (update is not null)
            {
                updateNotice.Text = $"发现新版本 v{update.Version} · 当前 v{UpdateChecker.CurrentVersion}";
                tip.SetToolTip(updateNotice, updateNotice.Text);
                tip.SetToolTip(downloadUpdate, "打开 GitHub 发布页，下载新版后退出并替换程序。");
            }
            checkUpdates.Text = update is null && manual ? "已是最新版本" : "检查更新";
            tip.SetToolTip(checkUpdates, update is null ? "当前已是最新正式版。点击可重新检查。" : "发现新版本，点击「前往更新」查看发布说明和下载。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (IsDisposed || updateCancellation.IsCancellationRequested) return;
            checkUpdates.Text = manual ? "检查失败，重试" : "检查更新";
            tip.SetToolTip(checkUpdates, "暂时无法检查更新，请检查网络后重试。本地清理可继续使用。");
        }
        finally
        {
            checkingUpdate = false;
            if (!IsDisposed && !updateCancellation.IsCancellationRequested) checkUpdates.Enabled = true;
        }
    }

    private void OpenUpdate()
    {
        if (availableUpdate is null || busy) return;
        try { openReleasePage(availableUpdate.ReleaseUri); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"无法打开浏览器，请访问发布页：\n{availableUpdate.ReleaseUri.AbsoluteUri}", "前往更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private Control BuildProcessBanner()
    {
        var row = CreateLayout(4, 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        processDot.Dock = DockStyle.Fill;
        processDot.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(processDot, 0, 0);
        processTitle.AutoSize = true;
        processTitle.Anchor = AnchorStyles.Left;
        processTitle.Margin = new Padding(0, 0, 12, 0);
        processDetail.Dock = DockStyle.Fill;
        processDetail.TextAlign = ContentAlignment.MiddleLeft;
        processDetail.AutoEllipsis = true;
        row.Controls.Add(processTitle, 1, 0);
        row.Controls.Add(processDetail, 2, 0);
        diagnosticsButton.Visible = false;
        diagnosticsButton.Anchor = AnchorStyles.Right;
        diagnosticsButton.Padding = new Padding(7, 2, 7, 2);
        diagnosticsButton.Font = new Font("Microsoft YaHei UI", 8f);
        row.Controls.Add(diagnosticsButton, 3, 0);
        processBanner.Controls.Add(row);
        return processBanner;
    }

    private Control BuildListPanel()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(15, 12, 15, 8), Margin = new Padding(0, 0, 12, 0), CornerRadius = 16 };
        var panel = CreateLayout(1, 4);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titleRow = CreateLayout(4, 1);
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = DesktopTheme.Label("会话", 11f, true);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        listCount.Dock = DockStyle.Fill;
        listCount.TextAlign = ContentAlignment.MiddleLeft;
        listCount.AutoEllipsis = true;
        refresh.Anchor = AnchorStyles.Right;
        backups.Anchor = AnchorStyles.Right;
        backups.Margin = new Padding(0, 0, 4, 0);
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(listCount, 1, 0);
        titleRow.Controls.Add(backups, 2, 0);
        titleRow.Controls.Add(refresh, 3, 0);
        panel.Controls.Add(titleRow, 0, 0);

        var searchRow = CreateLayout(2, 1);
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchFrame = new SearchField(search, clearSearch) { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 10, 6) };
        searchRow.Controls.Add(searchFrame, 0, 0);
        filter.Margin = new Padding(0, 2, 0, 6);
        searchRow.Controls.Add(filter, 1, 0);
        panel.Controls.Add(searchRow, 0, 1);

        var actions = CreateLayout(2, 1);
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selectionActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        selectAll.Margin = new Padding(0, 0, 7, 0);
        selectionActions.Controls.Add(selectAll);
        selectionActions.Controls.Add(clearSelection);
        repair.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        actions.Controls.Add(selectionActions, 0, 0);
        actions.Controls.Add(repair, 1, 0);
        panel.Controls.Add(actions, 0, 2);

        ConfigureGrid();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        emptyState.Dock = DockStyle.Fill;
        emptyState.TextAlign = ContentAlignment.MiddleCenter;
        emptyState.BackColor = Color.White;
        host.Controls.Add(list);
        host.Controls.Add(emptyState);
        emptyState.BringToFront();
        panel.Controls.Add(host, 0, 3);
        card.Controls.Add(panel);
        return card;
    }

    private void ConfigureGrid()
    {
        list.Dock = DockStyle.Fill;
        list.BackgroundColor = Color.White;
        list.BorderStyle = BorderStyle.None;
        list.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        list.GridColor = DesktopTheme.Hover;
        list.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        list.EnableHeadersVisualStyles = false;
        list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        list.ColumnHeadersHeight = 36;
        list.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = DesktopTheme.Faint, Font = new Font("Microsoft YaHei UI", 8.5f), SelectionBackColor = Color.White, Padding = new Padding(5, 0, 0, 0) };
        list.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = DesktopTheme.Ink, Font = new Font("Microsoft YaHei UI", 9.5f), SelectionBackColor = DesktopTheme.Hover, SelectionForeColor = DesktopTheme.Ink, Padding = new Padding(5, 0, 0, 0) };
        list.RowTemplate.Height = 43;
        list.RowHeadersVisible = false;
        list.ReadOnly = true;
        list.AllowUserToAddRows = list.AllowUserToDeleteRows = list.AllowUserToResizeRows = list.AllowUserToOrderColumns = false;
        list.AllowUserToResizeColumns = false;
        list.MultiSelect = false;
        list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        list.AutoGenerateColumns = false;
        list.ShowCellToolTips = true;
        list.AccessibleName = "本地会话列表，点击会话查看详情，勾选或按空格选择清理";
        list.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "", Width = 34, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = Padding.Empty } });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "会话标题", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 130, SortMode = DataGridViewColumnSortMode.NotSortable });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "消息 · 大小", Width = 104, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Microsoft YaHei UI", 8f), ForeColor = DesktopTheme.Muted, Alignment = DataGridViewContentAlignment.MiddleRight } });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Updated", HeaderText = "最近更新", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Microsoft YaHei UI", 8f), ForeColor = DesktopTheme.Muted } });
        list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "处理方式", Width = 108, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    private const int StatusColumn = 4;

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static string SizeText(ChatInfo chat) => $"{(chat.Messages is { } m ? m.ToString("N0") : "—")} · {FormatBytes(chat.RolloutBytes)}";

    private Control BuildSidebar()
    {
        var sidebar = CreateLayout(1, 2);
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 187));
        var detailCard = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 12, 13), Margin = new Padding(0, 0, 0, 12), CornerRadius = 16 };
        var detailLayout = CreateLayout(1, 3);
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var detailHeading = CreateLayout(4, 1);
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var detailTitle = DesktopTheme.Label("会话详情", 10f, true);
        detailTitle.Dock = DockStyle.Fill;
        detailTitle.TextAlign = ContentAlignment.MiddleLeft;
        copyDetails.Anchor = AnchorStyles.Right;
        copyDetails.Padding = new Padding(8, 4, 8, 4);
        expandDetails.Anchor = AnchorStyles.Right;
        expandDetails.Font = copyDetails.Font;
        expandDetails.Padding = copyDetails.Padding;
        expandDetails.Margin = new Padding(0, 0, 6, 0);
        expandDetails.AccessibleName = "展开会话完整详情";
        viewConversation.Anchor = AnchorStyles.Right;
        viewConversation.Font = copyDetails.Font;
        viewConversation.Padding = copyDetails.Padding;
        viewConversation.Margin = new Padding(0, 0, 6, 0);
        detailHeading.Controls.Add(detailTitle, 0, 0);
        detailHeading.Controls.Add(viewConversation, 1, 0);
        detailHeading.Controls.Add(expandDetails, 2, 0);
        detailHeading.Controls.Add(copyDetails, 3, 0);
        detailLayout.Controls.Add(detailHeading, 0, 0);
        detailStatus.Dock = DockStyle.Fill;
        detailStatus.TextAlign = ContentAlignment.MiddleLeft;
        detailStatus.AutoEllipsis = true;
        detailLayout.Controls.Add(detailStatus, 0, 1);
        details.Margin = new Padding(0, 5, 0, 0);
        detailLayout.Controls.Add(details, 0, 2);
        detailCard.Controls.Add(detailLayout);
        sidebar.Controls.Add(detailCard, 0, 0);

        var options = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 11), Margin = Padding.Empty, CornerRadius = 16 };
        var optionLayout = CreateLayout(1, 6);
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        optionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var optionTitle = DesktopTheme.Label("附加清理", 10f, true);
        optionTitle.Dock = DockStyle.Fill;
        optionLayout.Controls.Add(optionTitle, 0, 0);
        var optionHint = DesktopTheme.Label("可选 · 以下范围涉及所有聊天", 8f);
        optionHint.Dock = DockStyle.Fill;
        optionLayout.Controls.Add(optionHint, 0, 1);
        clearMemories.Margin = new Padding(0, 3, 0, 0);
        clearShared.Margin = new Padding(0, 3, 0, 0);
        optionLayout.Controls.Add(clearMemories, 0, 2);
        var memoryHint = DesktopTheme.Label("清空已提取及合并的本地记忆", 8f);
        memoryHint.Dock = DockStyle.Fill;
        memoryHint.Padding = new Padding(20, 0, 0, 0);
        optionLayout.Controls.Add(memoryHint, 0, 3);
        optionLayout.Controls.Add(clearShared, 0, 4);
        var sharedHint = DesktopTheme.Label("清空共享输入记录与数据库日志", 8f);
        sharedHint.Dock = DockStyle.Fill;
        sharedHint.Padding = new Padding(20, 0, 0, 0);
        optionLayout.Controls.Add(sharedHint, 0, 5);
        options.Controls.Add(optionLayout);
        sidebar.Controls.Add(options, 0, 1);
        return sidebar;
    }

    private Control BuildFooter()
    {
        var footer = CreateLayout(2, 1);
        footer.Padding = new Padding(3, 13, 0, 0);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var description = CreateLayout(1, 2);
        description.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        description.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        summary.Dock = selectionCount.Dock = DockStyle.Fill;
        summary.AutoEllipsis = selectionCount.AutoEllipsis = true;
        description.Controls.Add(summary, 0, 0);
        description.Controls.Add(selectionCount, 0, 1);
        footer.Controls.Add(description, 0, 0);
        preview.Padding = new Padding(20, 10, 20, 10);
        preview.Margin = new Padding(16, 0, 0, 0);
        preview.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        footer.Controls.Add(preview, 1, 0);
        return footer;
    }

    private static bool Selectable(ChatInfo chat) => chat.CanDelete || chat.CanRemoveRecord;

    private IEnumerable<ChatInfo> VisibleChats()
    {
        var q = search.Text.Trim();
        return (state?.Chats ?? []).Where(c =>
            (q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) &&
            (filter.SelectedIndex switch { 1 => Selectable(c), 2 => c.CanRemoveRecord, 3 => !Selectable(c) && !c.InUse, 4 => c.InUse, 5 => c.Archived, _ => true }));
    }

    private static string StatusText(ChatInfo chat) => chat.InUse ? "使用中" : chat.CanRemoveRecord ? "仅清理记录" : !chat.CanDelete ? "受保护" : chat.RolloutMissing ? "文件已缺失" : chat.Archived ? "已归档" : "本地会话";

    private static (Color Ink, Color Fill) StatusColors(ChatInfo chat) => chat.InUse
        ? (DesktopTheme.Ink, DesktopTheme.Pressed)
        : chat.CanRemoveRecord || chat.RolloutMissing ? (DesktopTheme.Warning, DesktopTheme.Hover)
        : !chat.CanDelete ? (DesktopTheme.Danger, DesktopTheme.Hover)
        : chat.Archived ? (DesktopTheme.Muted, DesktopTheme.Hover)
        : (DesktopTheme.Green, DesktopTheme.GreenFill);

    private void PaintStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != StatusColumn || list.Rows[e.RowIndex].Tag is not ChatInfo chat || e.Graphics is null) return;
        e.PaintBackground(e.ClipBounds, true);
        var (ink, fill) = StatusColors(chat);
        var scale = DeviceDpi / 96f;
        var inset = Math.Max(2, (int)(e.CellBounds.Height * 0.19f));
        var pill = new Rectangle(e.CellBounds.Left + (int)(5 * scale), e.CellBounds.Top + inset, e.CellBounds.Width - (int)(13 * scale), e.CellBounds.Height - 2 * inset);
        if (pill.Width > 0 && pill.Height > 0)
        {
            using var brush = new SolidBrush(fill);
            using var path = DesktopTheme.RoundedRect(pill, pill.Height / 2f);
            e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, StatusText(chat), e.CellStyle?.Font ?? list.Font, pill, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        e.Handled = true;
    }

    private void ApplyGridMetrics()
    {
        if (IsDisposed || list.Columns.Count <= StatusColumn) return;
        int ScaleSize(int value) => (int)Math.Round(value * DeviceDpi / 96f);
        list.RowTemplate.Height = ScaleSize(43);
        list.ColumnHeadersHeight = ScaleSize(36);
        list.Columns[0].Width = ScaleSize(34);
        list.Columns[1].MinimumWidth = ScaleSize(130);
        list.Columns[2].Width = ScaleSize(104);
        list.Columns[3].Width = ScaleSize(128);
        list.Columns[StatusColumn].Width = ScaleSize(108);
        foreach (DataGridViewRow row in list.Rows) row.Height = list.RowTemplate.Height;
        list.Invalidate();
    }

    private void ToggleRow(int index)
    {
        if (busy || index < 0 || index >= list.Rows.Count || list.Rows[index].Tag is not ChatInfo chat) return;
        list.CurrentCell = list.Rows[index].Cells[1];
        if (!Selectable(chat)) { RenderDetails(); return; }
        if (!selected.Add(chat.Id)) selected.Remove(chat.Id);
        list.Rows[index].Cells[0].Value = selected.Contains(chat.Id);
        RenderSelection();
    }

    private void ToggleVisibleChats()
    {
        var selectable = VisibleChats().Where(Selectable).ToList();
        var clear = selectable.Count > 0 && selectable.All(c => selected.Contains(c.Id));
        foreach (var chat in selectable)
            if (clear) selected.Remove(chat.Id); else selected.Add(chat.Id);
        RenderChats();
    }

    private void RenderChats()
    {
        if (IsDisposed) return;
        rendering = true;
        list.SuspendLayout();
        try
        {
            list.Rows.Clear();
            foreach (var chat in VisibleChats())
            {
                var index = list.Rows.Add(selected.Contains(chat.Id), chat.Title, SizeText(chat), chat.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—", StatusText(chat));
                var row = list.Rows[index];
                row.Tag = chat;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = $"{chat.Title}\n{chat.Id}\n{StatusText(chat)}" + (chat.BlockedReason is null ? "" : "\n" + chat.BlockedReason);
            }
            var focus = list.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is ChatInfo c && c.Id == focusedChatId);
            list.ClearSelection();
            if (focus is not null) { list.CurrentCell = focus.Cells[1]; focus.Selected = true; }
            else list.CurrentCell = null;
        }
        finally { list.ResumeLayout(); rendering = false; }
        emptyState.Text = state is null ? "暂时无法读取会话\n点击「刷新」重新检查" : state.Chats.Count == 0 ? "还没有本地会话\n识别到的聊天会显示在这里" : "没有匹配的会话\n试试其他关键词或筛选条件";
        emptyState.Visible = list.Rows.Count == 0;
        list.Visible = list.Rows.Count > 0;
        var inUse = state?.Chats.Count(c => c.InUse) ?? 0;
        listCount.Text = state is null ? "尚未加载" : $"{state.Chats.Count} 条记录 · 显示 {list.Rows.Count} 条" + (inUse > 0 ? $" · {inUse} 条使用中" : "");
        RenderDetails();
        RenderSelection();
    }

    private void RenderDetails()
    {
        if (list.CurrentRow?.Tag is not ChatInfo chat)
        {
            focusedChatId = null;
            detailStatus.Text = "选择一条会话";
            detailStatus.ForeColor = DesktopTheme.Muted;
            details.Text = "点击列表中的会话，在这里查看完整标题、路径和处理说明。\r\n\r\n勾选方框才会加入清理范围。";
            details.SelectionStart = details.SelectionLength = 0;
            copyDetails.Enabled = expandDetails.Enabled = viewConversation.Enabled = false;
            return;
        }
        focusedChatId = chat.Id;
        detailStatus.Text = StatusText(chat);
        detailStatus.ForeColor = StatusColors(chat).Ink;
        var explanation = chat.InUse ? "这条聊天正在 Codex 中使用，暂不能清理；可以查看对话。"
            : chat.CanRemoveRecord ? "可清理这条会话的本地数据库记录与已识别关联。原会话文件会保留，不会访问或删除该路径。"
            : !chat.CanDelete ? "当前无法清理这条会话，请先处理下面的原因。"
            : chat.RolloutMissing ? "会话文件已缺失，可清理残留的本地记录与关联。"
            : "将清理这条会话及已验证的本地关联；实际影响范围会在下一步列出。";
        var extra = chat.RolloutFiles.Where(f => !string.Equals(f, chat.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
        details.Text = $"{chat.Title}\r\n\r\n{explanation}"
            + (chat.BlockedReason is null ? "" : $"\r\n\r\n检查说明\r\n{chat.BlockedReason}")
            + $"\r\n\r\n会话 ID\r\n{chat.Id}\r\n\r\n会话路径\r\n{(string.IsNullOrWhiteSpace(chat.OriginalPath) ? chat.RelativePath ?? "未提供会话文件路径" : chat.OriginalPath)}"
            + (extra.Count > 0 ? $"\r\n\r\n同一聊天的其他会话文件（分段或压缩副本，一并删除）\r\n{string.Join("\r\n", extra)}" : "")
            + (chat.ForkChildren.Count > 0 ? $"\r\n\r\n被分叉引用\r\n{chat.ForkChildren.Count} 个分叉聊天以它为历史来源，需要一起删除：\r\n{string.Join("\r\n", chat.ForkChildren.Select(ChatLabel))}" : "")
            + $"\r\n\r\n消息 · 会话文件大小\r\n{SizeText(chat)}"
            + $"\r\n\r\n最近更新\r\n{chat.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}"
            + (chat.Archived ? "\r\n\r\n归档状态\r\n已归档" : "");
        details.SelectionStart = 0;
        details.SelectionLength = 0;
        copyDetails.Enabled = expandDetails.Enabled = viewConversation.Enabled = true;
    }

    private string ChatLabel(string id) => state?.Chats.FirstOrDefault(c => c.Id == id) is { } c ? $"{c.Title}（{id}）" : id;

    private void ShowConversation()
    {
        if (busy || list.CurrentRow?.Tag is not ChatInfo chat) return;
        using var dialog = new ConversationDialog(cleaner, chat);
        dialog.ShowDialog(this);
    }

    private void RenderSelection()
    {
        if (IsDisposed) return;
        var visible = VisibleChats().ToList();
        var selectable = visible.Where(Selectable).ToList();
        var hidden = selected.Count - visible.Count(c => selected.Contains(c.Id));
        var recordOnly = state?.Chats.Count(c => selected.Contains(c.Id) && c.CanRemoveRecord) ?? 0;
        var parts = new List<string>();
        if (selected.Count > 0) parts.Add($"已选 {selected.Count} 条会话");
        if (clearMemories.Checked) parts.Add("全部本地记忆");
        if (clearShared.Checked) parts.Add("共享历史与日志");
        summary.Text = busy ? ActivityText : parts.Count > 0 ? string.Join("  +  ", parts) : "尚未选择清理内容";
        var notes = new List<string>();
        if (hidden > 0) notes.Add($"筛选外仍有 {hidden} 条已选");
        if (recordOnly > 0) notes.Add($"{recordOnly} 条仅清理本地记录，保留原文件");
        if (codexRunning && selected.Count > 0) notes.Add("Codex 运行中：界面引用待退出后修复");
        if (notes.Count == 0) notes.Add("先预览，再确认 · 项目、设置和云端记录不受影响");
        selectionCount.Text = string.Join(" · ", notes);
        selectionCount.ForeColor = hidden > 0 || recordOnly > 0 ? DesktopTheme.Warning : DesktopTheme.Muted;
        tip.SetToolTip(summary, summary.Text);
        tip.SetToolTip(selectionCount, selectionCount.Text);
        var caps = state?.Capabilities;
        var recoveryBlocked = state?.RecoveryIssues.Any(i => i.BlocksWrites) == true;
        preview.Enabled = !busy && !recoveryBlocked && parts.Count > 0 && caps is not null && (selected.Count == 0 || caps.DeleteChats) && caps.ClearData;
        preview.Text = busy ? "正在处理…" : "预览清理范围  →";
        repair.Enabled = !busy && !recoveryBlocked && caps?.RepairReferences == true;
        refresh.Enabled = search.Enabled = filter.Enabled = list.Enabled = !busy;
        backups.Enabled = !busy;
        clearSearch.Enabled = !busy && search.TextLength > 0;
        clearSearch.Visible = search.TextLength > 0;
        // Clearing every chat's memories or shared history touches files Codex keeps writing.
        clearMemories.Enabled = clearShared.Enabled = !busy && !recoveryBlocked && !codexRunning && caps?.ClearData == true;
        selectAll.Enabled = !busy && selectable.Count > 0;
        selectAll.Text = selectable.Count > 0 && selectable.All(c => selected.Contains(c.Id)) ? "取消结果选择" : "全选结果";
        clearSelection.Enabled = !busy && selected.Count > 0;
        downloadUpdate.Enabled = !busy;
    }

    private async Task Run(string label, Func<Task> action)
    {
        if (busy) return;
        busy = true;
        StartActivity(label);
        UseWaitCursor = true;
        RenderSelection();
        try
        {
            if (refreshing is { } pending) await pending;
            await action();
        }
        catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            StopActivity();
            if (!IsDisposed) MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            StopActivity();
            if (!IsDisposed) { UseWaitCursor = false; RenderSelection(); }
        }
    }

    private string ActivityText => $"{activity}…  {busyClock.Elapsed.TotalSeconds:0} 秒";

    private void RenderActivity()
    {
        if (IsDisposed || !busy) return;
        summary.Text = ActivityText;
        tip.SetToolTip(summary, summary.Text);
    }

    private void SetActivity(string label)
    {
        if (IsDisposed || !busy || !busyClock.IsRunning) return;
        activity = label;
        RenderActivity();
    }

    private void StartActivity(string label)
    {
        busyClock.Restart();
        SetActivity(label);
        busyTimer.Start();
    }

    private void StopActivity()
    {
        busyClock.Stop();
        busyTimer.Stop();
    }

    private Task Reload() => Run("正在读取本地会话", LoadState);

    private Task ShowBackups() => Run("正在打开备份", async () =>
    {
        StopActivity();
        UseWaitCursor = false;
        using var dialog = new BackupDialog(cleaner);
        dialog.ShowDialog(this);
        if (!dialog.Changed) return;
        UseWaitCursor = true;
        StartActivity("正在刷新会话列表");
        await LoadState();
    });

    private async Task LoadState()
    {
        Snapshot snapshot;
        ProcessState process;
        try { (snapshot, process) = await Task.Run(() => (cleaner.Snapshot(), cleaner.CheckProcesses())); }
        catch
        {
            state = null;
            selected.Clear();
            RenderChats();
            throw;
        }
        if (IsDisposed) return;
        state = snapshot;
        selected.IntersectWith(snapshot.Chats.Where(Selectable).Select(c => c.Id));
        diagnostics = snapshot.Warnings.Concat(snapshot.RecoveryIssues.Select(i =>
            (i.BlocksWrites ? "上次操作异常中断，清理已锁定。故障材料：" : "上次操作的临时副本未能移除：") + i.Directory + (i.Error.Length > 0 ? "。" + i.Error : ""))).ToList();
        diagnosticsButton.Text = $"查看检查详情（{diagnostics.Count}）";
        diagnosticsButton.Visible = diagnostics.Count > 0;
        SetProcess(process);
        RenderChats();
    }

    private const int InUseRefreshTicks = 3;
    private int quietChecks;

    // Background refresh: a failure keeps the current list instead of interrupting the user.
    // It does not set busy, so typing and selection continue; Run waits for it before acting.
    private Task RefreshQuietly()
    {
        if (busy || IsDisposed || OwnedForms.Length > 0 || refreshing is { IsCompleted: false }) return Task.CompletedTask;
        return refreshing = Refresh();

        async Task Refresh()
        {
            try
            {
                var snapshot = await Task.Run(cleaner.Snapshot);
                if (IsDisposed || busy) return;
                state = snapshot;
                selected.IntersectWith(snapshot.Chats.Where(Selectable).Select(c => c.Id));
                RenderChats();
            }
            catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        }
    }

    private Task? refreshing;

    private async Task RefreshProcessState()
    {
        if (busy || checkingProcess || IsDisposed) return;
        checkingProcess = true;
        try
        {
            var process = await Task.Run(cleaner.CheckProcesses);
            if (IsDisposed || busy) return;
            var changed = SetProcess(process);
            // Which chats Codex holds changes as it opens and closes them; keep the list current.
            if (changed || (codexRunning && ++quietChecks % InUseRefreshTicks == 0)) await RefreshQuietly();
        }
        catch (Exception e)
        {
            if (!IsDisposed)
            {
                processTitle.Text = "状态待确认";
                processDetail.Text = "执行前会重新检查。";
                processDot.ForeColor = DesktopTheme.Warning;
                tip.SetToolTip(processDetail, e.Message);
            }
        }
        finally { checkingProcess = false; }
    }

    // Returns whether Codex started or stopped since the last check.
    private bool SetProcess(ProcessState process)
    {
        var ready = !process.Running && process.Processes.Count == 0;
        var changed = codexRunning == ready;
        codexRunning = !ready;
        processDot.ForeColor = ready ? DesktopTheme.Green : DesktopTheme.Ink;
        processTitle.Text = ready ? "准备就绪" : "Codex 正在运行";
        processDetail.Text = ready ? "核对预览后即可清理" : "可删除未在使用的聊天 · 全局清理需先退出";
        tip.SetToolTip(processTitle, ready ? "未检测到 Codex 相关进程。" : string.Join(Environment.NewLine, process.Processes));
        tip.SetToolTip(processDetail, (ready ? processDetail.Text : "Codex 运行时，标为「使用中」的聊天不能勾选，其余聊天可以直接清理；桌面界面里的置顶、草稿等引用会在退出 Codex 后用「检查失效引用」清除。")
            + $"\n程序会自动复查运行状态。\n数据目录：{cleaner.DataRoot}");
        if (!changed) return false;
        if (codexRunning) clearMemories.Checked = clearShared.Checked = false;
        RenderSelection();
        return true;
    }

    private void ShowDiagnostics() => ShowTextDialog("本地检查详情", string.Join("\r\n\r\n", diagnostics.Select((d, i) => $"{i + 1}. {d}")));

    private void ShowTextDialog(string title, string content)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(720, 460), MinimumSize = new Size(540, 340), Font = Font, BackColor = DesktopTheme.Canvas, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Padding = new Padding(18) };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = Color.White, ForeColor = DesktopTheme.Ink, Text = content, AccessibleName = title };
        var close = DesktopTheme.Button("关闭");
        close.Dock = DockStyle.Bottom;
        close.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(text);
        dialog.Controls.Add(close);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private Task PreviewCleanup() => Run("正在生成清理预览", async () =>
    {
        var selection = new Selection([.. selected], clearMemories.Checked, clearShared.Checked)
        {
            AllowRecordOnly = state?.Chats.Any(c => selected.Contains(c.Id) && c.CanRemoveRecord) == true,
        };
        var plan = await Task.Run(() => cleaner.PlanCleanup(selection));
        SetActivity("正在估算备份空间");
        var backupBytes = await Task.Run(() => cleaner.EstimateBackupBytes(plan.Id));
        if (!Confirm(plan, backupBytes)) return;
        StartActivity("正在执行清理");
        var stages = new Progress<string>(SetActivity);
        var result = await Task.Run(() => cleaner.Execute(plan.Id, stages));
        selected.Clear();
        clearMemories.Checked = clearShared.Checked = false;
        await Done(result, (plan.Chats.Any(c => c.CanRemoveRecord) ? "所选本地记录已清理。预览中标明保留的原会话文件未被删除。" : "所选内容已清理完成。")
            + (plan.Live ? "\n\nCodex 运行期间没有修改桌面界面状态。退出 Codex 后点击「检查失效引用」，可清除置顶、草稿等残留引用。" : ""));
    });

    private Task PreviewRepair() => Run("正在检查失效引用", async () =>
    {
        var plan = await Task.Run(cleaner.PlanRepair);
        if (plan.IsEmpty || (plan.ReferenceRepairs == 0 && plan.Files.Count == 0 && plan.DatabaseRows == 0))
        {
            StopActivity();
            MessageBox.Show(this, "检查完成，没有发现可修复的失效引用。\n\n列表中的“仅清理记录”会话，可直接勾选后预览清理。", "检查完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Confirm(plan)) return;
        StartActivity("正在修复失效引用");
        var stages = new Progress<string>(SetActivity);
        await Done(await Task.Run(() => cleaner.ExecuteRepair(plan.Id, stages)), "失效引用已修复，可以重新启动 Codex 检查项目和归档。");
    });

    private bool Confirm(Plan plan, long? estimatedBackupBytes = null)
    {
        StopActivity();
        UseWaitCursor = false;
        try { using var dialog = new ConfirmDialog(plan, cleaner.CheckProcesses, estimatedBackupBytes); return dialog.ShowDialog(this) == DialogResult.OK; }
        finally { if (!IsDisposed) UseWaitCursor = true; }
    }

    private async Task Done(ExecuteResult result, string message)
    {
        if (result.BackupDirectory is not null) message += $"\n\n删除前的备份已保留：{result.BackupDirectory}\n退出 Codex 后，可在“备份与恢复”中找回。";
        if (result.ResidualDirectory is not null) message += $"\n\n临时事务副本未能完全删除：{result.ResidualDirectory}\n{result.ResidualError}";
        SetActivity("正在刷新会话列表");
        await LoadState();
        StopActivity();
        MessageBox.Show(this, message, "处理完成", MessageBoxButtons.OK, result.ResidualDirectory is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            updateCancellation.Cancel();
            updateCancellation.Dispose();
            processTimer.Stop();
            processTimer.Dispose();
            StopActivity();
            busyTimer.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() { DoubleBuffered = true; }
    }
}
