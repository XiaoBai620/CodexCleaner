using CodexCleaner.Engine;

namespace CodexCleaner.UpdatePreview;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9f));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(e.Exception.Message);
        PreviewData? data = null;
        try
        {
            var previewData = new PreviewData();
            data = previewData;
            var cleaner = new Cleaner(previewData.DataRoot, previewData.TransactionRoot, () => new ProcessState(false, []));
            MainForm? main = null;
            void ShowManualDownload(Uri _)
            {
                MessageBox.Show(main,
                    "这是虚构 v9.0.0 的更新演示。点击「下载并重启」可体验进度、取消和重试。\n\n演示不会联网下载、替换文件或重启程序。",
                    "更新功能演示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            using var form = new MainForm(cleaner, new PreviewUpdateChecker(), ShowManualDownload, update =>
            {
                using var dialog = new UpdateDialog(update, ShowManualDownload,
                    new PreviewUpdater(previewData.Root), new PreviewInstaller(), _ => { });
                dialog.Text = "更新功能演示 · 虚构 v9.0.0";
                if (dialog.ShowDialog(main) == DialogResult.OK)
                    MessageBox.Show(main,
                        "模拟更新已完成！正式更新会在校验通过后自动重启程序。\n\n本次演示没有替换文件，也没有重启。可以再次点击「查看并更新」继续体验。",
                        "更新演示完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            });
            main = form;
            form.Text = "更新功能演示 · 模拟数据 · CodexCleaner";
            var notice = DesktopTheme.Label("演示模式：虚构 v9.0.0 · 约 20 秒模拟下载 · 可取消并重新下载 · 不替换文件、不重启。", 8.5f);
            notice.Dock = DockStyle.Bottom;
            notice.Height = 32;
            notice.TextAlign = ContentAlignment.MiddleCenter;
            notice.BackColor = DesktopTheme.WarningFill;
            form.Controls.Add(notice);
            Application.Run(form);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            ShowError("演示未能启动。\n" + e.Message);
        }
        finally
        {
            if (data is not null)
            {
                try { data.Dispose(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                { ShowError($"演示临时目录未能清除，可关闭演示后手动删除：\n{data.Root}\n\n{e.Message}"); }
            }
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "更新功能演示", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
