# AGENTS

- 测试：`dotnet test tests/CodexCleaner.Tests -c Release`（合成数据，不碰真实库）
- 发版：改 csproj `<Version>` + `docs/CHANGELOG.md` 新增 `## x.y.z` → commit/push → `git tag vx.y.z` 并推送，CI 测试、编译并创建 Release（exe + SHA256SUMS）。`dist/` 为临时输出，用完即删
- 图标：`python scripts/make_icon.py`（需要 Pillow）重新生成 `src/CodexCleaner/app.ico`，图形为 ChatGPT 标志，也作为嵌入资源供窗口左上角显示
- 配色集中在 `DesktopTheme.cs`（ChatGPT 风格），界面代码不要直接写 `Color.FromArgb`
- 界面截图：设置 `CODEXCLEANER_TEST_SCREENSHOTS=<目录>` 后运行 `UpdateUiTests`；`DrawToBitmap` 画不出无边框的搜索框，属正常现象
- 真实数据：`%USERPROFILE%\.codex`（或 `CODEX_HOME`）只允许只读访问。删除、恢复与故障注入测试只用合成数据；性能和界面测量先复制到 `%TEMP%`（SQLite 用只读源连接和 backup API，其余普通文件直接复制，不跟随链接），将 `CODEX_HOME` 与事务/备份目录指向隔离位置，测后删掉副本。报告只留统计和耗时，不留聊天内容、凭证或真实路径列表
- Codex 升级后结构指纹会变：同步更新 `Engine/Catalog.cs` 的 `Schemas` 和 `tests/CodexCleaner.Tests/fixture-schema.json`，并核查新表是否引用聊天 ID
- 运行中清理依赖上游 `codex-rs/thread-store/src/local/writer_lock.rs`（写入锁）、`rollout/src/maintenance.rs`（整理锁）、`rollout/src/rollout_reference_index.rs`（分叉引用）；上游改动时同步核对
- 查看对话的 `ChatView.cs` / `ChatLayout.cs` 移植自 DevinCleaner，修改时可对照同步
- 持久备份：`%LOCALAPPDATA%\CodexCleaner\Backups` 与临时 `Transactions` 分开；离线/运行中删除都先备份校验，失效引用修复只使用事务回滚。备份目录、数据目录和事务目录互不包含，所有访问继续经过路径及独立文件检查
- 恢复：Codex 必须退出，同时占用整理锁和聊天写入锁；核对备份格式、原数据根目录、全部数据库存在状态/结构指纹/迁移记录、内容摘要和依赖，跳过已有聊天；禁止 `INSERT OR REPLACE`、忽略冲突或猜测结构迁移
- 恢复顺序与触发器：先 `threads` / `thread_goals` 后其依赖行；保留聊天时间派生列原值；队列恢复产生新的单调版本，不回退全局序号；目录版本递增。普通恢复不回写置顶/草稿/窗口状态，附加记忆与共享历史按组独立恢复
- 故障恢复：未完成备份不能普通恢复或删除；比对操作前后状态后才能补回缺失数据。恢复回滚必须处理原先不存在的新文件，回滚未验证时保留材料并阻止后续写入；不能把多库和文件操作宣称为断电原子提交
- 在线索引：同一索引写入锁内先持久保存完整前后内容及摘要，再改写；可捕获的半写异常在该锁内还原并校验。故障恢复仅接受完整前态/后态或其后追加的无关有效记录，无法判定的变更拒绝自动覆盖
- 启动故障检查：`BackupRecoveryIssues` 只读取有大小限制的轻量清单，识别本数据目录的未完成状态；不要让每次 `Snapshot` 散列全部备份数据库和会话文件。完整摘要校验留在备份读取及恢复等实际使用入口
- 备份相关测试在 `BackupTests.cs`、`BackupDialogTests.cs`；覆盖结构/路径/依赖拒绝、跳过已有聊天、运行中锁竞争、索引重数、触发器、恢复与中断回滚，统一运行上面的完整测试命令
