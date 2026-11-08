using System.Text.Json;

namespace CodexCleaner.Engine;

public sealed partial class Cleaner
{
    // Tool output can reach megabytes per call; the viewer only needs a readable prefix.
    internal const int ToolResultLimit = 200_000;
    internal const int ToolArgumentsLimit = 200_000;

    // thread_items is Codex's own projection of the rollout into display items, in rollout order.
    public IReadOnlyList<ChatMessage> Conversation(string chatId) => WithRead(() =>
    {
        if (!Ids.Valid(chatId)) throw new CleanerException("聊天 ID 无效。");
        if (InspectDatabases()["thread_history_1.sqlite"] is not { Present: true, Supported: true })
            throw new CleanerException("对话历史数据库不存在或结构未验证，无法显示。");
        using var db = Db.Open(Data("thread_history_1.sqlite"), writable: false);
        var messages = new List<ChatMessage>();
        foreach (var r in Db.Rows(db, "SELECT item_type, item_json, created_at_ms FROM thread_items WHERE thread_id=$p0 ORDER BY rollout_ordinal", [chatId]))
        {
            var created = DateTimeOffset.FromUnixTimeMilliseconds(r[2] as long? ?? 0);
            using var doc = JsonDocument.Parse((string)r[1]!);
            var item = doc.RootElement;
            switch (r[0] as string)
            {
                case "userMessage":
                    messages.Add(new ChatMessage(ChatRole.User, string.Join("\n", Array(item, "content").Select(p => Str(p, "text")).Where(t => t.Length > 0)), null, [], created));
                    break;
                case "agentMessage":
                    messages.Add(new ChatMessage(ChatRole.Assistant, Str(item, "text"), null, [], created));
                    break;
                case "reasoning":
                    var summary = string.Join("\n", Array(item, "summary").Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()));
                    if (summary.Length > 0) messages.Add(new ChatMessage(ChatRole.Assistant, "", summary, [], created));
                    break;
                case "contextCompaction":
                    messages.Add(new ChatMessage(ChatRole.System, "上下文已压缩", null, [], created));
                    break;
                case "subAgentActivity":
                    break;
                case { } type:
                    var call = Tool(type, item);
                    // Consecutive tool items share one message so they render as adjacent rows.
                    if (messages.Count > 0 && messages[^1] is { Role: ChatRole.Assistant, Text: "", Thinking: null } last)
                        messages[^1] = last with { ToolCalls = [.. last.ToolCalls, call] };
                    else messages.Add(new ChatMessage(ChatRole.Assistant, "", null, [call], created));
                    break;
            }
        }
        return messages;
    });

    private static ChatToolCall Tool(string type, JsonElement item)
    {
        var id = Str(item, "id");
        ChatToolCall Call(string name, string summary, string arguments, string? result) =>
            new(id, name, OneLine(summary), Clip(arguments, ToolArgumentsLimit), result is null ? null : Clip(result, ToolResultLimit));
        switch (type)
        {
            case "commandExecution":
                var command = Str(item, "command");
                var exit = item.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? $"\n（退出码 {code.GetInt64()}）" : "";
                return Call("命令", command, command, Str(item, "aggregatedOutput") + exit);
            case "fileChange":
                var changes = Array(item, "changes").ToList();
                var paths = string.Join('\n', changes.Select(c => Str(c, "path")));
                return Call("文件修改", string.Join("、", changes.Select(c => Path.GetFileName(Str(c, "path")))), paths,
                    string.Join("\n\n", changes.Select(c => $"■ {Str(c, "path")}\n{Str(c, "diff")}")));
            case "mcpToolCall":
                return Call($"{Str(item, "server")}.{Str(item, "tool")}", "", Raw(item, "arguments"), Raw(item, "result"));
            case "webSearch":
                return Call("网页搜索", Str(item, "query"), Raw(item, "action"), null);
            case "imageView":
                return Call("查看图片", Str(item, "path"), Str(item, "path"), null);
            case "collabAgentToolCall":
                return Call("子任务", Str(item, "tool"), item.GetRawText(), null);
            default:
                return Call(type, "", item.GetRawText(), null);
        }
    }

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not JsonValueKind.Null ? v.GetRawText() : "";

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n…（超过 {limit:N0} 字符的部分未显示）";
}
