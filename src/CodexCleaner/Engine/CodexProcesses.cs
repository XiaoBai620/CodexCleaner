using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexCleaner.Engine;

public static class CodexProcesses
{
    // Standalone CLI installs (npm, winget) run from arbitrary folders, so names still count.
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
        { "codex", "chatgpt", "codex-desktop", "codex-code-mode-host", "codex-app-server", "codex-command-runner" };

    // Long-lived helpers that never write chat data: the Store sandbox service starts with Windows,
    // and extension-host is the browser extension's native-messaging bridge, which the browser
    // restarts on demand. It only relays to codex.exe (still detected below) and links no SQLite.
    // Treating either as a writer would block every cleanup.
    private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase) { "codex-windows-sandbox-service", "extension-host" };
    private const string StorePackage = @"\WindowsApps\OpenAI.Codex_";

    public static ProcessState Detect(string dataRoot)
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
            using (process)
                if (IsCodex(process.ProcessName, ImagePath(process.Id), dataRoot)) found.Add($"{process.ProcessName} ({process.Id})");
        return new ProcessState(found.Count > 0, found);
    }

    internal static bool IsCodex(string name, string? path, string dataRoot)
    {
        if (Helpers.Contains(name)) return false;
        if (Names.Contains(name) || name.StartsWith("codex-command-runner", StringComparison.OrdinalIgnoreCase)) return true;
        return path is not null && (path.Contains(StorePackage, StringComparison.OrdinalIgnoreCase) || SafePaths.Inside(dataRoot, path));
    }

    private const uint QueryLimitedInformation = 0x1000;

    // Protected or other-session processes deny access; those are then judged by name only.
    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
