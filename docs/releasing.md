# 提交与发布

以包含 `package.json`、`server.cjs` 的 **CodexCleaner 目录**作为仓库根目录。若下载的是源码 ZIP，先完整解压其中的 `CodexCleaner/`。

首次提交前，在该目录运行：

```powershell
npm run check
npm test
npm run package
git init -b main
git add .
git diff --cached --stat
git diff --cached
```

确认暂存区只有项目源码、文档、虚构测试数据和 CI 配置后，使用自己的 Git 姓名与邮箱提交：

```powershell
git commit -m "Prepare CodexCleaner for open source"
```

在 GitHub 创建自己的仓库，然后按 GitHub 显示的实际 remote 地址连接并推送。不要把开发工作区的父目录、真实 `.codex`、运行日志、故障事务或本地启动链接加入提交。项目不预设作者账户、仓库地址或任何发布凭证。

## 新版本

1. 更新 `package.json` 的版本，并运行 `npm install --package-lock-only --ignore-scripts` 同步锁文件；更新 `CHANGELOG.md`。
2. 用 `npm run check`、`npm test` 检查当前源码；涉及 UI 时完成实际浏览器验收。
3. 运行 `npm run package`，检查 `dist/` 中的两个 ZIP 和校验清单。
4. 将 Windows 包解压到新的目录，检查启动、退出及虚构数据流程；源码包应能独立执行检查和测试。
5. 提交后等待 GitHub 的 Windows CI 通过，再手动创建与版本一致的标签和 Release。

`dist/CodexCleaner-Windows-v<版本>.zip` 提供运行所需文件和用户文档；`dist/CodexCleaner-Source-v<版本>.zip` 还包含测试、CI 和开发脚本。两个压缩包都不包含 Node.js。`SHA256SUMS.txt` 可用于核对下载完整性，它不是作者身份签名。

打包器按 `scripts/release-files.json` 明确枚举文件，拒绝目录联接、符号链接和越界路径，并逐文件验证 ZIP 内的 SHA-256。固定条目顺序和时间戳使同一份字节内容在相同压缩运行环境中可重复打包。Git 的换行转换会改变源码字节和归档哈希。

`.github/workflows/ci.yml` 只执行检查、测试和构建，再上传供维护者检查的构建产物；不会自动公开发布 Release，也不使用发布令牌。仓库建立后，可在 GitHub 设置中开启私密漏洞报告和主分支保护。
