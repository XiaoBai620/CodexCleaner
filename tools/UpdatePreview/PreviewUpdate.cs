namespace CodexCleaner.UpdatePreview;

internal sealed class PreviewUpdateChecker : IUpdateChecker
{
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken);
        return new AvailableUpdate(new Version(9, 0, 0), new Uri("https://preview.invalid/releases/v9.0.0"),
            """
            ## 更新摘要（虚构版本，仅供体验）
            - 在程序内查看完整更新说明。
            - 下载时显示进度，可取消并重新下载。

            完整说明
            1. 主界面显示版本提醒和两条简要更新内容。
            2. 点击「查看并更新」，在此窗口阅读本次更新说明；文字可以选择和复制。
            3. 点击「下载并重启」，观察约 20 秒的模拟下载和进度变化。
            4. 下载中点击「取消下载」，窗口保留，可点击「重新下载」继续体验。
            5. 模拟下载完成后会显示成功提示，可回到主窗口再次点击「查看并更新」。

            演示边界
            v9.0.0 是虚构版本，并非 GitHub 正式发布。
            此演示使用与正式程序相同的更新界面，检查、下载和安装均由模拟实现提供。
            演示不会访问真实聊天目录，不会联网下载，不会替换或重启程序。
            主界面中的两条会话均为临时生成的虚构数据，退出后清除。
            """,
            new UpdatePackage(new Uri("https://preview.invalid/CodexCleaner.exe"),
                new Uri("https://preview.invalid/SHA256SUMS"), PreviewUpdater.TotalBytes));
    }
}

internal sealed class PreviewUpdater(string root) : IAppUpdater
{
    internal const long TotalBytes = 4 * 1024 * 1024;

    public async Task<PreparedUpdate> DownloadAsync(AvailableUpdate update,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        const int steps = 200;
        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(100, cancellationToken);
            progress?.Report(new UpdateProgress(TotalBytes * step / steps, TotalBytes, "正在下载更新（模拟）…"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        // No executable is created. Only the injected PreviewInstaller sees these paths.
        return new PreparedUpdate(root, Path.Combine(root, "simulated-update.exe"), new string('0', 64), update.Version);
    }
}

internal sealed class PreviewInstaller : IUpdateInstaller
{
    // Returning successfully exercises the real dialog's completion path without installing anything.
    public void Start(PreparedUpdate _) { }
}
