using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexCleaner.Core;

public sealed partial class Cleaner
{
    private static List<SqlAction> SqlActions(string rel, IReadOnlyList<string> ids, Selection selection)
    {
        var actions = new List<SqlAction>();
        object[] args = [.. ids];
        var marks = Db.In(0, ids.Count);
        void Add(string table, string column = "thread_id") { if (ids.Count > 0) actions.Add(new SqlAction(table, $"{Db.Quote(column)} IN ({marks})", args)); }
        void All(string table) => actions.Add(new SqlAction(table, "1=1", []));
        object[] twice = [.. ids, .. ids];
        switch (rel)
        {
            case "state_5.sqlite":
                if (ids.Count > 0)
                    actions.Add(new SqlAction("threads", $"attached_thread_parent_id IN ({marks}) AND id NOT IN ({Db.In(ids.Count, ids.Count)})", twice, ActionKind.ClearReference, "attached_thread_parent_id"));
                Add("thread_attachments"); Add("thread_dynamic_tools"); Add("thread_read_receipts"); Add("guardian_review_feedback");
                if (ids.Count > 0) actions.Add(new SqlAction("thread_spawn_edges", $"parent_thread_id IN ({marks}) OR child_thread_id IN ({Db.In(ids.Count, ids.Count)})", twice));
                Add("threads", "id");
                break;
            case "thread_history_1.sqlite":
                Add("thread_items"); Add("thread_turns"); Add("thread_realtime_items"); Add("thread_history_projection_state");
                break;
            case "memories_1.sqlite":
                // consolidation_progress holds only a singleton count, no remembered text; the runtime's
                // recreation behavior is undocumented, so it is preserved.
                if (selection.ClearMemories) { All("stage1_outputs"); All("jobs"); } else Add("stage1_outputs");
                break;
            case "logs_2.sqlite":
                if (selection.ClearSharedHistory) All("logs"); else Add("logs");
                break;
            case "goals_1.sqlite":
                Add("thread_goal_continuation_deferrals"); Add("thread_goals");
                break;
            case "queue_1.sqlite":
                Add("queued_items"); Add("queued_thread_revisions");
                break;
            case "sqlite/codex-dev.db":
                Add("automation_runs"); Add("inbox_items");
                if (ids.Count > 0)
                    foreach (var table in new[] { "live_visualization_suggestions", "local_thread_catalog", "local_thread_catalog_scan_entries", "thread_timeline_ledger" })
                        actions.Add(new SqlAction(table, $"thread_id IN ({marks}) AND host_id = $p{ids.Count}", [.. ids, "local"]));
                break;
        }
        return actions;
    }

    private (Dictionary<string, Fingerprint> Fingerprints, List<string> AutomationFiles) PlanBaseline()
    {
        var automationFiles = AutomationFiles();
        var tracked = Catalog.GlobalFiles.Concat([Catalog.History, Catalog.SessionIndex]).Concat(automationFiles)
            .Concat(Catalog.Schemas.Keys.SelectMany(rel => Catalog.SqliteSuffixes.Select(s => rel + s)));
        return (Fingerprints(tracked), automationFiles);
    }

