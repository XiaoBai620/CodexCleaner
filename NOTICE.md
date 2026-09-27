# 来源与名称说明

- CodexCleaner 的项目代码采用 MIT，版权声明见 `LICENSE`。
- 本项目是独立社区工具，不是 OpenAI 官方产品；Codex、ChatGPT、OpenAI 等名称用于说明兼容对象，不表示赞助、隶属或商标授权。
- Node.js 由使用者独立安装；发布包不包含 Node.js、Codex、SQLite 二进制或第三方 npm 包。`node:sqlite` 属于 Node.js 运行环境，其许可随 Node.js 分发。
- `test/fixture-schema.json` 是 2026-09-27 观察到的 SQLite 结构兼容性元数据：表、索引、触发器的定义，没有用户数据行、聊天正文或认证值。它用于在临时目录重建匹配的测试结构；不表示这些上游结构由本项目原创，也不为上游产品授予许可证。其他测试内容均为虚构数据。
- `public/index.html` 的简单线性 SVG 图标由本项目按几何元素重新绘制，未引入第三方图标包。界面使用系统字体，无远程字体或图片请求。
- `docs/screenshot.png` 来自虚构测试数据的本机界面，不包含真实聊天。
