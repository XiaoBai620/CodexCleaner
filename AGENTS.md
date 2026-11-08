# AGENTS

- 测试：`dotnet test tests/CodexCleaner.Tests -c Release`（合成数据，不碰真实库）
- 发版：改 csproj `<Version>` + `docs/CHANGELOG.md` 新增 `## x.y.z` → commit/push → `git tag vx.y.z` 并推送，CI 测试、编译并创建 Release（exe + SHA256SUMS）。`dist/` 为临时输出，用完即删
- 图标：`python scripts/make_icon.py` 重新生成 `src/CodexCleaner/app.ico`（不使用 OpenAI 标志）
- 真实数据：`%USERPROFILE%\.codex`（或 `CODEX_HOME`）只允许只读访问；写入验证在副本上做
- Codex 升级后结构指纹会变：同步更新 `Engine/Catalog.cs` 的 `Schemas` 和 `tests/CodexCleaner.Tests/fixture-schema.json`，并核查新表是否引用聊天 ID
- 运行中清理依赖上游 `codex-rs/thread-store/src/local/writer_lock.rs`（写入锁）、`rollout/src/maintenance.rs`（整理锁）、`rollout/src/rollout_reference_index.rs`（分叉引用）；上游改动时同步核对
- 查看对话的 `ChatView.cs` / `ChatLayout.cs` 移植自 DevinCleaner，修改时可对照同步
