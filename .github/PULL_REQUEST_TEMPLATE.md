## Summary

说明本次变更及其范围。

## Isolation and privacy impact

- 读取哪些目录：
- 写入哪些目录：
- 是否可能修改个人 profile：
- 是否处理凭据：

## Verification

- [ ] `dotnet format CodexMultiLauncher.slnx --verify-no-changes --no-restore`
- [ ] `dotnet build CodexMultiLauncher.slnx -c Release --no-restore`
- [ ] `dotnet test CodexMultiLauncher.slnx -c Release --no-build`
- [ ] `tools/audit-repository.ps1`
- [ ] UI 变化已提供脱敏截图，或说明不涉及 UI

## Sensitive-data check

- [ ] 不含 `auth.json`、真实 API Key、Token、内部域名、用户名、完整本机路径、完整运行日志或第三方二进制
