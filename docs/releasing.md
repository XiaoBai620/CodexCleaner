# 发布

发布由 GitHub Actions 完成：推送 `v<版本>` 标签后，CI 运行测试、构建 exe、生成 `SHA256SUMS.txt`，并用 `CHANGELOG.md` 中对应版本的内容创建 Release。

1. 更新 `src/CodexCleaner/CodexCleaner.csproj` 中的 `<Version>`，并在 `docs/CHANGELOG.md` 顶部新增 `## <版本>` 一节。
2. 本地运行 `dotnet test tests/CodexCleaner.Tests`，并用虚构数据或测试用的 `CODEX_HOME` 手动检查一遍界面：列表、搜索、全选、预览、确认、检查失效引用，以及 Codex 运行时确认按钮不可用。
3. 提交并推送到 `main`，等 CI 通过。
4. 打标签并推送：

   ```powershell
   git tag -a v3.0.0 -m "CodexCleaner 3.0.0"
   git push origin v3.0.0
   ```

   标签必须与 `<Version>` 一致，否则 CI 会失败；`CHANGELOG.md` 中缺少对应版本一节时也会失败，不会创建 Release。

本地构建 exe：`dotnet publish src/CodexCleaner -c Release -o dist`。

`CodexCleaner.exe` 是单文件程序，内含本项目代码和 SQLite 原生库，约 3 MB；运行时需要用户安装 .NET 10 桌面运行时（`RollForward=LatestMajor`，之后更高版本的运行时也能运行）。exe 没有代码签名；`SHA256SUMS.txt` 只用于核对下载完整性，不能证明作者身份。

CI 只在标签构建时获得写 Release 的权限，使用 GitHub 自动提供的临时令牌，不保存任何发布密钥。

不要把真实 `.codex` 数据、事务目录、日志或检查副本加入提交或发布。`docs/screenshot.png` 使用虚构数据和 `C:\Demo\.codex` 生成。
