# CodexCleaner · Codex 本地清理台

一个手动控制的 Windows 本地工具：查看 Codex 文件用途，选择聊天与记忆，核对预览后一次确认删除，检查并修复已识别的失效聊天引用。

**独立社区项目，与 OpenAI 无官方隶属关系。** 仅支持已验证的本机存储结构。数据格式变化时停止清理，需要适配后才能继续。

![使用虚构聊天数据的界面](docs/screenshot.png)

## 能做什么

- 按标题或 ID 搜索、选择聊天，包括归档聊天，同步清理已识别的关联记录。
- 单独清空已识别的本地记忆，或明确选择共享输入历史与数据库日志。
- 查看文件与目录的用途、大小和处理方式；未知内容保留。
- 预览并修复指向不存在聊天的本机项目迁移、归档、置顶等引用。
- 使用紧凑界面、键盘操作和窄屏布局；不要求输入确认词，不设日常可恢复区。

项目、工作树、插件、技能、登录信息和定时任务定义不属于清理范围。删除是普通本地删除，无法通过工具撤销；云端记录、外部备份和未识别文件不受影响。详细范围见 [使用说明](使用说明.md)。

## 开始使用

需要 **Windows 10/11、Node.js 24 或更新版本（含 `node:sqlite`）**，以及当前版本的 Edge、Chrome 或 Firefox。安装 Node.js 请使用 [官方网站](https://nodejs.org/)。工具没有 npm 运行依赖。

1. 将发布 ZIP 完整解压到独立目录。
2. 双击 `Start-CodexCleaner.cmd`，浏览器会打开本机界面。
3. 完全退出 Codex 桌面应用、命令行和其他写入同一数据目录的进程。
4. 勾选内容，点击“预览清理范围”，核对后点击“确认删除”。
5. 完成后点击“退出工具”，再启动 Codex。仅关闭标签页不会停止后台服务。

默认读取 `CODEX_HOME`，未设置时读取 `%USERPROFILE%\.codex`。数据只在本机处理；HTTP 服务绑定 `127.0.0.1`，每次启动使用独立的访问凭证。

也可以在项目目录运行：

```powershell
npm start
```

如浏览器未能自动打开，在终端运行 `node server.cjs --no-open`，使用终端显示的临时本地链接。该链接包含访问凭证，不要贴进 Issue。参数说明见 `node server.cjs --help`。

## 兼容性与限制

- 生产使用仅支持 Windows；自动测试使用 Windows 和 Node.js 24。未承诺 macOS、Linux 或 WSL 的进程检测。
- 当前支持的数据库结构来自 **2026-10-31 的兼容性检查**。相同文件名不代表结构相同；工具校验表及触发器定义的指纹，拒绝未支持的版本。
- “检查失效引用”无法重建已删除的聊天正文，也不保证修复所有项目或归档错误。
- 清空记忆不会关闭记忆生成功能；派生聊天、项目导出、语音文件、外部备份等可能另有内容副本。
- 执行期间使用短暂的内部事务副本。成功或验证回滚成功后删除；断电、强制终止或回滚失败时保留故障材料并锁定后续写入。具体路径会显示在界面。

内部结构和故障处理见 [架构与兼容性](docs/architecture.md)。

## 开发与检查

无需安装第三方依赖，在完整源码目录运行：

```powershell
npm run check
npm test
npm run package
```

测试在系统临时目录创建虚构数据，不读取或修改真实 `.codex`。请勿把真实数据目录用作测试样本。

打包命令在 `dist/` 生成 Windows 使用包、完整源码包和 `SHA256SUMS.txt`，只收录明确列入发布清单的文件。提交到 GitHub 时，以本目录作为仓库根目录；不要提交父目录的工作文件、日志或真实数据。完整发布步骤见 [发布指南](docs/releasing.md)。

```text
server.cjs                    本机 HTTP 服务与 Windows 进程检测
lib/cleaner.cjs                清理计划、数据校验、事务和关联修复
public/                       原生 HTML / CSS / JavaScript 界面
test/                         合成数据库与回归测试
scripts/                      源码检查、白名单打包
docs/                         架构、界面示例、发布说明
.github/                      Windows CI 与反馈模板
```

欢迎提交修复和合成数据回归测试。请先读 [贡献指南](CONTRIBUTING.md)；安全问题见 [安全说明](SECURITY.md)。

## License

项目代码采用 [MIT](LICENSE)。运行环境、兼容性数据与素材来源见 [NOTICE](NOTICE.md)。

## English

CodexCleaner is an independent, local Windows utility for reviewing and deleting recognized Codex chats and memories. It offers explicit previews, file-purpose explanations, and repairs for supported stale local references. It requires Node.js 24+, has no runtime npm dependencies, and refuses writes to unsupported database schemas. It does not erase cloud records or perform forensic disk wiping. Close all writers to the selected data directory before confirming a cleanup.
