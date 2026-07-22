# Architecture

## Goal

Codex 多开器在同一 Windows 用户会话中提供两个明确入口：个人实例继续使用默认 Codex Home；工作空间实例使用独立 Codex Home、Electron 用户数据和本地 App 运行副本。

```text
Codex 多开器
├─ 个人入口 → Windows Store 注册入口 → %USERPROFILE%\.codex
└─ 工作空间入口
   ├─ 注册：state\work-profile.json
   ├─ Codex Home：profiles\<id>\codex-home
   ├─ Electron 数据：profiles\<id>\electron
   └─ App 运行副本：runtime-cache\<package-version>
```

所有启动器数据默认位于 `%LOCALAPPDATA%\CodexChannelLauncher`。`<id>` 是受限的本机目录标识，界面显示名独立保存，可由用户修改。

## Profile lifecycle

运行状态分为：

- `NotConfigured`：没有注册；若发现多个旧 marker，会要求用户选择；
- `Configured`：注册、marker、`config.toml` 和 `auth.json` 均有效；
- `Invalid`：注册或配置存在但无法安全解析。

注册文件缺失且只发现一个有效旧 marker 时，启动器只写入注册文件并继续使用原目录。它不会移动 profile，也不会重写配置或认证。

新建工作空间先在运行根目录下的 staging 目录完成配置、认证和 marker，再以目录移动提交；单个配置文件采用同目录临时文件、刷新磁盘并原子替换。API Key 不进入错误信息、日志、注册或 marker。

## Import allowlist

导入只复制：

- 根目录的 `config.toml`、`auth.json`、`AGENTS.md` 和 `AGENTS.override.md`；
- `skills/` 下的用户 Skills，排除 `.system`、重解析点和常见凭据文件；
- `memories/` 下符合受管 Memory 规则的文件。

其他根目录不会遍历，因此任务会话、SQLite、日志、插件缓存和 Electron 数据不会进入新 profile。

## Configuration center

配置中心管理工作空间能力、MCP、权限和快照。Skills、Memories 与全局规则支持显式双向操作；任何指向个人侧的写入都必须由用户选择并在界面确认。快照明确排除认证文件。

## Test boundary

测试通过 `LauncherPathOverrides` 把个人目录、运行目录和 Electron 数据全部指向单次测试的临时根。测试不读取或写入真实 `%LOCALAPPDATA%\CodexChannelLauncher` 和 `%USERPROFILE%\.codex`。
