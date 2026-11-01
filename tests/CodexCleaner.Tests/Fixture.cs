using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexCleaner.Engine;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.Tests;

// Synthetic data only. Never point this fixture at a real CODEX_HOME.
public sealed class Fixture : IDisposable
{
    public static readonly string[] Ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"];
    public const string Stale = "33333333-3333-4333-8333-333333333333";
    private const string Prefix = "codex-cleaner-test-";

    private static readonly Dictionary<string, List<SchemaObject>> Schema =
        JsonSerializer.Deserialize<Dictionary<string, List<SchemaObject>>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture-schema.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public string Base { get; }
    public string DataRoot { get; }
    public string TrashRoot { get; }
    public string[] RolloutPaths { get; }
    public bool Running { get; set; }
    public Cleaner Cleaner { get; }

    public Fixture()
    {
        Base = Directory.CreateTempSubdirectory(Prefix).FullName;
        DataRoot = Path.Combine(Base, "synthetic-codex");
        TrashRoot = Path.Combine(Base, "transactions");
        try
        {
            RolloutPaths = Populate();
            Cleaner = new Cleaner(DataRoot, TrashRoot, () => new ProcessState(Running, Running ? ["codex (42)"] : []));
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(Base);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), Path.GetDirectoryName(full));
        Assert.StartsWith(Prefix, Path.GetFileName(full));
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private string[] Populate()
    {
        Directory.CreateDirectory(DataRoot);
        foreach (var (name, objects) in Schema)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DataRoot, name))!);
            using var db = Open(name);
            foreach (var type in new[] { "table", "index", "trigger", "view" })
                foreach (var o in objects.Where(x => x.Type == type)) Exec(db, o.Sql);
        }
        var rollouts = Ids.Select((id, i) => Put($"sessions/2026/09/27/rollout-{id}.jsonl",
            JsonSerializer.Serialize(new { timestamp = "2026-09-27T00:00:00Z", type = "session_meta", payload = new { id, cwd = Base } }) + "\n" +
            JsonSerializer.Serialize(new { timestamp = "2026-09-27T00:00:01Z", type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = $"Invented chat {i + 1}" } } } }) + "\n")).ToArray();
        using (var db = Open("state_5.sqlite"))
        {
            for (var i = 0; i < Ids.Length; i++)
            {
                Insert(db, "threads", new() { ["id"] = Ids[i], ["rollout_path"] = rollouts[i], ["created_at"] = 1780000000L + i, ["updated_at"] = 1780001000L + i, ["source"] = "cli", ["model_provider"] = "fixture", ["cwd"] = Base, ["title"] = $"Synthetic chat {i + 1}", ["sandbox_policy"] = "workspace-write", ["approval_mode"] = "on-request" });
                Insert(db, "thread_attachments", new() { ["id"] = $"attachment-{i}", ["thread_id"] = Ids[i], ["attachment_type"] = "file", ["identity_key"] = $"fake-{i}", ["payload"] = "{\"synthetic\":true}", ["created_at"] = 1L });
                Insert(db, "thread_dynamic_tools", new() { ["thread_id"] = Ids[i], ["position"] = 0L, ["name"] = "fixture", ["description"] = "Invented tool", ["input_schema"] = "{}" });
                Insert(db, "thread_read_receipts", new() { ["thread_id"] = Ids[i], ["first_unread_turn"] = "turn", ["revision"] = "1" });
                Insert(db, "guardian_review_feedback", new() { ["id"] = $"feedback-{i}", ["thread_id"] = Ids[i], ["record"] = Encoding.UTF8.GetBytes("{\"synthetic\":true}") });
            }
            Insert(db, "thread_spawn_edges", new() { ["parent_thread_id"] = Ids[0], ["child_thread_id"] = Ids[1], ["status"] = "completed" });
        }
        using (var db = Open("thread_history_1.sqlite"))
            foreach (var id in Ids)
            {
                Insert(db, "thread_turns", new() { ["thread_id"] = id, ["turn_id"] = "turn", ["rollout_ordinal"] = 1L, ["status"] = "completed" });
                Insert(db, "thread_items", new() { ["thread_id"] = id, ["turn_id"] = "turn", ["item_id"] = "item", ["rollout_ordinal"] = 1L, ["created_at_ms"] = 1L, ["item_json"] = "{\"text\":\"invented message\"}", ["item_type"] = "message" });
                Insert(db, "thread_realtime_items", new() { ["thread_id"] = id, ["item_id"] = "realtime", ["rollout_ordinal"] = 2L, ["created_at_ms"] = 2L, ["item_type"] = "message", ["item_json"] = "{\"text\":\"invented realtime\"}" });
                Insert(db, "thread_history_projection_state", new() { ["thread_id"] = id, ["next_rollout_byte_offset"] = 0L, ["next_rollout_ordinal"] = 3L });
            }
        using (var db = Open("memories_1.sqlite"))
        {
            foreach (var id in Ids) Insert(db, "stage1_outputs", new() { ["thread_id"] = id, ["source_updated_at"] = 1L, ["raw_memory"] = "Invented personal preference", ["rollout_summary"] = "Invented summary", ["generated_at"] = 1L });
            Insert(db, "jobs", new() { ["kind"] = "consolidation", ["job_key"] = "global", ["status"] = "complete", ["retry_remaining"] = 0L });
            Insert(db, "consolidation_progress", new() { ["singleton"] = 1L, ["max_thread_count"] = 2L });
        }
        using (var db = Open("goals_1.sqlite"))
            foreach (var id in Ids)
            {
                Insert(db, "thread_goals", new() { ["thread_id"] = id, ["goal_id"] = "goal", ["objective"] = "Invented goal", ["status"] = "complete", ["created_at_ms"] = 1L, ["updated_at_ms"] = 1L });
                Insert(db, "thread_goal_continuation_deferrals", new() { ["thread_id"] = id });
            }
        using (var db = Open("queue_1.sqlite"))
            for (var i = 0; i < Ids.Length; i++)
                Insert(db, "queued_items", new() { ["id"] = $"queued-{i}", ["thread_id"] = Ids[i], ["payload_json"] = "{\"text\":\"invented queued content\"}", ["queue_order"] = (long)i, ["created_at_ms"] = 1L, ["updated_at_ms"] = 1L });
        using (var db = Open("logs_2.sqlite"))
        {
            foreach (var id in Ids) Insert(db, "logs", new() { ["ts"] = 1L, ["ts_nanos"] = 1L, ["level"] = "INFO", ["target"] = "fixture", ["feedback_log_body"] = "Invented log", ["thread_id"] = id });
            Insert(db, "logs", new() { ["ts"] = 1L, ["ts_nanos"] = 1L, ["level"] = "INFO", ["target"] = "fixture", ["feedback_log_body"] = "Invented unlinked log", ["thread_id"] = null });
        }
        using (var db = Open("sqlite/codex-dev.db"))
        {
            Insert(db, "local_thread_catalog_hosts", new() { ["host_id"] = "local", ["host_kind"] = "local" });
            for (var i = 0; i < Ids.Length; i++)
            {
                Insert(db, "automation_runs", new() { ["thread_id"] = Ids[i], ["automation_id"] = $"run-{i}", ["status"] = "complete", ["thread_title"] = $"Synthetic chat {i + 1}", ["archived_user_message"] = "Invented archived input", ["archived_assistant_message"] = "Invented archived reply", ["created_at"] = 1L, ["updated_at"] = 1L });
                Insert(db, "inbox_items", new() { ["id"] = $"inbox-{i}", ["title"] = "Invented inbox title", ["description"] = "Invented inbox detail", ["thread_id"] = Ids[i], ["created_at"] = 1L });
                Insert(db, "live_visualization_suggestions", new() { ["account_id"] = "fixture", ["user_id"] = "fixture", ["host_id"] = "local", ["thread_id"] = Ids[i], ["id"] = $"suggestion-{i}", ["title"] = "Invented visualization title", ["description"] = "Invented visualization detail", ["status"] = "pending" });
                Insert(db, "local_thread_catalog", new() { ["host_id"] = "local", ["thread_id"] = Ids[i], ["display_title"] = $"Synthetic chat {i + 1}", ["source_created_at"] = 1L, ["source_updated_at"] = 1L, ["source_kind"] = "local", ["observation_sequence"] = 1L });
                Insert(db, "local_thread_catalog_scan_entries", new() { ["host_id"] = "local", ["thread_id"] = Ids[i], ["removed"] = 0L });
                Insert(db, "thread_timeline_ledger", new() { ["host_id"] = "local", ["thread_id"] = Ids[i], ["sequence"] = 1L, ["record_id"] = $"record-{i}", ["payload_json"] = "{\"text\":\"Invented timeline content\"}" });
            }
        }
        Put("session_index.jsonl", string.Concat(Ids.Select((id, i) => JsonSerializer.Serialize(new { id, thread_name = $"Synthetic chat {i + 1}", updated_at = "2026-09-27T00:00:00Z" }) + "\n")));
        Put(".codex-global-state.json", new JsonObject
        {
            ["electron-persisted-atom-state"] = new JsonObject
            {
                ["prompt-history"] = new JsonObject { [Ids[0]] = new JsonArray("invented selected input"), [Ids[1]] = new JsonArray("invented retained input"), ["global"] = new JsonArray("invented global input") },
                ["composer-prompt-drafts-v2"] = new JsonObject { [Ids[0]] = "Invented selected draft", [Ids[1]] = "Invented retained draft" },
            },
            ["saved-workspace-roots"] = new JsonArray(Base),
        }.ToJsonString());
        Put("history.jsonl", JsonSerializer.Serialize(new { session_id = Ids[0], ts = 1, text = "Invented history" }) + "\n");
        Put("memories/MEMORY.md", "Invented consolidated memory");
        Put("memories_v2/memory.txt", "Invented v2 memory");
        Put("memories_extensions/extension.txt", "Invented extension memory");
        Put("config.toml", "model = \"synthetic-model\"\n");
        Put("auth.json", "{\"syntheticCredential\":\"not-a-real-secret\"}");
        Put("plugins/example/README.md", "Unrelated plugin");
        Put("skills/example/SKILL.md", "Unrelated skill");
        Put("mystery-user-file.txt", "Keep unknown data");
        Put("log/codex-tui.log", "Invented shared text log");
        return rollouts;
    }

    public SqliteConnection Open(string name)
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(DataRoot, name)};Pooling=False");
        db.Open();
        return db;
    }

    public static void Exec(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // Fills every NOT NULL column without a default so fixtures survive additive schema changes.
    public static void Insert(SqliteConnection db, string table, Dictionary<string, object?> values)
    {
        var row = new Dictionary<string, object?>(values);
        foreach (var column in Query(db, $"PRAGMA table_info(\"{table}\")"))
        {
            var (name, type, notNull, defaultValue, pk) = ((string)column["name"]!, (string)column["type"]!, (long)column["notnull"]! == 1, column["dflt_value"], (long)column["pk"]! > 0);
            if (pk && !notNull && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)) continue;
            if (!row.ContainsKey(name) && (notNull || pk) && defaultValue is null)
                row[name] = type.Contains("INT", StringComparison.OrdinalIgnoreCase) || type.Contains("REAL", StringComparison.OrdinalIgnoreCase) || type.Contains("BOOL", StringComparison.OrdinalIgnoreCase) ? 1L
                    : type.Contains("BLOB", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetBytes("fixture") : "synthetic";
        }
        var names = row.Keys.ToList();
        Exec(db, $"INSERT INTO \"{table}\" ({string.Join(',', names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(',', names.Select((_, i) => "$p" + i))})", names.Select(n => row[n]).ToArray());
    }

    public static List<Dictionary<string, object?>> Query(SqliteConnection db, string sql, params object?[] args)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public List<Dictionary<string, object?>> Rows(string db, string table)
    {
        using var connection = Open(db);
        return Query(connection, $"SELECT * FROM \"{table}\"");
    }

    public string Put(string relative, string body) => PutAt(DataRoot, relative, body);

    public static string PutAt(string root, string relative, string body)
    {
        var file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body);
        return file;
    }

    public static string Checksum(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    public string Data(string rel) => Path.Combine(DataRoot, rel);

    public void AssertNoTransactions() =>
        Assert.Empty(Directory.Exists(TrashRoot) ? Directory.GetFileSystemEntries(TrashRoot) : []);

    private sealed record SchemaObject(string Name, string Type, string Sql);
}
