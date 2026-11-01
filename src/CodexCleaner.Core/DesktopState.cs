using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexCleaner.Core;

// Desktop JSON state is edited field by field: only references with a verified local
// owner are touched, never arbitrary strings that happen to look like a chat ID.
internal sealed partial class DesktopState(string dataRoot)
{
    private const string LocalHost = "local";

    [GeneratedRegex(@"^local:[a-f0-9]{64}\z", RegexOptions.IgnoreCase)]
    private static partial Regex AdoptedHost();

    private bool IsLocalHost(string? host) =>
        host == LocalHost || (host is not null && host.StartsWith("local:", StringComparison.Ordinal) && SafePaths.Same(SafePaths.Normal(host[6..]), dataRoot));

    private bool ReadHostIsLocal(JsonNode? reads, string host)
    {
        if (IsLocalHost(host)) return true;
        var adopted = Json.Str(Json.Get(Json.Get(Json.Get(reads, "legacyMigration"), "adoptedHostIds"), LocalHost));
        return adopted is not null && AdoptedHost().IsMatch(adopted) && host == adopted;
    }

    private string? RecentThreadId(JsonNode? entry, JsonNode? bindings)
    {
        if (!IsLocalHost(Json.Str(Json.Get(entry, "hostId"))) || Json.Str(Json.Get(entry, "threadId")) is not { } thread) return null;
        if (thread.StartsWith("local:", StringComparison.Ordinal)) return Ids.Valid(thread[6..]) ? thread[6..] : null;
        if (Ids.Valid(thread)) return thread;
        return thread.StartsWith("client-new-thread:", StringComparison.Ordinal) && Json.Str(Json.Get(bindings, thread)) is { } bound && Ids.Valid(bound) ? bound : null;
    }

    private static IEnumerable<JsonObject> Containers(JsonNode root) =>
        new[] { root, Json.Get(root, "electron-persisted-atom-state") }.OfType<JsonObject>();

    public HashSet<string> LocalIds(JsonNode root)
    {
        var ids = new HashSet<string>();
        void Add(string? id) { if (Ids.Valid(id)) ids.Add(id!); }
        void List(JsonNode? array) { if (array is JsonArray a) foreach (var item in a) Add(Json.Str(item)); }
        foreach (var o in Containers(root))
        {
            foreach (var (host, state) in Json.Properties(Json.Get(o, "app-server-projects-migration-by-host")))
                if (IsLocalHost(host)) List(Json.Get(state, "pendingThreadAssignmentIds"));
            var reads = Json.Get(o, "electron-thread-read-state-v1");
            foreach (var (_, hosts) in Json.Properties(Json.Get(reads, "unreadByIdentity")))
                foreach (var (host, array) in Json.Properties(hosts)) if (ReadHostIsLocal(reads, host)) List(array);
            foreach (var (host, array) in Json.Properties(Json.Get(Json.Get(reads, "legacyMigration"), "unreadThreadIdsByHostId")))
                if (IsLocalHost(host)) List(array);
            var bindings = Json.Get(o, "client-thread-bindings-v1");
            foreach (var (_, entry) in Json.Properties(Json.Get(o, "recent-workspace-files-v3"))) Add(RecentThreadId(entry, bindings));
            foreach (var key in o.Select(p => p.Key))
                if (key.StartsWith("thread-client-id-v1:local%3A", StringComparison.Ordinal)) Add(key["thread-client-id-v1:local%3A".Length..]);
        }
        return ids;
    }

    public (bool Changed, int References) Clean(JsonNode root, IReadOnlySet<string> ids, bool shared)
    {
        var removed = 0;
        var clearedGlobal = false;
        void RemoveKeys(JsonNode? map) { if (map is JsonObject o) foreach (var id in ids) if (o.Remove(id)) removed++; }
        void RemoveItems(JsonNode? parent, string name)
        {
            if (Json.Get(parent, name) is not JsonArray array) return;
            for (var i = array.Count - 1; i >= 0; i--) if (Json.Str(array[i]) is { } s && ids.Contains(s)) { array.RemoveAt(i); removed++; }
        }
        foreach (var o in Containers(root))
        {
            foreach (var name in Catalog.DesktopMapKeys) RemoveKeys(Json.Get(o, name));
            foreach (var name in Catalog.DesktopListKeys) RemoveItems(o, name);
            if (shared && Json.Get(o, "prompt-history") is JsonObject history && history.Remove("global")) clearedGlobal = true;
            foreach (var id in ids) foreach (var prefix in Catalog.DesktopKeyPrefixes) if (o.Remove(prefix + id)) removed++;
            var bindings = Json.Get(o, "client-thread-bindings-v1");
            foreach (var (host, migration) in Json.Properties(Json.Get(o, "app-server-projects-migration-by-host")))
                if (IsLocalHost(host)) RemoveItems(migration, "pendingThreadAssignmentIds");
            var reads = Json.Get(o, "electron-thread-read-state-v1");
            foreach (var (_, hosts) in Json.Properties(Json.Get(reads, "unreadByIdentity")))
                foreach (var (host, _) in Json.Properties(hosts)) if (ReadHostIsLocal(reads, host)) RemoveItems(hosts, host);
            var legacy = Json.Get(Json.Get(reads, "legacyMigration"), "unreadThreadIdsByHostId");
            foreach (var (host, _) in Json.Properties(legacy)) if (IsLocalHost(host)) RemoveItems(legacy, host);
            if (Json.Get(o, "recent-workspace-files-v3") is JsonObject recent)
                foreach (var (key, entry) in recent.ToList())
                    if (RecentThreadId(entry, bindings) is { } id && ids.Contains(id)) { recent.Remove(key); removed++; }
            if (bindings is JsonObject b)
                foreach (var (key, value) in b.ToList())
                    if (Json.Str(value) is { } id && ids.Contains(id)) { b.Remove(key); removed++; }
        }
        return (removed > 0 || clearedGlobal, removed);
    }

    public static (JsonNode Root, byte[] Bytes) Read(string file, string rel)
    {
        var info = new FileInfo(file);
        if (info.Length > Catalog.MaxJsonBytes) throw new CleanerException($"无法安全解析桌面状态：{rel}");
        var bytes = File.ReadAllBytes(file);
        JsonNode? root;
        try { root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')); }
        catch (JsonException) { throw new CleanerException($"桌面状态 JSON 无法解析，停止清理：{rel}"); }
        return root is JsonObject ? (root, bytes) : throw new CleanerException($"桌面状态结构未知：{rel}");
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Serialize(JsonNode root) => Encoding.UTF8.GetBytes(root.ToJsonString(Indented) + "\n");
}
