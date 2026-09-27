# Privacy

Codex 多开器本身不包含遥测、分析 SDK、广告或维护者自建的崩溃上报，也不会由启动器主动把配置、日志或使用数据发送给维护者。

## Local data read

启动器可能读取：

- 当前用户安装的 Codex Windows App 包注册信息、安装目录与包身份；
- 当前 Codex/ChatGPT 桌面进程的 PID、创建时间、可执行路径和命令行，用于判断 Profile 归属；
- 当前用户 HKCU `Environment` 中 `CODEX_HOME` 与 `CODEX_SQLITE_HOME` 的原始值和值类型，用于安全恢复启动事务；
- `%USERPROFILE%\.codex` 中用于对比或由用户明确迁移的全局规则、用户 Skills、受管 Memories 和 MCP 配置；
- 用户在“使用已有工作空间”向导中明确选择的本机工作空间目录；
- `%LOCALAPPDATA%\CodexChannelLauncher` 中由本工具创建的 Profile 注册、快照、合并基线、状态、环境恢复 journal 和日志。

## Local data written

启动器的持久运行数据默认写入 `%LOCALAPPDATA%\CodexChannelLauncher`。其中包括隔离工作空间、Electron 用户数据、快照、三方合并基线、状态、恢复 journal 和诊断日志。

在隔离 Profile 的 MSIX 激活事务中，启动器会**临时修改当前 Windows 用户级** `CODEX_HOME` 与 `CODEX_SQLITE_HOME`，并在验证启动后恢复原值。恢复 journal 仅用于记录这两个变量恢复所需的路径值和值类型；它不保存 API Key、Token、Cookie 或 `auth.json` 内容。

API Key 只写入隔离工作空间的 `auth.json`。它不会显示在界面中，不会进入启动器日志、快照、恢复 journal 或仓库。个人 `config.toml` 与 `auth.json` 不由启动器自动修改。

当用户明确执行“工作空间 → 个人”的 Skills、Memories、全局规则合并，或恢复个人快照时，启动器会修改箭头指向的个人内容；执行前会显示目标和安全提示。

当前 MSIX 启动路径不复制、不修改官方 Codex App 安装文件。

## Network behavior

启动器自身不调用维护者服务器。启动后的 Codex App、配置的模型 Provider、MCP、浏览器插件或电脑操作插件有各自的网络和隐私行为，不属于本项目的数据处理范围。

## Removal

完全退出个人/隔离 Codex 实例和多开器后，删除 `%LOCALAPPDATA%\CodexChannelLauncher` 即可移除本工具创建的运行数据、恢复 journal 和旧版遗留缓存。该操作不会删除个人 `%USERPROFILE%\.codex`。