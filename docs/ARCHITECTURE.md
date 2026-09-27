# Architecture

## Goal

Codex 多开器在同一 Windows 用户会话中保留一个个人主入口，并允许任意数量的隔离 Profile。个人实例继续使用默认 Codex Home；每个隔离实例使用自己的 Codex Home 与 Electron 用户数据。

当前 `msix-identity-launch` 分支不再复制 Codex App 运行文件。个人和隔离实例都通过 Windows 注册的 MSIX/AppX 应用身份启动。

```text
Codex 多开器
├─ 个人入口
│  └─ IApplicationActivationManager → OpenAI.Codex MSIX → 默认 Codex Home / Electron
└─ 隔离 Profile 入口（0..N）
   ├─ 注册表：state\profiles.json
   ├─ Codex Home：profiles\<directory-id>\codex-home
   ├─ Electron 数据：profiles\<directory-id>\electron
   └─ 启动事务：
      ├─ 临时 HKCU CODEX_HOME / CODEX_SQLITE_HOME
      ├─ WM_SETTINGCHANGE
      ├─ MSIX activation + --user-data-dir=<profile electron>
      ├─ PID / package / command-line / fresh-activity verification
      └─ 恢复原用户环境
```

所有启动器数据默认位于 `%LOCALAPPDATA%\CodexChannelLauncher`。旧版 `runtime-cache` 目录可能仍存在，但当前 MSIX 路径不创建或执行其中的 App 副本。

正常主窗口采用托盘驻留生命周期：标题栏关闭与 `Alt+F4` 取消窗口关闭并隐藏主窗口，托盘左键或“显示主窗口”恢复；托盘“退出多开器”、预览渲染完成以及 Windows 会话结束才允许真实关闭。

## MSIX launch transaction

启动受两个层级的锁保护：

1. 原有 `state/profile-operation.lock` 协调启动、删除、Profile 设置、配置中心和合并写入；
2. MSIX 用户环境锁协调所有 patched launcher 实例的临时用户环境事务。

隔离启动会保存 `CODEX_HOME` 与 `CODEX_SQLITE_HOME` 的原始 registry 值和值类型，写入恢复 journal，然后把两者临时指向目标 Profile Codex Home。广播环境变化后，启动器通过 `IApplicationActivationManager` 激活 `OpenAI.Codex_2p2nqsd0c76g0!App`，并传入目标 Electron 目录的绝对 `--user-data-dir`。

一个隔离启动只有同时满足以下条件才会登记成功：

- Windows 返回的 PID 在启动前不存在；
- PID 对应仍存活的 Codex 主进程；
- `GetPackageFullName` 与当前已安装 Codex 包一致；
- 命令行中只有一个可解析的绝对 `--user-data-dir`，并与目标 Profile Electron 目录一致；
- 目标 Codex Home 与 Electron 目录都出现本次启动后的新文件活动。

成功或失败退出事务时都会尝试恢复原用户环境。若当前值已被外部修改为第三个值，compare-before-write 策略会保留外部值并留下 journal，不进行破坏性覆盖。

## Process ownership

运行实例不再以“可执行文件是否位于 runtime-cache”作为主要身份依据。

`ProcessInventory` 获取 ChatGPT/Codex 根进程后，结合：

- PID 与父子进程关系；
- 进程创建时间和可执行路径；
- MSIX 包身份；
- WMI 可读的进程命令行；
- 命令行中的 `--user-data-dir`；
- `profiles.json` 与 Profile marker 的一致性；

对个人、受管 Profile 和未解析进程做保守分类。

无法读取命令行、目录不在注册范围内或 marker/注册信息不一致的实例不会被静默当作个人实例。受管变更可能因此被阻止，直到未知实例退出。

## Profile lifecycle

运行状态分为：

- `NotConfigured`：没有注册；
- `Configured`：注册、marker、`config.toml` 以及所选认证方式所需的认证状态有效；ChatGPT 账号模式允许在首次启动后登录；
- `Invalid`：注册或配置存在但无法安全解析。

新版注册表缺失时，启动器会将发现的全部有效旧 marker 一次性登记到 `profiles.json`，为 marker 原子补写稳定 `ProfileId`，并消费旧单例注册文件。它不会移动 Profile，也不会重写配置或认证；之后运行只读取新注册表。

新建隔离空间先在运行根目录下的 staging 目录完成配置、认证和 marker，再以目录移动提交。编辑已有空间时，配置、认证、marker 与注册表作为一个带逆序回滚的文件事务提交，避免部分成功。API Key 不进入错误信息、日志、注册、marker 或快照。

删除工作空间默认只从 `profiles.json` 注销，保留本地内容。用户显式选择同时删除本地内容时，Profile、快照、合并基线以及旧版本遗留运行缓存（如存在）会按安全删除事务处理。运行中的 Profile 一律拒绝删除，个人 Codex Home 永不进入删除集合。

认证模式由注册表明确记录：`ChatGptAccount`、`OpenAiApiKey`、`CustomResponses`。切换认证模式时会先校验新模式所需字段；切换到账号模式只在用户明确保存后移除旧 API Key 文件。

## Attach existing profile

“使用已有工作空间”只接受 `profiles/<directory-id>` 或其 `codex-home`，并要求存在有效的多开器 marker。接入事务：

- 校验路径、marker、配置与认证状态；
- 复用 marker 中稳定的 `ProfileId`；
- 原子更新 `profiles.json`。

旧 marker 缺少 `ProfileId` 时只升级该启动器元数据文件。该流程不创建新的 Profile 目录，也不复制或重写配置、认证、会话、SQLite、插件、Skills、Memories 和 Electron 数据。个人 Codex Home、外部目录以及任意祖先层级含重解析点的路径会被拒绝。

## Configuration center

配置中心管理工作空间能力、MCP、权限和快照。Skills、Memories 与全局规则支持显式双向操作；任何指向个人侧的写入都必须由用户选择并在界面确认。快照明确排除认证文件；恢复中途失败时会自动应用操作前安全快照。

## Recovery

用户环境恢复 journal 位于启动器运行根目录的 MSIX launch state 下。正常异常路径通过 `using`/finally 恢复；强制终止或断电后，下次启动会尝试恢复兼容的未完成事务。

还提供独立恢复脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Restore-MsixEnvironment.ps1
```

它只处理本分支定义的两个环境变量恢复记录，并在发现外部修改冲突时停止。

## Test boundary

测试通过 `LauncherPathOverrides` 把个人目录、运行目录和 Electron 数据指向临时根。MSIX 回归单元测试覆盖命令行 quoting/解析、旧文件不能伪造 readiness、环境恢复决策、未解析进程分类以及注册目录/marker 一致性；它们不会修改真实 HKCU 用户环境，也不会启动真实 Codex。

2026-09-27 的人工验收在 Windows x64 / OpenAI.Codex `26.924.2738.0` 上完成：Release 构建成功，61 项测试通过，个人 + 已有隔离 Profile 并行运行、重新识别、关闭和重新启动均通过。