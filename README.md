# CodexCleaner · Codex 本地清理台

Windows 桌面小工具：选择本机的 Codex 聊天与记忆，核对预览后一次确认删除，并修复指向已删除聊天的失效引用。

**独立社区项目，与 OpenAI 无官方隶属关系。** 只支持已验证的本机存储结构；Codex 数据格式变化时会停止清理，等待工具适配。

## 使用

需要 64 位 Windows 10/11。下载 `CodexCleaner.exe` 双击运行即可，不需要安装任何运行环境，也不会占用端口或打开浏览器。

1. 完全退出 Codex 桌面应用、Codex 命令行和 Chrome 插件。窗口顶部会实时显示 Codex 是否仍在运行；运行期间可以浏览和预览，但不能执行。
2. 勾选要删除的聊天（可按标题或 ID 搜索，已归档的聊天也会列出）。按需要勾选“清空全部本地记忆”“清空共享输入历史与日志”。
3. 点击“预览清理范围”，核对聊天、数据库记录和文件，点击“确认删除”。
4. 之前删除后在 Codex 里遇到项目或归档错误时，点击“检查失效引用”，核对后“确认修复”。

exe 没有代码签名，首次运行时 SmartScreen 可能提示“未知发布者”，可以对照发布页的 `SHA256SUMS.txt` 核对文件。

默认读取 `CODEX_HOME`，未设置时读取 `%USERPROFILE%\.codex`。所有处理都在本机完成。

更多说明见 [使用说明](使用说明.md)。

## 会删除什么

| 选项 | 处理内容 |
|---|---|
| 勾选聊天 | 已验证数据库中属于该聊天的记录、原始会话文件、会话索引、输入历史中带该会话 ID 的行、桌面状态中识别出的该聊天字段，以及该聊天直接提取的记忆。附着在它下面的子聊天会解除关联并保留。 |
| 清空全部本地记忆 | 记忆提取结果、记忆整理任务，以及 `memories`、`memories_v2`、`memories_extensions` 里的文件。 |
| 清空共享输入历史与日志 | 桌面全局输入历史和 `logs_2.sqlite` 中的全部日志，会影响所有聊天。 |
| 检查失效引用 | 只清除指向已不存在聊天的本机引用，保留现存聊天、项目和远端主机记录。 |

项目、工作树、插件、技能、设置、登录信息和自动化定义都不在清理范围。被自动化任务引用的聊天不能删除，需要先在 Codex 里处理该任务。删除是普通的本机删除，不能撤销，也不是磁盘安全擦除；云端记录和外部备份不受影响。

## 兼容性与安全

- 支持的数据库结构来自 **2026-10-31** 的兼容性检查。工具会校验每个数据库的表和触发器定义指纹，任何一个不匹配都只允许浏览。
- 写入前检查 Codex 进程（包括商店版应用、命令行和数据目录内的插件程序），并在执行过程中多次复查。
- 预览 15 分钟内有效，并绑定数据指纹；预览后数据有任何变化都会要求重新预览。
- 执行时先把要修改的数据库和文件备份到 `%LOCALAPPDATA%\CodexCleaner\Transactions`，出错自动回滚并校验。成功或回滚成功后删除备份；回滚本身失败时保留故障材料并锁定后续写入，界面会显示具体路径。
- 拒绝越界路径、符号链接、目录联接和硬链接。

内部结构见 [架构与兼容性](docs/architecture.md)。

## 开发

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)。

```powershell
dotnet test
dotnet publish src/CodexCleaner -c Release -o dist
```

测试在系统临时目录生成虚构数据，不读取也不修改真实的 `.codex`。发布步骤见 [发布指南](docs/releasing.md)。

```text
src/CodexCleaner.Core/   清理引擎：结构校验、预览计划、事务执行与回滚、进程检测
src/CodexCleaner/        WinForms 桌面界面
tests/                   合成数据与回归测试
docs/                    架构与发布说明
```

欢迎提交修复和合成数据回归测试，请先读 [贡献指南](CONTRIBUTING.md)；安全问题见 [安全说明](SECURITY.md)。

## License

项目代码采用 [MIT](LICENSE)。第三方组件与兼容性数据来源见 [NOTICE](NOTICE.md)。

## English

CodexCleaner is an independent native Windows utility for reviewing and deleting recognized local Codex chats and memories, with explicit previews and repair of stale local references. It ships as a single self-contained exe, refuses writes to unverified database schemas or while Codex is running, and rolls back automatically on failure. It does not erase cloud records or perform forensic disk wiping.
