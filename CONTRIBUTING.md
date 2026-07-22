# Contributing

感谢改进 Codex 多开器。提交代码前，请先阅读 [NOTICE.md](NOTICE.md)、[SECURITY.md](SECURITY.md) 和 [兼容性说明](docs/COMPATIBILITY.md)。

## Development setup

要求 Windows、.NET SDK `10.0.300`（`global.json` 会固定补丁系列）以及当前用户安装的 Codex Windows App。常用检查：

```powershell
dotnet restore CodexMultiLauncher.slnx
dotnet format CodexMultiLauncher.slnx --verify-no-changes --no-restore
dotnet build CodexMultiLauncher.slnx -c Release --no-restore
dotnet test CodexMultiLauncher.slnx -c Release --no-build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\audit-repository.ps1
```

## Pull requests

- 每个 PR 聚焦一个可审查的主题，并说明行为变化、隔离边界和验证层级。
- 新增配置写入或导入路径时，必须使用临时根目录测试，不得依赖真实 `%LOCALAPPDATA%` 或个人 Codex Home。
- 不得提交 `dist/`、`artifacts/`、EXE、安装包、运行配置、第三方 App 文件或生成的凭据。
- 不得在 Issue、PR、测试快照或日志中放入真实 API Key、内部域名、用户名、组织路径或完整本机日志。
- UI 变化请提供脱敏截图；不得使用含真实账号、端点或任务数据的截图。

提交采用简洁的 Conventional Commits 风格。合并后保持线性历史。
