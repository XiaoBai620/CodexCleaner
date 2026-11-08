# 来源与名称说明

- CodexCleaner 的项目代码采用 MIT，版权声明见仓库根目录的 `LICENSE`。
- 本项目是独立社区工具，不是 OpenAI 官方产品；Codex、ChatGPT、OpenAI 等名称仅用于说明兼容对象，不表示赞助、隶属或商标授权。
- 发布的 `CodexCleaner.exe` 运行在用户另行安装的 .NET 10 桌面运行时（MIT，© .NET Foundation and Contributors）上，并内含以下第三方组件，各自按其许可证分发：
  - Microsoft.Data.Sqlite（MIT，© .NET Foundation and Contributors）
  - SQLitePCLRaw（Apache License 2.0，© Eric Sink / SourceGear）
  - SQLite（公有领域）
  - ZstdSharp.Port（MIT，© Oleg Stepanischev），用于读取 Codex 压缩的 `.jsonl.zst` 会话文件首行
- 程序图标 `src/CodexCleaner/app.ico` 由 `scripts/make_icon.py` 生成，图形为 OpenAI / ChatGPT 标志（路径数据取自 Simple Icons，CC0）。该标志是 OpenAI 的商标，此处仅用于表明兼容对象，不表示官方关系；对外分发前请自行确认符合 [OpenAI 品牌使用条款](https://openai.com/brand/)。查看对话窗口的界面代码移植自同作者的 DevinCleaner（MIT）。`docs/screenshot.png` 使用虚构的测试数据生成，不含真实聊天。
- `tests/CodexCleaner.Tests/fixture-schema.json` 是 2026-10-31 观察到的 SQLite 结构兼容性元数据：只有表、索引、触发器的定义，没有用户数据行、聊天正文或认证值。它用于在临时目录重建匹配的测试结构；不表示这些上游结构由本项目原创，也不为上游产品授予许可证。其他测试内容均为虚构数据。