    private List<FileWork> JsonEdits(IReadOnlyList<string> ids, Selection selection)
    {
        var edits = new List<FileWork>();
        var selected = ids.ToHashSet();
        var desktop = new DesktopState(DataRoot);
        foreach (var rel in Catalog.GlobalFiles)
        {
            if (Stat(rel) is not { } entry) continue;
            if (entry is not FileInfo) throw new CleanerException($"无法安全解析桌面状态：{rel}");
            var (root, bytes) = DesktopState.Read(entry.FullName, rel);
            var (changed, references) = desktop.Clean(root, selected, selection.ClearSharedHistory);
            if (changed)
                edits.Add(new FileWork(rel, false, DesktopState.Serialize(root), DesktopState.Hash(bytes), references));
        }
        if (ids.Count > 0)
        {
            foreach (var (rel, key) in new[] { (Catalog.SessionIndex, "id"), (Catalog.History, "session_id") })
            {
                if (Stat(rel) is not { } entry) continue;
                if (entry is not FileInfo file || file.Length > Catalog.MaxJsonBytes) throw new CleanerException($"索引太大或类型未知：{rel}");
                var bytes = File.ReadAllBytes(file.FullName);
                var text = Encoding.UTF8.GetString(bytes);
                var kept = new StringBuilder();
                var changed = false;
                foreach (var line in SplitKeepingNewlines(text))
                {
                    if (line.Trim().Length > 0)
                    {
                        JsonNode? value;
                        try { value = JsonNode.Parse(line.TrimStart('\uFEFF')); }
                        catch (JsonException) { throw new CleanerException($"索引包含无法解析的 JSON 行：{rel}"); }
                        if (Json.Str(Json.Get(value, key)) is { } id && selected.Contains(id)) { changed = true; continue; }
                    }
                    kept.Append(line);
                }
                if (changed) edits.Add(new FileWork(rel, false, Encoding.UTF8.GetBytes(kept.ToString()), DesktopState.Hash(bytes)));
            }
        }
        Hooks.AfterJsonEdits?.Invoke();
        return edits;
    }

