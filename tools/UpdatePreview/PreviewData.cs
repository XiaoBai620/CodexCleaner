using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.UpdatePreview;

// This fixture is owned entirely by the preview process. Never resolve CODEX_HOME.
internal sealed class PreviewData : IDisposable
{
    private const string Prefix = "codexcleaner-update-preview-";
    private readonly string tempParent;
    public string Root { get; }
    public string DataRoot => Path.Combine(Root, "synthetic-codex");
    public string TransactionRoot => Path.Combine(Root, "transactions");

    internal PreviewData()
    {
        tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        Root = Directory.CreateTempSubdirectory(Prefix).FullName;
        try { Populate(); }
        catch
        {
            try { Dispose(); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or InvalidOperationException)
            { throw new IOException($"创建演示数据失败，临时目录可能需要手动清除：{Root}", cleanupError); }
            throw;
        }
    }

    private void Populate()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(TransactionRoot);
        using var stream = typeof(PreviewData).Assembly.GetManifestResourceStream("CodexCleaner.UpdatePreview.fixture-schema.json")
            ?? throw new InvalidOperationException("缺少演示数据库结构。");
        var schemas = JsonSerializer.Deserialize<Dictionary<string, List<SchemaObject>>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("演示数据库结构无法读取。");
        foreach (var (name, objects) in schemas)
        {
            var databasePath = OwnedPath(Path.Combine(DataRoot, name));
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            using var db = Open(databasePath);
            using var transaction = db.BeginTransaction();
            foreach (var type in new[] { "table", "index", "trigger", "view" })
                foreach (var item in objects.Where(item => item.Type == type))
                {
                    using var command = db.CreateCommand();
                    command.CommandText = item.Sql;
                    command.ExecuteNonQuery();
                }
            transaction.Commit();
        }

        string[] ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"];
        string[] titles = ["虚构会话：体验更新摘要", "虚构会话：体验下载与取消"];
        using var state = Open(OwnedPath(Path.Combine(DataRoot, "state_5.sqlite")));
        using var stateTransaction = state.BeginTransaction();
        for (var i = 0; i < ids.Length; i++)
        {
            var path = OwnedPath(Path.Combine(DataRoot, "sessions", "preview", $"rollout-{ids[i]}.jsonl"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:00Z", type = "session_meta", payload = new { id = ids[i], cwd = Root } }) + "\n" +
                JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:01Z", type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "这是更新界面演示生成的虚构对话。" } } } }) + "\n");
            using var insert = state.CreateCommand();
            insert.CommandText = """
                INSERT INTO threads (id, rollout_path, created_at, updated_at, source, model_provider,
                    cwd, title, sandbox_policy, approval_mode, has_user_event)
                VALUES ($id, $path, $time, $time, 'cli', 'preview', $cwd, $title, 'workspace-write', 'on-request', 1)
                """;
            insert.Parameters.AddWithValue("$id", ids[i]);
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$time", 1767225600L + i);
            insert.Parameters.AddWithValue("$cwd", Root);
            insert.Parameters.AddWithValue("$title", titles[i]);
            insert.ExecuteNonQuery();
        }
        stateTransaction.Commit();
        File.WriteAllText(OwnedPath(Path.Combine(DataRoot, "session_index.jsonl")),
            string.Concat(ids.Select((id, i) => JsonSerializer.Serialize(new { id, thread_name = titles[i], updated_at = "2026-01-01T00:00:00Z" }) + "\n")));
    }

    private string OwnedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演示路径越出临时目录。");
        return full;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(full), tempParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("演示临时目录身份不匹配，保留目录。");
        if (!Directory.Exists(full)) return;
        AssertNoLinks(full);
        Directory.Delete(full, recursive: true);
    }

    private static void AssertNoLinks(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("演示临时目录中出现链接，为避免误删已保留目录。");
        if ((attributes & FileAttributes.Directory) != 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) AssertNoLinks(entry);
    }

    private sealed record SchemaObject(string Name, string Type, string Sql);
}
