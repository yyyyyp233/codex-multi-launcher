# Security Policy

## Supported versions

当前仅维护 `1.5.x` preview 源码。Preview 版本不承诺稳定兼容性，不建议在无法接受配置恢复或 App 兼容风险的环境中使用。

## Reporting a vulnerability

请优先使用 GitHub 的 **Private vulnerability reporting** 私下提交安全问题，并说明受影响版本、复现条件、预期影响和最小必要日志。不要先创建公开 Issue。

无论通过哪种方式报告，都不要上传以下内容：

- `auth.json`、API Key、Token、Cookie 或完整环境变量；
- 完整 Codex Home、任务会话、SQLite 数据库或浏览器配置；
- 未脱敏的完整日志、用户名、本机绝对路径或组织内部域名；
- OpenAI、Microsoft 或其他第三方二进制文件。

可以提供经过脱敏的错误信息、最小配置片段和文件哈希。维护者会在确认收到后尽力评估，但不提供响应时限或安全保证。

## Security boundaries

- API Key 只写入所选工作空间的隔离 `auth.json`，不会进入启动器日志或快照。
- “使用已有工作空间”只原地注册多开器 `profiles` 根目录下带有效 marker 的空间，不复制内容；个人、外部或重解析点目录会被拒绝绑定。
- 启动器默认不修改个人 `config.toml` 或 `auth.json`。用户明确选择指向个人侧的 Skills、Memories、规则合并或个人快照恢复时，才会写入对应个人内容。
- 不建议为本项目、Codex 或其运行副本设置宽泛的杀毒软件白名单。应针对具体告警核对签名、哈希、命令行和进程树。
