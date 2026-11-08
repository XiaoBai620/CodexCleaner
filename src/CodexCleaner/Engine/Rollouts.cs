using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZstdSharp;

namespace CodexCleaner.Engine;

// Physical rollouts grouped by the thread ID in their first session_meta line, plus direct fork
// edges (session_meta.history_base.thread_id), mirroring codex-rs/rollout RolloutReferenceIndex.
// A thread can own several files: continuation segments and cold .jsonl.zst copies.
internal sealed class RolloutIndex
{
    public Dictionary<string, List<(string Path, long Bytes)>> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> ForkChildren { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> FilesOf(string id) => Files.TryGetValue(id, out var list) ? list.Select(f => f.Path).ToList() : [];
    public long BytesOf(string id) => Files.TryGetValue(id, out var list) ? list.Sum(f => f.Bytes) : 0;

    // References from children that are deleted in the same request do not block it.
    public List<string> ExternalChildren(string id, IReadOnlySet<string> selection) =>
        ForkChildren.TryGetValue(id, out var children) ? children.Where(c => !selection.Contains(c)).Distinct().ToList() : [];
}

public sealed partial class Cleaner
{
    private const string CompressedSuffix = ".zst";

    internal RolloutIndex ScanRollouts()
    {
        var index = new RolloutIndex();
        var seenBase = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { "archived_sessions", "sessions" })
            if (Stat(root) is DirectoryInfo) Walk(root, index, seenBase);
        foreach (var list in index.Files.Values) list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return index;
    }

    private void Walk(string rel, RolloutIndex index, HashSet<string> seenBase)
    {
        foreach (var entry in ((DirectoryInfo)Stat(rel)!).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var child = $"{rel}/{entry.Name}";
            if (entry is DirectoryInfo) { Walk(child, index, seenBase); continue; }
            var plain = entry.Name.EndsWith(".jsonl" + CompressedSuffix, StringComparison.OrdinalIgnoreCase) ? entry.Name[..^CompressedSuffix.Length] : entry.Name;
            if (!plain.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) || !plain.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
            // Unreadable files are skipped, as RolloutReferenceIndex::scan does.
            try { if (!IndependentFile(entry)) continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var meta = ReadSessionMeta(child);
            if (meta is null) continue;
            if (!index.Files.TryGetValue(meta.Value.Id, out var files)) index.Files[meta.Value.Id] = files = [];
            files.Add((child, ((FileInfo)entry).Length));
            // Codex counts one history base per thread; a compressed sibling repeats its plain file.
            if (meta.Value.Base is { } parent && parent != meta.Value.Id && seenBase.Add(meta.Value.Id))
            {
                if (!index.ForkChildren.TryGetValue(parent, out var children)) index.ForkChildren[parent] = children = [];
                children.Add(meta.Value.Id);
            }
        }
    }

    // Returns the first session_meta, as codex read_session_meta_line does; unreadable files are skipped.
    internal (string Id, string? Base)? ReadSessionMeta(string rel)
    {
        try
        {
            using var file = File.Open(Data(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var stream = rel.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase) ? new DecompressionStream(file) : (Stream)file;
            foreach (var line in Lines(stream, Catalog.MaxJsonBytes))
            {
                if (line.Trim().Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line.TrimStart('\uFEFF')); }
                catch (JsonException) { continue; }
                switch (Json.Str(Json.Get(node, "type")))
                {
                    case "session_meta":
                        var payload = Json.Get(node, "payload");
                        return Json.Str(Json.Get(payload, "id")) is { } id && Ids.Valid(id)
                            ? (id, Json.Str(Json.Get(Json.Get(payload, "history_base"), "thread_id")))
                            : null;
                    case "response_item" or "inter_agent_communication": return null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ZstdException or InvalidDataException) { }
        return null;
    }

    private static IEnumerable<string> Lines(Stream stream, long limit)
    {
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Write(buffer, start, i - start);
                yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buffer, start, read - start);
            if (total > limit) throw new InvalidDataException("会话元数据行太长，无法验证。");
        }
        if (line.Length > 0) yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
    }
}
