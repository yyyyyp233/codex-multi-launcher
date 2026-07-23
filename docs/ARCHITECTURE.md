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

正常主窗口采用托盘驻留生命周期：标题栏关闭与 `Alt+F4` 取消窗口关闭并隐藏主窗口，托盘左键或“显示主窗口”恢复；托盘“退出多开器”、预览渲染完成以及 Windows 会话结束才允许真实关闭。托盘退出会先移除图标，再关闭窗口并结束 WPF Application，避免残留图标或后台进程。

## Profile lifecycle

运行状态分为：

- `NotConfigured`：没有注册；
- `Configured`：注册、marker、`config.toml` 以及所选认证方式所需的认证状态有效；ChatGPT 账号模式允许在首次启动后登录；
- `Invalid`：注册或配置存在但无法安全解析。

新版注册表缺失时，启动器会将发现的全部有效旧 marker 一次性登记到 `profiles.json`，为 marker 原子补写稳定 `ProfileId`，并消费旧单例注册文件。它不会移动 profile，也不会重写配置或认证；之后运行只读取新注册表。

新建隔离空间先在运行根目录下的 staging 目录完成配置、认证和 marker，再以目录移动提交。编辑已有空间时，配置、认证、marker 与注册表作为一个带逆序回滚的文件事务提交，避免部分成功。API Key 不进入错误信息、日志、注册、marker 或快照。

删除工作空间默认只从 `profiles.json` 注销，保留全部本地内容和 marker 中的稳定 `ProfileId`，供后续原地重新接入。用户显式选择同时删除本地内容时，Profile、快照、合并基线和专属运行副本会先移动到 staging 隔离目录；每个所有者根及完整祖先链都必须不存在重解析点，注册表提交失败时按逆序移回，提交成功后才清理隔离目录。运行中的 Profile 一律拒绝删除，个人 Codex Home 永不进入删除集合。

认证模式由注册表明确记录：`ChatGptAccount`、`OpenAiApiKey`、`CustomResponses`。切换认证模式时会先校验新模式所需字段；切换到账号模式只在用户明确保存后移除旧 API Key 文件。

## Attach existing profile

“使用已有工作空间”只接受 `profiles/<directory-id>` 或其 `codex-home`，并要求存在有效的多开器 marker。接入事务完成以下操作：

- 校验路径、marker、配置与认证状态；
- 复用 marker 中的稳定 `ProfileId`，并建立唯一角标颜色；
- 原子更新 `profiles.json`。

旧 marker 缺少 `ProfileId` 时只升级该启动器元数据文件。该流程不创建新的 Profile 目录，也不复制或重写配置、认证、会话、SQLite、插件、Skills、Memories 和 Electron 数据。个人 Codex Home、外部目录以及任意祖先层级含重解析点的路径会被拒绝；外部资源迁移继续由配置中心的显式资源操作负责。

## Configuration center

配置中心管理工作空间能力、MCP、权限和快照。Skills、Memories 与全局规则支持显式双向操作；任何指向个人侧的写入都必须由用户选择并在界面确认。快照明确排除认证文件；恢复中途失败时会自动应用操作前安全快照，若自动回滚本身失败则同时保留两个异常与安全快照路径。

启动、删除、Profile 设置、配置中心和合并写入统一使用 `state/profile-operation.lock` 的独占跨进程文件锁，进程异常退出后由 Windows 自动释放。运行状态优先核对活进程；状态文件缺失或损坏时，从 `runtime-cache/versions/*/cache-manifest.json` 恢复 Profile 归属，未知缓存进程保守阻止变更。缓存复用要求缓存 App 与源 App 文件集合完全相同，非托管图标文件逐一执行 SHA-256 校验，托管图标再按 manifest 中的品牌哈希单独验证。

## Test boundary

测试通过 `LauncherPathOverrides` 把个人目录、运行目录和 Electron 数据全部指向单次测试的临时根。测试不读取或写入真实 `%LOCALAPPDATA%\CodexChannelLauncher` 和 `%USERPROFILE%\.codex`。
