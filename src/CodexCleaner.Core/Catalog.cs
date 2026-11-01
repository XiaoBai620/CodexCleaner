namespace CodexCleaner.Core;

// Storage layout observed on 2026-10-31. These are not a stable Codex API; any schema change disables writes.
internal static class Catalog
{
    public static readonly IReadOnlyDictionary<string, string> Schemas = new Dictionary<string, string>
    {
        ["state_5.sqlite"] = "8c829eff66f38fbaa1edc0a89e2c148f755569dab3ae6bd3bd572e8cf636dc8c",
        ["thread_history_1.sqlite"] = "96ed60adb80f45d47f2735ef546fb6880d87552b1c85df5432b41e3884e49610",
        ["memories_1.sqlite"] = "da722ff7607ab390384b30c26de1e45bf0a6a991ceaf3a9ff24bb06046668f55",
        ["logs_2.sqlite"] = "0ced295540ebe7cdee7f1be50bc9989a2db63b8b33e9588c7c1f2918eb06cced",
        ["goals_1.sqlite"] = "94f1ccfb1a050966dee766b9c0a037040882ac7eb2b0b38571163792d1f4bdc9",
        ["queue_1.sqlite"] = "93dbfc824a719acb2a513bc4de1932f7ab3ee979bb197f5638e3fe5b61fd5553",
        ["sqlite/codex-dev.db"] = "55f16b86b9e32e53b6c5dac23df73002781a3de8294c8aa840bb3424e8dc808e",
    };

    public static readonly string[] MemoryDirs = ["memories", "memories_v2", "memories_extensions"];
    public static readonly string[] GlobalFiles = [".codex-global-state.json", ".codex-global-state.json.bak"];
    public static readonly string[] SqliteSuffixes = ["", "-wal", "-journal"];
    public const string SessionIndex = "session_index.jsonl";
    public const string History = "history.jsonl";
    public const long MaxJsonBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);

    public static readonly string[] DesktopMapKeys =
    [
        "thread-project-assignments", "thread-writable-roots", "thread-projectless-output-directories", "thread-workspace-root-hints",
        "queued-follow-ups", "thread-descriptions-v1", "thread-permission-settings-by-id", "heartbeat-thread-permissions-by-id",
        "thread-workspace-draft-tabs-v1", "composer-prompt-drafts-v1", "composer-prompt-drafts-v2", "prompt-history",
    ];
    public static readonly string[] DesktopListKeys = ["projectless-thread-ids", "pinned-thread-ids", "archived-thread-ids"];
    public static readonly string[] DesktopKeyPrefixes =
        ["thread-reference-capability:", "thread-browser-tabs-v1:", "thread-tab-routes-v1:", "thread-client-id-v1:local%3A", "codex-writing-block-deleted-thread-v1:"];

    // Describes the only kinds of files a plan can touch.
    public static string Purpose(string rel) =>
        rel == SessionIndex ? "会话索引；按聊天 ID 删除对应行"
        : rel == History ? "命令行输入历史；删除带该会话 ID 的行"
        : GlobalFiles.Contains(rel) ? "桌面界面状态；只改识别出的聊天字段"
        : MemoryDirs.Any(d => rel.StartsWith(d + "/", StringComparison.Ordinal)) ? "本地记忆"
        : "原始会话记录";
}
