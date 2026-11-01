using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexCleaner.Core;

public sealed partial class Cleaner
{
    private readonly Func<ProcessState> processCheck;
    private readonly Dictionary<string, PlanState> plans = [];
    private readonly Lock gate = new();
    private bool busy;
    private int readers;
    internal TestHooks Hooks { get; } = new();

    public string DataRoot { get; }
    public string TransactionRoot { get; }

    public Cleaner(string dataRoot, string transactionRoot, Func<ProcessState>? processCheck = null)
    {
        DataRoot = SafePaths.Normal(dataRoot);
        TransactionRoot = SafePaths.Normal(transactionRoot);
        if (SafePaths.Inside(DataRoot, TransactionRoot) || SafePaths.Inside(TransactionRoot, DataRoot)) throw new CleanerException("临时事务目录和数据目录不能互相包含。");
        this.processCheck = processCheck ?? (() => CodexProcesses.Detect(DataRoot));
    }

    public ProcessState CheckProcesses() => processCheck();

    public static string DefaultDataRoot() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string DefaultTransactionRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexCleaner", "Transactions");

    // Reject links/junctions at every existing path component, including root ancestors.
    internal string Safe(string root, string relative = "", bool missing = false)
    {
        var target = relative.Length > 0 ? Path.GetFullPath(Path.Combine(root, SafePaths.ValidRelative(relative))) : root;
        if (!SafePaths.Inside(root, target)) throw new CleanerException("路径越出受控目录。");
        var current = Path.GetPathRoot(target)!;
        foreach (var part in target[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception e) when (missing && e is FileNotFoundException or DirectoryNotFoundException) { break; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) && (attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(current) : (FileSystemInfo)new FileInfo(current)).LinkTarget is not null)
                throw new CleanerException($"拒绝符号链接或目录联接：{current}");
        }
        return target;
    }

    internal string Data(string rel, bool missing = false) => Safe(DataRoot, rel, missing);
    internal string Trash(string rel = "", bool missing = false) => Safe(TransactionRoot, rel, missing);

    internal FileSystemInfo? Stat(string rel)
    {
        var full = Data(rel, missing: true);
        if (File.Exists(full)) return new FileInfo(full);
        return Directory.Exists(full) ? new DirectoryInfo(full) : null;
    }

    internal static bool IndependentFile(FileSystemInfo? entry) => entry is FileInfo file && FileLinks.Count(file.FullName) == 1;

    internal Fingerprint FingerprintOf(string root, string rel)
    {
        var file = Safe(root, rel, missing: true);
        if (Directory.Exists(file) || (File.Exists(file) && FileLinks.Count(file) > 1)) throw new CleanerException($"拒绝特殊文件或硬链接：{rel}");
        if (!File.Exists(file)) return new Fingerprint(false);
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // An empty WAL is equivalent to no WAL; SQLite connections may create it.
        if (rel.EndsWith("-wal", StringComparison.Ordinal) && stream.Length == 0) return new Fingerprint(false);
        return new Fingerprint(true, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    // SHM is a coordination index and can change during an innocent read. WAL and the
    // main DB carry persistent content and are the stale-preview guard.
    internal Dictionary<string, Fingerprint> Fingerprints(IEnumerable<string> rels) =>
        rels.Distinct().Where(r => !r.EndsWith("-shm", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToDictionary(r => r, r => FingerprintOf(DataRoot, r));

    internal void AssertFingerprints(Dictionary<string, Fingerprint> expected)
    {
        foreach (var (rel, old) in expected)
            if (FingerprintOf(DataRoot, rel) != old) throw new CleanerException($"数据在预览后已改变，请刷新后重新选择：{rel}");
        Hooks.AfterFingerprintCheck?.Invoke();
    }

    private T WithRead<T>(Func<T> operation)
    {
        lock (gate)
        {
            if (busy) throw new CleanerException("清理正在进行，请完成后再刷新或预览。");
            readers++;
        }
        try { return operation(); }
        finally { lock (gate) readers--; }
    }

    private void ProcessGuard()
    {
        var state = processCheck() ?? throw new CleanerException("无法可靠确认 Codex 是否退出；禁止写入。");
        if (state.Running || state.Processes.Count > 0) throw new CleanerException("请先完全退出 Codex 桌面程序、命令行与相关后台进程，再重试。");
    }

    private T Exclusive<T>(Func<T> operation)
    {
        lock (gate)
        {
            if (busy) throw new CleanerException("另一项清理或修复正在进行。");
            if (readers > 0) throw new CleanerException("读取尚未完成，请稍后重试。");
            busy = true;
        }
        FileStream? lockHandle = null;
        string? lockFile = null;
        try
        {
            ProcessGuard();
            Directory.CreateDirectory(Trash(missing: true));
            lockFile = Trash(".mutation.lock", true);
            lockHandle = TryCreate(lockFile) ?? ReclaimStaleLock(lockFile);
            JsonSerializer.Serialize(lockHandle, new LockInfo(Environment.ProcessId, DateTimeOffset.UtcNow, DataRoot), ManifestJson.Default.LockInfo);
            lockHandle.Flush();
            ProcessGuard();
            return operation();
        }
        finally
        {
            if (lockHandle is not null) { lockHandle.Dispose(); File.Delete(lockFile!); }
            lock (gate) busy = false;
        }
    }

    private const int FileExists = unchecked((int)0x80070050);

    private static FileStream? TryCreate(string file)
    {
        try { return new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException e) when (e.HResult == FileExists) { return null; }
    }

    private FileStream ReclaimStaleLock(string lockFile)
    {
        var reclaimFile = Trash(".mutation-reclaim.lock", true);
        Hooks.BeforeReclaimOpen?.Invoke();
        using var reclaim = TryCreate(reclaimFile)
            ?? throw new CleanerException($"存在未完成的写入锁检查，无法确认状态。请关闭其他清理窗口并检查故障文件：{reclaimFile}；本次未执行。");
        try
        {
            bool stale;
            try
            {
                var info = JsonSerializer.Deserialize(File.ReadAllText(Trash(".mutation.lock")), ManifestJson.Default.LockInfo);
                if (info is null || info.Pid <= 0 || !SafePaths.Same(SafePaths.Normal(info.DataRoot), DataRoot)) throw new CleanerException("锁文件信息无效。");
                stale = !ProcessAlive(info.Pid);
            }
            catch (Exception e) { throw new CleanerException($"无法验证清理工具的写入锁，请关闭其他清理窗口后重试：{e.Message}", e); }
            if (!stale) throw new CleanerException("另一个清理工具正在写入，请等待其完成。");
            File.Delete(Trash(".mutation.lock"));
            return TryCreate(lockFile) ?? throw new CleanerException("另一个清理工具正在写入，请等待其完成。");
        }
        finally
        {
            reclaim.Dispose();
            File.Delete(reclaimFile);
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [GeneratedRegex(@"^(state|thread_history|memories|logs|goals|queue)_\d+\.sqlite\z")]
    private static partial Regex VersionedDatabase();

    private Dictionary<string, DatabaseStatus> InspectDatabases()
    {
        var databases = new Dictionary<string, DatabaseStatus>();
        foreach (var name in Directory.EnumerateFiles(Safe(DataRoot)).Select(Path.GetFileName))
            if (VersionedDatabase().IsMatch(name!) && !Catalog.Schemas.ContainsKey(name!))
                databases[name!] = new DatabaseStatus(true, false, "发现其他版本的聊天数据库，本工具尚未验证。");
        foreach (var (rel, expected) in Catalog.Schemas)
        {
            try
            {
                var entry = Stat(rel);
                if (entry is null) { databases[rel] = new DatabaseStatus(false, true); continue; }
                if (!IndependentFile(entry)) throw new CleanerException("数据库不是独立普通文件。");
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (Stat(rel + suffix) is { } sidecar && !IndependentFile(sidecar)) throw new CleanerException("数据库附属文件不是独立普通文件。");
                using var db = Db.Open(entry.FullName, writable: false);
                databases[rel] = Db.SchemaHash(db) == expected ? new DatabaseStatus(true, true) : new DatabaseStatus(true, false, "数据库结构与已验证版本不同；只允许浏览。");
            }
            catch (Exception e) when (e is CleanerException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                databases[rel] = new DatabaseStatus(true, false, e.Message);
            }
        }
        return databases;
    }

    internal List<string> AutomationFiles()
    {
        if (Stat("automations") is not DirectoryInfo directory) return [];
        return directory.EnumerateDirectories()
            .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Select(d => $"automations/{d.Name}/automation.toml")
            .Where(rel => Stat(rel) is FileInfo)
            .Order(StringComparer.Ordinal).ToList();
    }

    // An exact quoted ID match blocks cleanup regardless of ACTIVE/PAUSED so resumed jobs remain valid.
    private Dictionary<string, List<string>> AutomationTargets(Dictionary<string, DatabaseStatus> databases, IEnumerable<string> files)
    {
        var targets = new Dictionary<string, List<string>>();
        void Add(string id, string label) { if (!targets.TryGetValue(id, out var list)) targets[id] = list = []; list.Add(label); }
        if (databases["sqlite/codex-dev.db"] is { Present: true, Supported: true })
        {
            using var db = Db.Open(Data("sqlite/codex-dev.db"), writable: false);
            foreach (var row in Db.Rows(db, "SELECT id,name,target_thread_id FROM automations WHERE target_thread_id IS NOT NULL"))
                Add((string)row[2]!, $"{(row[1] as string is { Length: > 0 } name ? name : row[0])}（{row[0]}）");
        }
        foreach (var rel in files)
        {
            if (((FileInfo)Stat(rel)!).Length > Catalog.MaxJsonBytes) throw new CleanerException("自动化定义太大，不能可靠验证关联。");
            foreach (var id in AutomationToml.Targets(File.ReadAllText(Data(rel)))) Add(id, rel);
        }
        return targets;
    }

    public Snapshot Snapshot() => WithRead(TakeSnapshot);

    private Snapshot TakeSnapshot()
    {
        Hooks.DuringRead?.Invoke();
        Safe(DataRoot);
        var warnings = new List<string>();
        var databases = InspectDatabases();
        var unsupported = databases.Where(d => d.Value is { Present: true, Supported: false }).ToList();
        warnings.AddRange(unsupported.Select(d => $"{d.Key}：{d.Value.Reason}"));
        Dictionary<string, List<string>> targets = [];
        string? automationError = null;
        try { targets = AutomationTargets(databases, AutomationFiles()); }
        catch (Exception e) when (e is CleanerException or IOException or Microsoft.Data.Sqlite.SqliteException) { automationError = e.Message; warnings.Add($"自动化关联检查失败：{e.Message}"); }
        var chats = new List<ChatInfo>();
        var statePresent = databases["state_5.sqlite"].Present;
        if (databases["state_5.sqlite"].Supported && statePresent)
        {
            using var db = Db.Open(Data("state_5.sqlite"), writable: false);
            foreach (var r in Db.Rows(db, "SELECT id,title,rollout_path,updated_at,updated_at_ms,archived FROM threads ORDER BY updated_at DESC"))
            {
                var id = (string)r[0]!;
                var (block, reason) = unsupported.Count > 0 ? (BlockReason.Database, "数据库检查未通过，请查看上方具体原因。")
                    : automationError is not null ? (BlockReason.AutomationInspection, "自动化定义无法解析，无法安全维护其目标。")
                    : (BlockReason.None, "");
                if (targets.TryGetValue(id, out var labels)) (block, reason) = (BlockReason.Automation, $"此聊天是自动化 {string.Join('、', labels)} 的目标，请先在 Codex 中处理该任务。");
                string? relativePath = null;
                var missing = false;
                if (r[2] is string { Length: > 0 } rollout)
                {
                    try
                    {
                        relativePath = RolloutRelative(rollout);
                        var entry = Stat(relativePath);
                        if (entry is not null && !IndependentFile(entry)) throw new CleanerException("会话路径不是独立普通文件。");
                        missing = entry is null;
                    }
                    catch (CleanerException e) { (block, reason) = (BlockReason.UnsafePath, e.Message); }
                }
                var updated = r[4] is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : r[3] is long s ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;
                chats.Add(new ChatInfo(id, r[1] as string is { Length: > 0 } title ? title : "未命名聊天", updated, r[5] is long archived && archived != 0, relativePath, missing, reason, block));
            }
        }
        if (!statePresent) warnings.Add("未发现受支持的聊天索引 state_5.sqlite；不会猜测或删除孤立会话文件。");
        var safe = unsupported.Count == 0;
        return new Snapshot(chats, databases, warnings,
            new Capabilities(statePresent && safe && automationError is null, statePresent && safe, safe), RecoveryIssues())
        { AutomationTargets = targets };
    }

    [GeneratedRegex(@"^(sessions|archived_sessions)/.+\.jsonl\z")]
    private static partial Regex RolloutPath();

    private string RolloutRelative(string value)
    {
        var full = SafePaths.Normal(Path.IsPathRooted(value) ? value : Path.Combine(DataRoot, value));
        if (!SafePaths.Inside(DataRoot, full)) throw new CleanerException("聊天会话路径在数据目录外，禁止清理。");
        var rel = SafePaths.ValidRelative(Path.GetRelativePath(DataRoot, full).Replace('\\', '/'));
        if (!RolloutPath().IsMatch(rel)) throw new CleanerException("会话文件不在已验证的 sessions / archived_sessions 目录。");
        return rel;
    }

    public IReadOnlyList<RecoveryIssue> RecoveryIssues()
    {
        string directory;
        try { directory = Trash(); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        if (!Directory.Exists(directory)) return [];
        var issues = new List<RecoveryIssue>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            var (error, blocks) = ("", true);
            try
            {
                var file = Trash($"{entry.Name}/manifest.json");
                if (!File.Exists(file) || FileLinks.Count(file) > 1 || new FileInfo(file).Length > Catalog.MaxJsonBytes) throw new CleanerException("事务清单无法安全读取。");
                var manifest = JsonSerializer.Deserialize(File.ReadAllText(file), ManifestJson.Default.Manifest);
                if (manifest is null || manifest.Format != "codex-cleaner-v2" || manifest.Id != entry.Name || !SafePaths.Same(SafePaths.Normal(manifest.DataRoot), DataRoot))
                    throw new CleanerException("事务清单与当前数据目录不匹配。");
                (error, blocks) = (manifest.Error ?? manifest.RecoveryError ?? "", manifest.Status is not ("complete" or "cleanup-required"));
            }
            catch (Exception e) when (e is CleanerException or IOException or JsonException or UnauthorizedAccessException) { error = e.Message; }
            issues.Add(new RecoveryIssue(entry.FullName, error, blocks));
        }
        return issues;
    }
}
