# Privacy

Codex 多开器本身不包含遥测、分析 SDK、崩溃上报、广告或自动更新检查，也不会由启动器主动把配置、日志或使用数据发送给维护者。

## Local data read

启动器可能读取：

- 当前用户安装的 Codex Windows App 包注册信息和安装目录；
- `%USERPROFILE%\.codex` 中用于对比或由用户明确迁移的全局规则、用户 Skills、受管 Memories 和 MCP 配置；
- 用户在导入向导中明确选择的 Codex Home；
- `%LOCALAPPDATA%\CodexChannelLauncher` 中由本工具创建的注册、快照、运行副本和日志。

## Local data written

启动器默认只写入 `%LOCALAPPDATA%\CodexChannelLauncher`。其中包括隔离工作空间、Electron 用户数据、运行副本、快照、三方合并基线、状态和诊断日志。

API Key 只写入隔离工作空间的 `auth.json`。它不会显示在界面中，不会进入启动器日志、快照或仓库。个人 `config.toml` 与 `auth.json` 不由启动器修改。

当用户明确执行“工作空间 → 个人”的 Skills、Memories、全局规则合并，或恢复个人快照时，启动器会修改箭头指向的个人内容；执行前会显示目标和安全提示。

## Network behavior

启动器自身不调用维护者服务器。启动后的 Codex App、配置的模型 Provider、MCP、浏览器插件或电脑操作插件有各自的网络和隐私行为，不属于本项目的数据处理范围。

## Removal

完全退出两个 Codex 实例和多开器后，删除 `%LOCALAPPDATA%\CodexChannelLauncher` 即可移除本工具创建的运行数据。该操作不会删除个人 `%USERPROFILE%\.codex`。
