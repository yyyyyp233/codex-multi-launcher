# Architecture

## Goal

Codex 多开器在同一 Windows 用户会话中保留一个个人主入口，并允许任意数量的隔离 Profile。个人实例继续使用默认 Codex Home；每个隔离实例都使用自己的 Codex Home、Electron 用户数据、本地 App 运行变体和颜色角标。

```text
Codex 多开器
├─ 个人入口 → Windows Store 注册入口 → %USERPROFILE%\.codex
└─ 隔离 Profile 入口（0..N）
   ├─ 注册表：state\profiles.json
   ├─ Codex Home：profiles\<directory-id>\codex-home
   ├─ Electron 数据：profiles\<directory-id>\electron
   └─ App 运行变体：runtime-cache\<package-version>-<profile-id>-<color>
```

所有启动器数据默认位于 `%LOCALAPPDATA%\CodexChannelLauncher`。`<id>` 是受限的本机目录标识，界面显示名独立保存，可由用户修改。

## Profile lifecycle

运行状态分为：

- `NotConfigured`：没有注册；
- `Configured`：注册、marker、`config.toml` 以及所选认证方式所需的认证状态有效；ChatGPT 账号模式允许在首次启动后登录；
- `Invalid`：注册或配置存在但无法安全解析。

新版注册表缺失时，启动器会将发现的全部有效旧 marker 一次性登记到 `profiles.json`，并消费旧单例注册文件。它不会移动 profile，也不会重写配置或认证；之后运行只读取新注册表。

新建隔离空间先在运行根目录下的 staging 目录完成配置、认证和 marker，再以目录移动提交。注册表、配置与 API Key 文件采用临时文件、刷新磁盘并原子替换。API Key 不进入错误信息、日志、注册、marker 或快照。

删除工作空间默认只从 `profiles.json` 注销，保留全部本地内容供后续原地重新接入。用户显式选择同时删除本地内容时，Profile、快照、合并基线和专属运行副本会先移动到 staging 隔离目录；注册表提交失败时按逆序移回，提交成功后才清理隔离目录。运行中的 Profile 一律拒绝删除，个人 Codex Home 永不进入删除集合。

认证模式由注册表明确记录：`ChatGptAccount`、`OpenAiApiKey`、`CustomResponses`。切换认证模式时会先校验新模式所需字段；切换到账号模式只在用户明确保存后移除旧 API Key 文件。

## Attach existing profile

“使用已有工作空间”只接受 `profiles/<directory-id>` 或其 `codex-home`，并要求存在有效的多开器 marker。接入事务完成以下操作：

- 校验路径、marker、配置与认证状态；
- 从现有目录名建立新的注册和唯一角标颜色；
- 原子更新 `profiles.json`。

该流程不创建新的 Profile 目录，也不复制或重写 Codex Home、会话、SQLite、插件、Skills、Memories 和 Electron 数据。个人 Codex Home、外部目录以及重解析点会被拒绝；外部资源迁移继续由配置中心的显式资源操作负责。

## Configuration center

配置中心管理工作空间能力、MCP、权限和快照。Skills、Memories 与全局规则支持显式双向操作；任何指向个人侧的写入都必须由用户选择并在界面确认。快照明确排除认证文件。

## Test boundary

测试通过 `LauncherPathOverrides` 把个人目录、运行目录和 Electron 数据全部指向单次测试的临时根。测试不读取或写入真实 `%LOCALAPPDATA%\CodexChannelLauncher` 和 `%USERPROFILE%\.codex`。
