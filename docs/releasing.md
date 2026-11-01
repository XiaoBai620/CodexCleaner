# 发布

1. 更新 `src/CodexCleaner/CodexCleaner.csproj` 中的 `<Version>` 和 `CHANGELOG.md`。
2. 运行 `dotnet test tests/CodexCleaner.Tests`，并用虚构数据或测试用的 `CODEX_HOME` 手动检查一遍界面：列表、搜索、全选、预览、确认、检查失效引用，以及 Codex 运行时确认按钮不可用。
3. 生成单文件 exe 和校验文件：

   ```powershell
   dotnet publish src/CodexCleaner -c Release -o dist
   certutil -hashfile dist\CodexCleaner.exe SHA256
   ```

   把哈希写入 `dist/SHA256SUMS.txt`，格式为 `<sha256>  CodexCleaner.exe`。
4. 提交后等待 GitHub 的 Windows CI 通过，再手动创建与版本一致的标签和 Release，上传 `CodexCleaner.exe` 和 `SHA256SUMS.txt`。

`CodexCleaner.exe` 是单文件程序，内含本项目代码和 SQLite 原生库，约 3 MB；运行时需要用户安装 .NET 10 桌面运行时（`RollForward=LatestMajor`，之后更高版本的运行时也能运行）。exe 没有代码签名；`SHA256SUMS.txt` 只用于核对下载完整性，不能证明作者身份。

CI（`.github/workflows/ci.yml`）只执行测试和发布构建，并上传构建产物供维护者检查；不会自动公开发布 Release，也不使用任何发布令牌。

不要把真实 `.codex` 数据、事务目录、日志或检查副本加入提交或发布。