    private static IEnumerable<string> SplitKeepingNewlines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { yield return text[start..(i + 1)]; start = i + 1; }
        if (start < text.Length) yield return text[start..];
    }

    private void CheckRollout(string rel, string id)
    {
        var buffer = new byte[1024 * 1024];
        int read;
        using (var stream = File.Open(Data(rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var end = text.IndexOf('\n');
        if (end < 0 && read == buffer.Length) throw new CleanerException("会话元数据行太长，无法验证。");
        JsonNode? first;
        try { first = JsonNode.Parse((end < 0 ? text : text[..end]).TrimStart('\uFEFF').Trim()); }
        catch (JsonException) { first = null; }
        if (Json.Str(Json.Get(first, "type")) != "session_meta" || Json.Str(Json.Get(Json.Get(first, "payload"), "id")) != id)
            throw new CleanerException($"会话文件 ID 与聊天索引不一致：{rel}");
        Hooks.AfterCheckRollout?.Invoke(rel);
    }

    private void WalkMemory(string rel, List<string> result)
    {
        var entry = Stat(rel);
        switch (entry)
        {
            case null: return;
            case FileInfo:
                if (!IndependentFile(entry)) throw new CleanerException($"记忆目录中有硬链接，禁止清理：{rel}");
                result.Add(rel);
                return;
            case DirectoryInfo directory:
                foreach (var name in directory.EnumerateFileSystemInfos().Select(e => e.Name).Order(StringComparer.Ordinal)) WalkMemory($"{rel}/{name}", result);
                return;
        }
    }

    private List<string> MemoryFiles()
    {
        var result = new List<string>();
        foreach (var dir in Catalog.MemoryDirs) WalkMemory(dir, result);
        return result.Order(StringComparer.Ordinal).ToList();
    }

    public Plan PlanCleanup(Selection selection) => WithRead(() => BuildPlan(selection, repair: false));

    public Plan PlanRepair() => WithRead(() =>
    {
        var empty = new Plan("", true, default, new Selection([]), [], 0, [], [], []);
        var baseline = PlanBaseline();
        var snapshot = TakeSnapshot();
        if (!snapshot.Capabilities.RepairReferences) throw new CleanerException("聊天数据库检查未通过，暂不能判断哪些本机引用已经失效。");
        var live = snapshot.Chats.Select(c => c.Id).ToHashSet();
        var candidates = new HashSet<string>();
        var desktop = new DesktopState(DataRoot);
        foreach (var rel in Catalog.GlobalFiles)
            if (Stat(rel) is FileInfo file) candidates.UnionWith(desktop.LocalIds(DesktopState.Read(file.FullName, rel).Root));
        // These stores describe local Codex histories. Do not infer local identity from UUID
        // syntax or from unscoped desktop settings shared with cloud chats.
        foreach (var (rel, sql) in new[]
        {
            ("state_5.sqlite", "SELECT thread_id FROM thread_read_receipts UNION SELECT attached_thread_parent_id FROM threads WHERE attached_thread_parent_id IS NOT NULL"),
            ("thread_history_1.sqlite", "SELECT DISTINCT thread_id FROM thread_history_projection_state"),
            ("memories_1.sqlite", "SELECT DISTINCT thread_id FROM stage1_outputs"),
            ("goals_1.sqlite", "SELECT DISTINCT thread_id FROM thread_goals"),
            ("queue_1.sqlite", "SELECT DISTINCT thread_id FROM queued_thread_revisions"),
            ("sqlite/codex-dev.db", "SELECT thread_id FROM local_thread_catalog WHERE host_id = 'local' AND source_kind NOT IN ('chatgpt','cloud')"),
        })
        {
            if (!snapshot.Databases[rel].Present) continue;
            using var db = Db.Open(Data(rel), writable: false);
            foreach (var row in Db.Rows(db, sql)) if (row[0] is string id && Ids.Valid(id)) candidates.Add(id);
        }
        var ids = candidates.Where(id => !live.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) { AssertFingerprints(baseline.Fingerprints); return empty; }
        return BuildPlan(new Selection(ids), repair: true, snapshot, baseline);
    });

    private Plan BuildPlan(Selection request, bool repair, Snapshot? initialSnapshot = null, (Dictionary<string, Fingerprint> Fingerprints, List<string> AutomationFiles)? initialBaseline = null)
    {
        if (request.ChatIds.Any(id => !Ids.Valid(id))) throw new CleanerException("请选择有效的聊天 ID。");
        var ids = request.ChatIds.Distinct().ToList();
        if (ids.Count > (repair ? 10000 : 500)) throw new CleanerException("选择的条目过多，请分批清理。");
        var selection = request with { ChatIds = ids };
        if (ids.Count == 0 && !selection.ClearMemories && !selection.ClearSharedHistory) throw new CleanerException("没有选择要清理的内容。");
        // Take the baseline before reading paths and row counts. Otherwise a write
        // during preview construction can silently become the accepted baseline.
        var baseline = initialBaseline ?? PlanBaseline();
        var snapshot = initialSnapshot ?? TakeSnapshot();
        foreach (var (rel, info) in snapshot.Databases)
            if (info is { Present: true, Supported: false }) throw new CleanerException($"无法安全清理：{rel} 的结构未验证。");
        if (repair)
            foreach (var id in ids)
                if (snapshot.AutomationTargets.TryGetValue(id, out var labels)) throw new CleanerException($"失效聊天 {id} 仍被自动化 {string.Join('、', labels)} 引用，请先在 Codex 中处理该任务。");
        var selected = repair ? [] : ids.Select(id =>
        {
            var chat = snapshot.Chats.FirstOrDefault(c => c.Id == id) ?? throw new CleanerException("选中的聊天已不存在，请刷新。");
            return chat.CanDelete ? chat : throw new CleanerException(chat.Reason);
        }).ToList();
        var rolloutPaths = selected.Select(c => c.RelativePath).OfType<string>().ToList();
        var rolloutFingerprints = Fingerprints(rolloutPaths);
        var files = new Dictionary<string, FileWork>();
        foreach (var chat in selected)
            if (chat.RelativePath is { } rel && Stat(rel) is not null) { CheckRollout(rel, chat.Id); files[rel] = new FileWork(rel, true); }
        var memoryPaths = selection.ClearMemories ? MemoryFiles() : [];
        foreach (var rel in memoryPaths) files[rel] = new FileWork(rel, true);
        foreach (var edit in JsonEdits(ids, selection)) files[edit.Path] = edit;
        var databases = new List<DatabaseWork>();
        foreach (var (rel, info) in snapshot.Databases)
        {
            if (!info.Present) continue;
            var actions = SqlActions(rel, ids, selection).Where(a => !(repair && rel == "state_5.sqlite" && a is { Table: "threads", Kind: ActionKind.Delete })).ToList();
            if (actions.Count == 0) continue;
            using var db = Db.Open(Data(rel), writable: false);
            foreach (var action in actions) action.Count = Db.Count(db, action.Table, action.Where, action.Args);
            if (rel == "sqlite/codex-dev.db" && (repair || actions.Any(a => a is { Table: "local_thread_catalog", Count: > 0 })))
                actions.Add(new SqlAction("local_thread_catalog_metadata", "id=1", [], ActionKind.CatalogRevision) { Count = 1 });
            // Queue delete triggers can recreate revision rows; keep zero-count actions.
            if (actions.Any(a => a.Count > 0)) databases.Add(new DatabaseWork(rel, actions));
        }
        var automationFiles = AutomationFiles();
        if (!automationFiles.SequenceEqual(baseline.AutomationFiles)) throw new CleanerException("自动化文件列表在生成预览时改变，请重新预览。");
        // Even preserved DBs can gain new messages or automations between preview and execution.
        var tracked = files.Keys.Concat(rolloutPaths).Concat(Catalog.GlobalFiles).Concat([Catalog.History, Catalog.SessionIndex]).Concat(automationFiles)
            .Concat(Catalog.Schemas.Keys.SelectMany(rel => Catalog.SqliteSuffixes.Select(s => rel + s)));
        var fingerprints = Fingerprints(tracked);
        foreach (var (rel, before) in baseline.Fingerprints.Concat(rolloutFingerprints))
            if (fingerprints[rel] != before) throw new CleanerException($"数据在生成预览时发生变化，请重新预览：{rel}");
        foreach (var file in files.Values)
            if (file.SourceHash is not null && file.SourceHash != fingerprints[file.Path].Sha256) throw new CleanerException($"数据在生成预览时发生变化，请重新预览：{file.Path}");
        var warnings = repair
            ? new List<string> { "仅清除已不在本机聊天索引中的明确本机引用，保留现有聊天、项目和远端状态。" }
            : new List<string>
            {
                "删除一条聊天不会删除其派生聊天里已复制的上下文，也不会删除项目、工作树、导出、语音文件或未识别日志。",
                "单条记忆删除不能移除已汇总进其他记忆的内容；需要时勾选“全部本地记忆”。设置和记忆开关不变，日后使用可能再次产生记忆。",
                "这是本机逻辑删除，不保证从 SSD、系统还原点或外部备份中不可恢复。",
            };
        if (!repair && !selection.ClearSharedHistory) warnings.Add("共享输入历史和无法归属聊天的数据库日志仍保留；可勾选共享历史清理。");
        var planId = Guid.NewGuid().ToString();
        var publicPlan = new Plan(planId, repair, DateTimeOffset.Now + Catalog.PlanLifetime, selection, selected,
            files.Values.Sum(f => f.References),
            files.Values.Select(f => new PlanFile(f.Path, f.Remove, Catalog.Purpose(f.Path))).ToList(),
            databases.Select(d => new PlanDatabase(d.Path, d.Actions.Sum(a => a.Count), d.Actions.Select(a => new PlanTable(a.Table, a.Count, a.Kind == ActionKind.ClearReference)).ToList())).ToList(),
            warnings);
        lock (gate)
        {
            foreach (var key in plans.Where(p => p.Value.Expires < DateTimeOffset.Now).Select(p => p.Key).ToList()) plans.Remove(key);
            plans[planId] = new PlanState(publicPlan, [.. files.Values], databases, fingerprints, automationFiles, memoryPaths, publicPlan.ExpiresAt);
        }
        return publicPlan;
    }
}
