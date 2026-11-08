using System.Text.Json.Serialization;

namespace CodexCleaner.Engine;

public sealed record ProcessState(bool Running, IReadOnlyList<string> Processes);

public sealed record ChatInfo(string Id, string Title, DateTimeOffset? UpdatedAt, bool Archived, string? RelativePath, bool RolloutMissing, string? BlockedReason)
{
    public bool CanDelete => BlockedReason is null;
    // Display-only metadata. Never use this value as an I/O target.
    public string? OriginalPath { get; init; }
    public bool CanRemoveRecord { get; init; }
    // Codex holds this chat's writer lock, so it is loaded in a running Codex process.
    public bool InUse { get; init; }
    public long? Messages { get; init; }
    public long RolloutBytes { get; init; }
    public IReadOnlyList<string> RolloutFiles { get; init; } = [];
    // Forks whose history is read from this chat; deleting it alone would break them.
    public IReadOnlyList<string> ForkChildren { get; init; } = [];
}

public sealed record DatabaseStatus(bool Present, bool Supported, string? Reason = null);

public sealed record RecoveryIssue(string Directory, string Error, bool BlocksWrites);

public sealed record Capabilities(bool DeleteChats, bool RepairReferences, bool ClearData);

public sealed record Snapshot(
    IReadOnlyList<ChatInfo> Chats, IReadOnlyDictionary<string, DatabaseStatus> Databases,
    IReadOnlyList<string> Warnings, Capabilities Capabilities, IReadOnlyList<RecoveryIssue> RecoveryIssues)
{
    internal Dictionary<string, List<string>> AutomationTargets { get; init; } = [];
    internal RolloutIndex Rollouts { get; init; } = new();
}

public sealed record Selection(IReadOnlyList<string> ChatIds, bool ClearMemories = false, bool ClearSharedHistory = false)
{
    // The caller must explicitly include record-only cleanup in its reviewed scope.
    public bool AllowRecordOnly { get; init; }
}

public sealed record PlanTable(string Name, long Rows, bool ClearsReference);

public sealed record PlanDatabase(string Path, long RowCount, IReadOnlyList<PlanTable> Tables);

public sealed record PlanFile(string Path, bool Remove, string Purpose);

public sealed record Plan(
    string Id, bool Repair, DateTimeOffset ExpiresAt, Selection Selection, IReadOnlyList<ChatInfo> Chats,
    int ReferenceRepairs, IReadOnlyList<PlanFile> Files, IReadOnlyList<PlanDatabase> Databases, IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => Id.Length == 0;
    public long DatabaseRows => Databases.Sum(d => d.RowCount);
    // Built while Codex was running: executes under the chats' writer locks, one database at a time.
    public bool Live { get; init; }
}

public enum ChatRole { User, Assistant, Tool, System }

public sealed record ChatToolCall(string Id, string Name, string Summary, string Arguments, string? Result);

public sealed record ChatMessage(ChatRole Role, string Text, string? Thinking, IReadOnlyList<ChatToolCall> ToolCalls, DateTimeOffset CreatedAt);

public sealed record ExecuteResult(string? ResidualDirectory, string? ResidualError);

internal enum ActionKind { Delete, ClearReference, CatalogRevision }

internal sealed record SqlAction(string Table, string Where, object[] Args, ActionKind Kind = ActionKind.Delete, string? Column = null)
{
    public long Count { get; set; }
}

internal sealed record DatabaseWork(string Path, List<SqlAction> Actions);

internal sealed record FileWork(string Path, bool Remove, byte[]? Content = null, string? SourceHash = null, int References = 0);

internal sealed record Fingerprint(bool Exists, long SizeBytes = 0, string? Sha256 = null);

internal sealed record PlanState(
    Plan Public, List<FileWork> Files, List<DatabaseWork> Databases, Dictionary<string, Fingerprint> Fingerprints,
    List<string> AutomationFiles, List<string> MemoryPaths, DateTimeOffset Expires)
{
    // Live plans: threads.updated_at_ms (or updated_at) per chat at preview time.
    public Dictionary<string, long?> Updated { get; init; } = [];
}

internal sealed class Manifest
{
    public string Format { get; set; } = "codex-cleaner-v2";
    public string Id { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<ManifestItem> Files { get; set; } = [];
    public List<ManifestItem> Databases { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? RecoveryError { get; set; }
}

internal sealed class ManifestItem
{
    public string Path { get; set; } = "";
    public string? Action { get; set; }
    public string Backup { get; set; } = "";
    public Fingerprint? Original { get; set; }
    public Fingerprint? BackupFingerprint { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(LockInfo))]
internal sealed partial class ManifestJson : JsonSerializerContext;

internal sealed record LockInfo(int Pid, DateTimeOffset CreatedAt, string DataRoot);

// Seams for fault-injection tests; all null in production.
internal sealed class TestHooks
{
    public Action? DuringRead;
    public Action<string>? AfterCheckRollout;
    public Action? AfterJsonEdits;
    public Action<string, string>? AfterBackupCopy;
    public Action? AfterFingerprintCheck;
    public Action<string>? BeforeDelete;
    public Action<int>? BeforeCommit;
    public Action? BeforeReclaimOpen;
    public Action? BeforeRollback;
    public Action? AfterThreadLocks;
}
