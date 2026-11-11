using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexCleaner.Engine;
using Microsoft.Win32.SafeHandles;

namespace CodexCleaner;

internal interface IUpdateInstaller
{
    void Start(PreparedUpdate update);
}

internal sealed class UpdateInstallException(string message, bool preserveFiles = false, Exception? inner = null)
    : IOException(message, inner)
{
    public bool PreserveFiles { get; } = preserveFiles;
}

internal sealed record UpdateInstallRequest(
    string Directory, string TargetPath, string OldSha256, string NewSha256, string Version,
    string WorkingDirectory, int ParentId, long ParentStartTicks, string Nonce);

internal sealed class UpdateInstaller : IUpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string CompleteArgument = "--complete-update";
    private const int ManifestLimit = 16 * 1024;
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2);
    private static (UpdateInstallRequest Request, Process Helper)? pendingCompletion;
    private static bool handoffCommitted;

    public void Start(PreparedUpdate update)
    {
        Process? helper = null;
        UpdateInstallRequest? request = null;
        try
        {
            var target = Environment.ProcessPath ?? throw new IOException("无法确定当前程序的位置，请从发布的 exe 启动后更新。");
            ValidateFile(target);
            AppUpdater.ValidateExecutable(target, UpdateChecker.CurrentVersion);
            if (update.Version <= UpdateChecker.CurrentVersion) throw new InvalidDataException("下载的版本不是更新版本。");
            using var parent = Process.GetCurrentProcess();
            request = new UpdateInstallRequest(Normal(update.Directory), Normal(target), Hash(target), update.Sha256,
                update.Version.ToString(3), Environment.CurrentDirectory, parent.Id,
                parent.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"));
            ValidateRequest(request, AppUpdater.IsOwnedDirectory);
            if (!SafePaths.Same(Normal(update.ExecutablePath), SourcePath(request)))
                throw new InvalidDataException("下载文件的位置不正确，请重新下载。");
            VerifyFile(SourcePath(request), request.NewSha256);
            AppUpdater.ValidateExecutable(SourcePath(request), ParseVersion(request.Version));
            ProbeTargetDirectory(request);

            CopyNew(request.TargetPath, HelperPath(request));
            VerifyFile(HelperPath(request), request.OldSha256);
            WriteNew(ManifestPath(request), JsonSerializer.Serialize(request));
            var start = new ProcessStartInfo(HelperPath(request))
            {
                UseShellExecute = false,
                WorkingDirectory = request.Directory,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(ApplyArgument);
            start.ArgumentList.Add(ManifestPath(request));
            start.ArgumentList.Add(request.Nonce);
            helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
            var deadline = Stopwatch.StartNew();
            while (!HasMarker(request, "ready"))
            {
                if (helper.HasExited) throw new IOException("更新助手未能启动，当前程序保持不变。请使用发布页手动更新。");
                if (deadline.Elapsed > PreparationTimeout) throw new IOException("更新助手准备超时，当前程序保持不变。请重试或手动更新。");
                Thread.Sleep(50);
            }
            if (helper.HasExited) throw new IOException("更新助手已退出，请重试更新。");
            // The helper must authenticate the live parent before this explicit commit can be issued.
            WriteNew(MarkerPath(request, "commit"), request.Nonce);
        }
        catch (Exception error)
        {
            var stopped = StopHelper(helper, request);
            if (!stopped)
                throw new UpdateInstallException("更新准备未完成，更新助手尚未退出。请保留更新文件并关闭程序后重试。", true, error);
            if (error is UpdateInstallException) throw;
            throw new UpdateInstallException($"无法准备更新：{error.Message}\n当前程序保持不变，可在发布页下载新版后手动替换。", false, error);
        }
        finally { helper?.Dispose(); }
    }

    // Helpers are handled before Cleaner is constructed: they never open application data.
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 0 && args[0] == ApplyArgument)
        {
            try
            {
                if (args.Length != 3) throw new InvalidDataException("更新助手参数无效。");
                RunHelper(args[1], args[2]);
            }
            catch (Exception error)
            {
                // Preparation errors are reported by the still-running parent. After handoff the helper owns error UI.
                if (handoffCommitted)
                    MessageBox.Show($"更新未能完成：{error.Message}\n请重新打开原程序，或从 GitHub 发布页下载新版。",
                        "CodexCleaner 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }
        if (args.Length != 0 && args[0] == CompleteArgument)
        {
            // Invalid cleanup arguments are ignored; they never grant arbitrary file deletion.
            try
            {
                if (args.Length == 5 && int.TryParse(args[3], out var helperId) && long.TryParse(args[4], out var helperTicks))
                {
                    if (CreatingParentId() != helperId) return false;
                    var request = ReadRequest(args[1], args[2], AppUpdater.IsOwnedDirectory);
                    if (!SafePaths.Same(Normal(Environment.ProcessPath ?? ""), request.TargetPath)) return false;
                    VerifyFile(request.TargetPath, request.NewSha256);
                    AppUpdater.ValidateExecutable(request.TargetPath, ParseVersion(request.Version));
                    VerifyFile(HelperPath(request), request.OldSha256);
                    var helper = FindProcess(helperId, helperTicks, HelperPath(request));
                    // Cleanup only follows a still-live, authenticated helper process.
                    if (helper is not null) pendingCompletion = (request, helper);
                }
            }
            catch (Exception) { /* Retain materials if ownership or process identity cannot be proven. */ }
        }
        return false;
    }

    internal static void ConfirmStartup()
    {
        var pending = pendingCompletion;
        pendingCompletion = null;
        if (pending is null) return;
        try
        {
            VerifyFile(pending.Value.Request.TargetPath, pending.Value.Request.NewSha256);
            WriteNew(MarkerPath(pending.Value.Request, "complete"), pending.Value.Request.Nonce);
            _ = Task.Run(() => Complete(pending.Value.Request, pending.Value.Helper));
        }
        catch (Exception) { pending.Value.Helper.Dispose(); }
    }

    private static void RunHelper(string manifestPath, string nonce)
    {
        var request = ReadRequest(manifestPath, nonce, AppUpdater.IsOwnedDirectory);
        if (!SafePaths.Same(Normal(Environment.ProcessPath ?? ""), HelperPath(request)))
            throw new InvalidDataException("更新助手必须从已验证的下载目录运行。");
        if (ParseVersion(request.Version) <= UpdateChecker.CurrentVersion)
            throw new InvalidDataException("下载的版本不是更新版本。");
        VerifyFile(HelperPath(request), request.OldSha256);
        AppUpdater.ValidateExecutable(HelperPath(request), UpdateChecker.CurrentVersion);
        if (CreatingParentId() != request.ParentId)
            throw new InvalidDataException("更新助手不是由本次原程序启动的。");
        using var parent = FindProcess(request.ParentId, request.ParentStartTicks, request.TargetPath)
            ?? throw new InvalidDataException("原程序已退出或身份发生变化，请重新打开程序后更新。");
        // Force a process handle to be acquired before ready; PID reuse cannot satisfy this wait.
        _ = parent.Handle;
        using var updateLock = AcquireLock(request.TargetPath);
        VerifyFile(request.TargetPath, request.OldSha256);
        VerifyFile(SourcePath(request), request.NewSha256);
        AppUpdater.ValidateExecutable(SourcePath(request), ParseVersion(request.Version));
        WriteNew(MarkerPath(request, "ready"), request.Nonce);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (HasMarker(request, "cancel")) return;
            var committed = HasMarker(request, "commit");
            if (parent.WaitForExit(100))
            {
                if (!committed && !HasMarker(request, "commit")) return;
                break;
            }
            if (wait.Elapsed > ExitTimeout)
                throw new IOException("原程序未能在两分钟内退出，更新已取消，原版本保持不变。");
        }
        if (HasMarker(request, "cancel")) return;
        handoffCommitted = true;
        Apply(request, AppUpdater.IsOwnedDirectory, AppUpdater.ValidateExecutable, Restart);
    }

    internal static void Apply(UpdateInstallRequest request, Func<string, bool> ownsDirectory,
        Action<string, Version> validateExecutable, Action<UpdateInstallRequest> restart)
    {
        ValidateRequest(request, ownsDirectory);
        using var updateLock = AcquireLock(request.TargetPath);
        var newPath = NewPath(request);
        var backupPath = BackupPath(request);
        var replaced = false;
        var replacementAttempted = false;
        var createdNew = false;
        try
        {
            EnsureMissing(newPath);
            EnsureMissing(backupPath);
            VerifyFile(SourcePath(request), request.NewSha256);
            validateExecutable(SourcePath(request), ParseVersion(request.Version));
            VerifyFile(request.TargetPath, request.OldSha256);
            CopyNew(SourcePath(request), newPath);
            createdNew = true;
            VerifyFile(newPath, request.NewSha256);
            validateExecutable(newPath, ParseVersion(request.Version));
            ValidateDirectory(Path.GetDirectoryName(request.TargetPath)!);
            // Hold the original file against writes until Replace; delete sharing permits the rename.
            using (var original = OpenRead(request.TargetPath, allowDelete: true))
            {
                VerifyStream(original, request.OldSha256);
                ValidateFile(request.TargetPath);
                EnsureMissing(backupPath);
                replacementAttempted = true;
                File.Replace(newPath, request.TargetPath, backupPath);
            }
            replaced = true;
            VerifyFile(backupPath, request.OldSha256);
            VerifyFile(request.TargetPath, request.NewSha256);
            validateExecutable(request.TargetPath, ParseVersion(request.Version));
            restart(request);
        }
        catch (Exception error)
        {
            if (error is UnconfirmedRestartException)
                throw new UpdateInstallException($"新版启动结果无法确认，已保留原版本备份：{backupPath}\n请关闭程序后手动处理。", true, error);
            if (replacementAttempted && !replaced)
            {
                try { VerifyFile(request.TargetPath, request.OldSha256); }
                catch (Exception)
                {
                    throw new UpdateInstallException($"更新替换结果无法确认，请保留更新材料与原程序备份：{backupPath}", true, error);
                }
            }
            if (replaced)
            {
                try
                {
                    VerifyFile(request.TargetPath, request.NewSha256);
                    VerifyFile(backupPath, request.OldSha256);
                    File.Replace(backupPath, request.TargetPath, null);
                    VerifyFile(request.TargetPath, request.OldSha256);
                }
                catch (Exception rollback)
                {
                    throw new UpdateInstallException($"更新失败且无法确认回退结果，请保留更新材料与原程序备份：{backupPath}\n{rollback.Message}", true, error);
                }
                throw new UpdateInstallException("新版启动失败，已恢复原版本；请重新打开程序后重试。", true, error);
            }
            if (createdNew)
            {
                try { VerifyFile(newPath, request.NewSha256); File.Delete(newPath); }
                catch (Exception) { /* Do not remove a sibling whose contents or identity changed. */ }
            }
            throw new UpdateInstallException($"更新未写入，原版本保持不变：{error.Message}", true, error);
        }
    }

    private static void Restart(UpdateInstallRequest request)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(request.TargetPath)
        {
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory,
        };
        start.ArgumentList.Add(CompleteArgument);
        start.ArgumentList.Add(ManifestPath(request));
        start.ArgumentList.Add(request.Nonce);
        start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new IOException("无法重新启动新版程序。");
        try
        {
            var wait = Stopwatch.StartNew();
            while (!HasMarker(request, "complete"))
            {
                if (child.WaitForExit(100)) throw new IOException("新版程序在启动完成前退出。");
                if (wait.Elapsed > PreparationTimeout) throw new IOException("新版程序未能确认启动。");
            }
        }
        catch
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    if (!child.WaitForExit(5000)) throw new IOException("新版程序尚未退出。");
                }
            }
            catch (Exception stopError) { throw new UnconfirmedRestartException("新版程序启动失败且无法确认退出。", stopError); }
            throw;
        }
    }

    private sealed class UnconfirmedRestartException(string message, Exception? inner = null) : IOException(message, inner);

    private static void Complete(UpdateInstallRequest request, Process helper)
    {
        using var helperHandle = helper;
        try
        {
            if (!helper.WaitForExit(15000)) return;
            using var updateLock = AcquireLock(request.TargetPath);
            ValidateRequest(request, AppUpdater.IsOwnedDirectory);
            VerifyFile(request.TargetPath, request.NewSha256);
            AppUpdater.ValidateExecutable(request.TargetPath, ParseVersion(request.Version));
            VerifyFile(BackupPath(request), request.OldSha256);
            File.Delete(BackupPath(request));
            AppUpdater.Discard(new PreparedUpdate(request.Directory, SourcePath(request), request.NewSha256, ParseVersion(request.Version)));
        }
        catch (Exception) { /* Keeping a verified old copy is safer than broad startup cleanup. */ }
    }

    internal static UpdateInstallRequest ReadRequest(string manifestPath, string nonce, Func<string, bool> ownsDirectory)
    {
        var path = Normal(manifestPath);
        if (!SafePaths.Same(Path.GetFileName(path), "install.json")) throw new InvalidDataException("更新清单位置无效。");
        var directory = Path.GetDirectoryName(path)!;
        if (!ownsDirectory(directory)) throw new InvalidDataException("更新目录无法验证。");
        ValidateFile(path);
        using var stream = OpenRead(path);
        if (stream.Length > ManifestLimit) throw new InvalidDataException("更新清单过大。");
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(stream) ?? throw new InvalidDataException("更新清单无效。");
        ValidateRequest(request, ownsDirectory);
        if (!SafePaths.Same(directory, request.Directory) || request.Nonce != nonce)
            throw new InvalidDataException("更新清单与本次更新不匹配。");
        return request;
    }

    private static void ValidateRequest(UpdateInstallRequest request, Func<string, bool> ownsDirectory)
    {
        if (request is null || request.Nonce is null || request.Nonce.Length != 32 ||
            request.Nonce.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !IsHash(request.OldSha256) || !IsHash(request.NewSha256) || request.ParentId <= 0 || request.ParentStartTicks <= 0)
            throw new InvalidDataException("更新清单字段无效。");
        _ = ParseVersion(request.Version);
        if (!SafePaths.Same(Normal(request.Directory), request.Directory) || !ownsDirectory(request.Directory))
            throw new InvalidDataException("更新目录无法验证。");
        if (!SafePaths.Same(Normal(request.TargetPath), request.TargetPath) ||
            !request.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            SafePaths.Inside(request.Directory, request.TargetPath))
            throw new InvalidDataException("原程序位置无效。");
        ValidateFile(request.TargetPath);
        ValidateDirectory(request.WorkingDirectory);
    }

    private static Version ParseVersion(string value)
    {
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != value)
            throw new InvalidDataException("更新版本号无效。");
        return version;
    }

    private static Process? FindProcess(int id, long startTicks, string path)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(id);
            _ = process.Handle;
            using var current = Process.GetCurrentProcess();
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks &&
                startTicks <= current.StartTime.ToUniversalTime().Ticks &&
                SafePaths.Same(Normal(process.MainModule?.FileName ?? ""), path)) return process;
        }
        catch (Exception) { /* Exited, inaccessible and reused processes cannot authenticate. */ }
        process?.Dispose();
        return null;
    }

    private static int CreatingParentId()
    {
        using var current = Process.GetCurrentProcess();
        if (NtQueryInformationProcess(current.Handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 ||
            info.ParentProcessId.ToInt64() is <= 0 or > int.MaxValue)
            throw new IOException("无法验证更新进程的创建来源。");
        return (int)info.ParentProcessId.ToInt64();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2, Reserved3, ProcessId, ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnLength);

    private static bool StopHelper(Process? helper, UpdateInstallRequest? request)
    {
        if (helper is null) return true;
        try
        {
            if (helper.HasExited) return true;
            if (request is not null && !File.Exists(MarkerPath(request, "cancel")))
                WriteNew(MarkerPath(request, "cancel"), request.Nonce);
            if (helper.WaitForExit(1500)) return true;
            // Only terminate the specific helper process we created, never the parent application.
            helper.Kill();
            return helper.WaitForExit(5000);
        }
        catch (Exception)
        {
            try { return helper.HasExited; }
            catch (Exception) { return false; }
        }
    }

    private sealed class UpdateLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static UpdateLock AcquireLock(string target)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Local\CodexCleaner.Update." + key);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一个更新正在处理此程序，请稍后重试。");
            return new UpdateLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private static void ProbeTargetDirectory(UpdateInstallRequest request)
    {
        if (File.GetAttributes(request.TargetPath).HasFlag(FileAttributes.ReadOnly))
            throw new IOException("当前程序文件为只读，不能自动替换；请取消只读属性或手动更新。");
        // Request DELETE access without deleting anything. A directory probe alone does not establish replacement rights.
        using (var deleteAccess = CreateFile(request.TargetPath, 0x00010000, 0x00000007, IntPtr.Zero, 3, 0, IntPtr.Zero))
            if (deleteAccess.IsInvalid)
                throw new IOException("当前程序位置不允许替换，请将程序放到可写目录或手动更新。", new Win32Exception(Marshal.GetLastWin32Error()));
        var path = Path.Combine(Path.GetDirectoryName(request.TargetPath)!, $".codexcleaner-update-{request.Nonce}.probe");
        var created = false;
        try { WriteNew(path, request.Nonce); created = true; VerifyFile(request.TargetPath, request.OldSha256); }
        finally
        {
            if (created) { ValidateFile(path); File.Delete(path); }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    internal static void ValidateFile(string path)
    {
        path = Normal(path);
        ValidateDirectory(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || FileLinks.Count(path) != 1)
            throw new InvalidDataException("更新拒绝链接、目录或非独立文件。");
    }

    private static void ValidateDirectory(string path)
    {
        var directory = new DirectoryInfo(Normal(path));
        for (var current = directory; current is not null; current = current.Parent)
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("更新目录不存在或包含目录链接。");
    }

    private static string Normal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl))
            throw new InvalidDataException("更新只支持普通本地文件路径。");
        var normal = Path.GetFullPath(path);
        if (normal.Split(Path.DirectorySeparatorChar).Skip(1).Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("更新路径包含不明确的文件名。");
        return normal;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static FileStream OpenRead(string path, bool allowDelete = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | (allowDelete ? FileShare.Delete : FileShare.None));
    private static string Hash(string path)
    {
        ValidateFile(path);
        using var stream = OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyStream(Stream stream, string expected)
    {
        if (!IsHash(expected) || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件校验失败或原程序已发生变化。");
    }

    private static void VerifyFile(string path, string expected)
    {
        ValidateFile(path);
        using var stream = OpenRead(path);
        VerifyStream(stream, expected);
    }

    private static void CopyNew(string source, string destination)
    {
        ValidateFile(source);
        ValidateDirectory(Path.GetDirectoryName(destination)!);
        using var input = OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void WriteNew(string path, string content)
    {
        ValidateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(content));
        stream.Flush(true);
    }

    private static bool HasMarker(UpdateInstallRequest request, string name)
    {
        var path = MarkerPath(request, name);
        if (!File.Exists(path)) return false;
        try
        {
            ValidateFile(path);
            using var stream = OpenRead(path);
            if (stream.Length < 32) return false;
            if (stream.Length > 32) throw new InvalidDataException("更新交接标记无效。");
            using var reader = new StreamReader(stream, Encoding.UTF8, false);
            if (reader.ReadToEnd() != request.Nonce) throw new InvalidDataException("更新交接标记不匹配。");
            return true;
        }
        // The path appears before WriteNew closes its exclusive, flushed stream. Poll until it closes.
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
    }

    private static void EnsureMissing(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("更新临时位置已被占用，请重新下载更新。");
    }

    private static string SourcePath(UpdateInstallRequest request) => Path.Combine(request.Directory, "CodexCleaner.exe");
    private static string HelperPath(UpdateInstallRequest request) => Path.Combine(request.Directory, "helper.exe");
    private static string ManifestPath(UpdateInstallRequest request) => Path.Combine(request.Directory, "install.json");
    private static string MarkerPath(UpdateInstallRequest request, string name) => Path.Combine(request.Directory, name);
    internal static string NewPath(UpdateInstallRequest request) => Path.Combine(Path.GetDirectoryName(request.TargetPath)!, $".codexcleaner-update-{request.Nonce}.new");
    internal static string BackupPath(UpdateInstallRequest request) => Path.Combine(Path.GetDirectoryName(request.TargetPath)!, $".codexcleaner-update-{request.Nonce}.old");
}
