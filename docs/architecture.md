# 架构与兼容性

程序只有一个项目 `src/CodexCleaner`：根目录下是 WinForms 界面，`Engine/` 子目录是清理引擎，两者在同一进程内直接调用。没有网络服务、端口或浏览器。引擎不接受任意 SQL 或任意删除路径：界面只能提交聊天 ID 和两个开关，引擎生成固定规则的预览计划，执行时按计划 ID 取回已验证的操作。

| `Engine/` 下的文件 | 内容 |
|---|---|
| `Catalog.cs` | 支持的数据库结构指纹、桌面状态字段清单 |
| `Cleaner.cs` | 路径安全、数据指纹、读写互斥、跨进程写入锁、快照、故障材料检查 |
| `Cleaner.Plan.cs` | 每个数据库的删除动作、桌面状态与索引改写、预览与失效引用检查 |
| `Cleaner.Execute.cs` | 备份、事务执行、结果校验、自动回滚、事务目录清理 |
| `DesktopState.cs` | `.codex-global-state.json` 中本机聊天引用的识别与清理 |
| `AutomationToml.cs` | 自动化定义中目标聊天的识别 |
| `CodexProcesses.cs` | Codex 进程检测 |
| `Db.cs` | SQLite 只读/读写打开、结构指纹计算 |
| `Models.cs`、`SafePaths.cs` | 数据类型；路径与硬链接检查 |

## 数据结构

以下名称和 `Catalog.cs` 中的结构指纹来自 2026-10-31 的兼容性检查，不是 Codex 对外保证的稳定 API，也不代表所有 Codex 版本都受支持。

| 数据库 | 处理对象 |
|---|---|
| `state_5.sqlite` | 聊天记录、附件、动态工具、已读回执、审核反馈、派生关系；子聊天的 `attached_thread_parent_id` 置空后保留；项目保留 |
| `thread_history_1.sqlite` | 消息、轮次和实时历史投影 |
| `memories_1.sqlite` | 直接提取的记忆；明确全选时清理整理任务 |
| `logs_2.sqlite` | 所选聊天的日志；明确全选时清理全部日志 |
| `goals_1.sqlite` | 聊天目标及延续状态 |
| `queue_1.sqlite` | 待处理消息和队列版本 |
| `sqlite/codex-dev.db` | 本机聊天目录、收件箱、时间线、可视化建议；修改目录后递增 `catalog_revision` |

指纹是所有表和触发器定义（按 `类型:名称` 排序）的 SHA-256，严格匹配，不为适配新版而忽略差异。`tests/CodexCleaner.Tests/fixture-schema.json` 保存同一批结构定义（只有结构，没有数据行），测试用它在临时目录重建数据库，并验证重建结果与 `Catalog.cs` 的指纹一致。

适配新版 Codex 时，除了更新指纹和 `fixture-schema.json`，还要逐个核查新增的表、列、触发器和级联关系是否引用聊天 ID，并补充对应的删除动作和回归测试。

原始会话文件必须位于 `sessions/` 或 `archived_sessions/`，且首行 `session_meta` 的 ID 与聊天一致。正文缺失时可以只清理索引。桌面 JSON 状态按明确字段处理：只有能确认属于本机数据目录的引用才会被修改，不会删除任意匹配 UUID 的字符串。

## 写入流程

1. 预览：先记录所有数据库、桌面状态、索引和自动化定义的数据指纹，再读取路径和行数，最后重新计算指纹；期间有任何变化就拒绝生成预览。计划 15 分钟内有效。
2. 执行前：取得实例互斥和跨进程写入锁（`.mutation.lock`，可回收已退出进程留下的锁），确认 Codex 已退出，确认没有未处理的故障材料，重新检查结构、指纹、自动化关联和记忆文件列表。
3. 备份将修改的文件和数据库（SQLite 在线备份），逐个校验备份指纹。
4. 再次检查进程和指纹，对所有数据库取得 `BEGIN IMMEDIATE` 写锁后再复查一次指纹，然后执行删除和文件改写，再检查一次进程后逐个提交。
5. 提交后校验：目标行已不存在、`quick_check` 通过、文件已删除或内容一致。全部通过后删除事务目录。
6. 任何一步失败：回滚未提交的事务，用备份还原已修改的数据库和文件并逐个校验。还原成功则删除事务目录；还原失败则把状态写成 `needs-recovery` 并保留材料，之后的写入会被拒绝。

多个 SQLite 文件和普通文件之间没有操作系统级的原子提交。事务副本用于自动回滚可捕获的失败，不能保证断电后无需人工处理。

进程检测按进程名识别 Codex 命令行，按程序路径识别商店版 Codex 包（`WindowsApps\OpenAI.Codex_*`）和数据目录内的 Codex 程序（例如插件用的 `.plugin-appserver\codex.exe`）。两个常驻辅助程序不计入：商店版随系统启动的 `codex-windows-sandbox-service`，以及浏览器插件的原生通信桥 `extension-host`（由浏览器按需重启，只把消息转给 `codex.exe`，本身不链接 SQLite、不写聊天数据库）。真正写数据的 `codex.exe` 仍会被检测到。检测和文件指纹无法防御同一账户下恶意程序的并发替换；使用者需要停止所有写入目标目录的程序。

## 目录

- 数据目录：`CODEX_HOME`，未设置时为 `%USERPROFILE%\.codex`。
- 事务目录：`%LOCALAPPDATA%\CodexCleaner\Transactions`。与数据目录不能互相包含。
- 只读检查副本：系统临时目录下的 `codex-cleaner-inspect-*`，关闭数据库后删除。
- 自动测试：系统临时目录下 `codex-cleaner-test-*` 的虚构数据。
