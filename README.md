# CodexCleaner · Codex 本地清理台

Windows 桌面小工具：选择本机的 Codex 聊天与记忆，核对预览后一次确认删除，并修复指向已删除聊天的失效引用。

**独立社区项目，与 OpenAI 无官方隶属关系。** 只支持已验证的本机存储结构；Codex 数据格式变化时会停止清理，等待工具适配。

![使用虚构聊天数据的界面（当前稳定版截图）](docs/screenshot.png)

## 使用

需要 64 位 Windows 10/11 和 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（Windows Desktop Runtime）。从 [Releases](https://github.com/XiaoBai620/CodexCleaner/releases/latest) 下载 `CodexCleaner.exe`（约 3 MB）双击运行；没装运行时的话，Windows 会弹窗提示并给出下载地址。工具不占用端口。

启动后会在后台检查 GitHub 上的最新正式版本，有新版本时显示更新提示，也可点击“检查更新”。点击“前往更新”才会打开本项目的发布页，由你下载替换程序，不会自动下载安装。断网或检查失败不影响清理。

1. 按标题或 ID 搜索，也可按状态筛选。点选一行，在右侧查看完整标题、路径、消息数和处理原因；点击“查看对话”或双击行可只读查看对话。勾选框用于选择清理内容。“全选结果”选择当前筛选中的可清理聊天；底部统计包含筛选外已选记录。
2. Codex 开着也能清理：正在 Codex 中使用（已加载、或有子任务在跑）的聊天标为“使用中”，不能勾选；其他聊天可以直接预览并确认。运行期间不修改桌面界面状态（置顶、草稿等），退出 Codex 后点击“检查失效引用”清除残留。
3. “全部本地记忆”“共享输入历史与日志”默认不勾选，需要先完全退出 Codex 桌面应用和命令行才能选择。点击“预览清理范围”，逐项核对聊天、数据库、文件及保留的会话文件，再点击“确认清理”。
4. 之前删除后在 Codex 里遇到项目或归档错误时，退出 Codex 后点击“检查失效引用”，核对后“确认修复”。

exe 没有代码签名，首次运行时 SmartScreen 可能提示“未知发布者”（点“更多信息”→“仍要运行”）。可以对照发布页的 `SHA256SUMS.txt` 核对文件：`certutil -hashfile CodexCleaner.exe SHA256`。

默认读取 `CODEX_HOME`，未设置时读取 `%USERPROFILE%\.codex`。聊天与记忆的读取、预览和清理都在本机完成。更新检查仅请求 GitHub 的公开版本信息，不上传聊天、记忆、文件路径、登录信息或本地文件。

更多说明见 [使用说明](docs/使用说明.md)，版本变化见 [更新记录](docs/CHANGELOG.md)。

显示“仅清理记录”的聊天仍可勾选。这表示会话路径为空、不受支持或位于当前数据目录外；清理会移除已验证的本地记录与关联数据，不会按该异常路径访问会话文件。预览会明确列出保留的文件；若与其他清理范围冲突，会停止并提示调整。原文件以后被再次导入时，聊天可能重新出现。

## 会删除什么

| 选项 | 处理内容 |
|---|---|
| 勾选聊天 | 已验证数据库中属于该聊天的记录、经过验证的原始会话文件（含分段文件和 `.jsonl.zst` 压缩副本）、会话索引、输入历史中带该会话 ID 的行、桌面状态中识别出的该聊天字段，以及该聊天直接提取的记忆。附着在它下面的子聊天会解除关联并保留。 |
| 仅清理记录 | 会话路径异常或未提供时，移除该聊天的本地记录与已识别关联；不按异常路径查找、读取或删除会话文件。 |
| 清空全部本地记忆 | 记忆提取结果、记忆整理任务，以及 `memories`、`memories_v2`、`memories_extensions` 里的文件。 |
| 清空共享输入历史与日志 | 桌面全局输入历史和 `logs_2.sqlite` 中的全部日志，会影响所有聊天。 |
| 检查失效引用 | 只清除指向已不存在聊天的本机引用，保留现存聊天、项目和远端主机记录。 |

项目、工作树、插件、技能、设置、登录信息和自动化定义都不在清理范围。被自动化任务引用的聊天不能删除，需要先在 Codex 里处理该任务；被其他分叉聊天当作历史来源的聊天需要和分叉聊天一起删除。删除是普通的本机删除，不能撤销，也不是磁盘安全擦除；云端记录和外部备份不受影响。

## 兼容性与安全

- 支持的数据库结构来自 **2026-10-31** 的兼容性检查。工具会校验每个数据库的表和触发器定义指纹，任何一个不匹配都只允许浏览。
- 写入前检查 Codex 进程（包括商店版应用、命令行和数据目录内的 Codex 程序）。Codex 退出时按原有流程执行，并在过程中多次复查进程。
- Codex 运行时，按 Codex 自己的 `thread-writer-locks` 写入锁判断聊天是否在使用；执行时先占住所选聊天的写入锁，Codex 无法同时打开它们。共享数据库只删除这些聊天的行，每个库单独事务，失败的库自动回滚。
- 预览 15 分钟内有效。Codex 退出时绑定所有相关文件的数据指纹；运行时绑定所选聊天的更新时间和会话文件指纹。预览后有变化都会要求重新预览。
- 执行时先把要修改的数据库和文件备份到 `%LOCALAPPDATA%\CodexCleaner\Transactions`，出错自动回滚并校验。成功或回滚成功后删除备份；回滚本身失败时保留故障材料并锁定后续写入，界面会显示具体路径。
- 文件删除拒绝越界路径、符号链接、目录联接和硬链接。“仅清理记录”不放宽文件路径检查，也不能绕过数据库或自动化保护。

内部结构见 [架构与兼容性](docs/architecture.md)。

## 开发

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)。

```text
dotnet test tests/CodexCleaner.Tests
dotnet publish src/CodexCleaner -c Release -o dist
```

测试在系统临时目录生成虚构数据，不读取也不修改真实的 `.codex`。发布步骤见 [发布指南](docs/releasing.md)。

```text
src/CodexCleaner/        WinForms 桌面界面
src/CodexCleaner/Engine/ 清理引擎：结构校验、预览计划、事务执行与回滚、进程检测
tests/                   合成数据与回归测试
docs/                    使用说明、架构、发布、贡献与安全说明
```

欢迎提交修复和合成数据回归测试，请先读 [贡献指南](docs/CONTRIBUTING.md)；安全问题见 [安全说明](docs/SECURITY.md)。

## License

项目代码采用 [MIT](LICENSE)。第三方组件与兼容性数据来源见 [NOTICE](docs/NOTICE.md)。

## English

CodexCleaner is an independent native Windows utility for reviewing and deleting recognized local Codex chats and memories, with explicit previews and repair of stale local references. It ships as a single small exe that requires the .NET 10 Windows Desktop Runtime, refuses writes to unverified database schemas, deletes idle chats while Codex runs by holding Codex's own per-thread writer locks, and rolls back automatically on failure. It does not erase cloud records or perform forensic disk wiping.
