using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CodexCleaner.Engine;

public sealed class CleanerException(string message, Exception? inner = null) : Exception(message, inner);

internal static partial class Ids
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}\z")]
    private static partial Regex Pattern();

    public static bool Valid(string? value) => value is not null && Pattern().IsMatch(value);
}

internal static class SafePaths
{
    public static string Normal(string value)
    {
        var unprefixed = value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + value[8..]
            : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
        return Path.GetFullPath(unprefixed);
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool Inside(string root, string target)
    {
        var rel = Path.GetRelativePath(root, target);
        return rel == "." || (rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    public static string ValidRelative(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Contains('\0') || Path.IsPathRooted(rel) || (rel.Length >= 2 && char.IsAsciiLetter(rel[0]) && rel[1] == ':'))
            throw new CleanerException("路径必须是数据目录内的相对路径。");
        var parts = rel.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => c == ':' || c < 0x20) || p.EndsWith('.') || p.EndsWith(' ')))
            throw new CleanerException("路径含有不允许的部分。");
        return string.Join('/', parts);
    }
}

internal static class FileLinks
{
    // .NET has no hard-link count API; a second link would let a delete or rewrite reach data outside the root.
    public static uint Count(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.NumberOfLinks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}

internal static class Json
{
    public static JsonNode? Get(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value : null;

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static IEnumerable<KeyValuePair<string, JsonNode?>> Properties(JsonNode? node) => node is JsonObject o ? o.ToList() : [];
}
