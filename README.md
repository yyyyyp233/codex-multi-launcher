# Codex 多开器

[![build-test](https://github.com/kuroiii/codex-multi-launcher/actions/workflows/ci.yml/badge.svg?branch=msix-identity-launch)](https://github.com/kuroiii/codex-multi-launcher/actions/workflows/ci.yml?query=branch%3Amsix-identity-launch)
[![License: MIT](https://img.shields.io/badge/License-MIT-F58A32.svg)](LICENSE)

> **Unofficial community project.** A source-first Windows launcher that keeps the normal Codex App profile as the primary profile and can start any number of locally isolated profiles. Each managed profile can use a ChatGPT account, an OpenAI API key, or a third-party Responses-compatible provider. Requires Windows, the Codex Windows App, and .NET 10 SDK to build. It is not affiliated with or endorsed by OpenAI or Microsoft. This compatibility branch launches isolated profiles through the registered Windows MSIX/AppX identity while keeping separate Codex Home and Electron data; read the [compatibility and terms notice](docs/COMPATIBILITY.md) before use. No binaries are distributed in this preview.

> **Fork note:** this branch is maintained at `kuroiii/codex-multi-launcher` and is based on the original MIT-licensed project `yyyyyp233/codex-multi-launcher`. The upstream history and attribution are preserved.

Codex 多开器是一个 Windows 本机 GUI，用于明确选择：

- **个人空间**：通过原始 Store 注册入口启动，继续使用默认登录和 `%USERPROFILE%\.codex`；
- **隔离空间**：可任意创建，每个空间使用独立 Codex Home 与 Electron 用户数据；当前兼容分支通过 Windows 注册的 MSIX/AppX 身份启动官方 App，不复制或修改 Codex App 运行文件。认证可选 ChatGPT 账号、OpenAI API Key 或第三方 Responses 兼容 Provider。

隔离空间是可选的，个人空间始终是默认主入口。启动器不会为了切换空间而覆盖个人的全局 `config.toml` 或 `auth.json`。

![Codex 多开器界面预览](docs/images/main-window.svg)

## 重要声明

本项目是非官方实验性工具，不属于 OpenAI 或 Microsoft，也未获得任何赞助、认可或兼容保证。MIT 许可证只覆盖本仓库源码，不覆盖第三方 App、服务、商标或二进制文件。

工作空间启动依赖 Codex Windows App 当前实现中的 Electron `--user-data-dir` 入口、`CODEX_HOME` / `CODEX_SQLITE_HOME` 行为以及 Windows MSIX 激活接口；这些并不是本项目能够保证长期存在的兼容契约。当前分支不复制、修改或重新打包 Codex App 文件。OpenAI 的 [Terms of Use](https://openai.com/policies/terms-of-use/) 与 [OpenAI Design Guidelines](https://openai.com/brand/) 仍然适用；请在使用前自行确认适用条款、软件许可、组织政策和风险。

详细说明见 [NOTICE.md](NOTICE.md) 与 [兼容性文档](docs/COMPATIBILITY.md)。

## 当前发布范围

`1.5.1-msix-preview.1` 兼容分支仅发布源码。已在 OpenAI.Codex `26.924.2738.0`（Windows x64）上完成 Release 构建、61 项单元测试，以及个人空间 + 原隔离空间并行启动/重新识别的人工验收；这不代表未来 Codex 版本必然兼容：

- 不提交 `dist/`、`artifacts/`、EXE 或真实运行配置；
- 不创建 GitHub Release 或稳定标签；
- 不分发 OpenAI、Microsoft 或其他第三方二进制；
- 需要使用者在本机从源码构建并自行承担 Preview 风险。

## 安装要求

- Windows 10/11 x64；
- 当前用户已安装可被 Windows AppX 注册信息定位的 Codex Windows App；
- 从源码构建需要 [.NET SDK 10.0.300](https://dotnet.microsoft.com/download/dotnet/10.0) 或 `global.json` 允许的同补丁系列版本；
- 使用第三方 Provider 认证时，该 Provider 必须支持 OpenAI Responses API 线协议。

## 从源码运行

```powershell
git clone https://github.com/kuroiii/codex-multi-launcher.git
cd codex-multi-launcher
git checkout msix-identity-launch

dotnet restore CodexMultiLauncher.slnx
dotnet run --project CodexChannelLauncher.csproj -c Release
```
正常主窗口的关闭按钮与 `Alt+F4` 会把多开器隐藏到系统托盘，不结束后台进程。单击托盘图标或右键选择“显示主窗口”可恢复窗口；只有托盘右键菜单中的“退出多开器”才会彻底退出。Windows 注销或关机不会被该行为拦截。

验证源码：

```powershell
dotnet format CodexMultiLauncher.slnx --verify-no-changes --no-restore
dotnet build CodexMultiLauncher.slnx -c Release --no-restore
dotnet test CodexMultiLauncher.slnx -c Release --no-build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\audit-repository.ps1
```

仓库保留 `publish.ps1` 供本地实验构建使用，但当前公开版本不提供、上传或支持预编译 EXE。未来二进制发布需要另行完成签名、SmartScreen、SHA256、.NET License 与 ThirdPartyNotices 审核。

## 首次配置

首次打开多开器且尚无隔离空间时会显示配置向导；之后可随时通过“新增空间”继续创建，数量不受固定卡片限制。可以选择：

### 新建隔离空间

先选择认证方式：

- **ChatGPT 账号**：不创建 API Key 文件，首次启动后在该隔离 App 内独立登录；
- **OpenAI API Key**：使用 Codex 内置 OpenAI Provider，只填写模型、推理等级与 Key；
- **第三方 Responses Provider**：填写 Provider ID、名称、Base URL、模型、推理等级与 Key。

启动器界面统一使用黑橙视觉；每个新空间仍会分配并持久化主题色用于界面区分。MSIX 启动路径不再复制 App 文件，因此不会为官方 Codex 托盘图标生成运行副本角标。

- Base URL 必须为 HTTPS；只有 `localhost`、`127.0.0.1`、`::1` 等回环地址允许 HTTP；
- URL 内嵌用户名或密码、查询参数与片段会被拒绝；
- Provider ID 只允许英文字母、数字、短横线与下划线；
- API Key 使用密码框，不回显，只原子写入隔离 `auth.json`，不进入日志或快照。

生成的核心配置采用 `responses` wire API，并默认启用网络访问和响应不落库配置。具体 Provider、模型和端点完全由用户填写，仓库不内置组织通道。

### 使用已有工作空间

“使用已有工作空间”是原地接入，不是复制或新建。可选择：

- `%LOCALAPPDATA%\CodexChannelLauncher\profiles\<目录名>` 工作空间目录；
- 或其下的 `codex-home` 目录。

所选目录必须带有有效的多开器 marker。接入不移动、不复制工作空间，只向 `profiles.json` 增加注册；旧 marker 会原子升级并持久化稳定的 `ProfileId`，以便保留后再次接入时继续复用。`config.toml`、`auth.json`、任务会话、SQLite、插件、Skills、Memories 和同级 Electron 数据都保持原位置与内容。

个人 `%USERPROFILE%\.codex` 和 `profiles` 根目录之外的任意 Codex Home 会被拒绝绑定。外部配置需要创建新的隔离空间后再通过配置中心按资源迁移，避免把个人或未知目录误纳入工作空间生命周期。

### 兼容旧 profile

如果新版注册表不存在，多开器会一次性扫描 `profiles/*/codex-home` 中全部带有效旧 marker 的 profile，并将它们直接迁移到新注册表：不移动、不复制、不重写其 `config.toml` 或 `auth.json`；只会升级启动器 marker 以写入稳定 `ProfileId`。旧单例注册文件成功迁移后会被移除，后续运行只认新结构。注册表已经存在时，可通过“使用已有工作空间”重新接入保留在本机的未注册空间。

## 隔离边界

默认运行数据位于：

```text
%LOCALAPPDATA%\CodexChannelLauncher\
├─ state\profiles.json
├─ state\profile-operation.lock
├─ profiles\<id>\codex-home\
├─ profiles\<id>\electron\
├─ runtime-cache\              # 仅兼容旧版本遗留数据；当前 MSIX 路径不创建运行副本
├─ snapshots\<id>\
├─ merge-bases\<id>\
└─ logs\
```

个人入口继续使用默认 Codex Home 与默认 Electron 目录。隔离空间启动时，多开器在受锁保护的短事务中临时设置当前 Windows 用户的 `CODEX_HOME` 与 `CODEX_SQLITE_HOME`，广播环境变化，再通过 `IApplicationActivationManager` 以官方 MSIX 身份激活 Codex，并用 `--user-data-dir` 指向该空间的 Electron 目录；确认目标 PID、包身份、命令行目录以及两侧出现本次新写入后，会恢复原用户环境值。启动期间不要从开始菜单、任务栏、另一个终端或旧版多开器额外启动 Codex/Codex CLI。

需要注意：账号和本地 App 状态可以隔离，但 Windows 会话、工作目录、Chrome、前台桌面、Unity、MCP 后端和其他外部资源仍可能共享。即使两边都具备 Chrome 或电脑操作能力，也应避免同时争用同一资源。

## 配置中心

任一隔离空间卡片的设置按钮会打开只针对该空间的配置中心：

- **Skills**：个人与工作空间双向 Diff、整包采用、启停；`.system` 与插件自带 Skills 不参与；
- **全局规则**：`AGENTS.md` 与 `AGENTS.override.md` 双向逐文件 Diff / Merge；
- **Memories**：轻量文件概览与双向合并，不加载超大逐行 Diff；
- **Chrome / 电脑操作**：工作空间插件安装状态、开关和 Windows 应用允许列表；
- **MCP**：对比两侧列表，迁移不含静态凭据的配置；引号键、`env`、`http_headers` 和疑似密钥字段同样会被阻止；
- **权限**：工作空间 approval、sandbox、network 和 Windows sandbox 配置；
- **快照 / 恢复**：变更前自动快照并支持手动恢复，始终排除 `auth.json`；恢复中途失败会立即使用安全快照自动回滚；
- **删除工作空间**：默认只移除多开器入口并保留本地数据；只有显式勾选“同时删除本地内容”才清理该空间的 Codex Home、Electron 数据、快照、合并基线以及旧版本遗留的专属运行缓存（如存在）。

写入工作空间配置前需要退出工作空间 App。双向合并会修改箭头指向的目标侧，因此要求两个 App 都退出；任何指向个人侧的写入都由用户显式选择并再次确认。

所有启动、删除、Profile 设置、配置中心与合并写入共用同一个跨进程操作锁。当前 MSIX 路径还使用单独的用户环境事务锁与恢复 journal。运行实例的归属优先通过 PID、进程创建时间、可执行路径、MSIX 包身份与命令行中的绝对 `--user-data-dir` 核对，并要求该 Electron 目录与已注册 Profile 的 marker/注册表一致；无法可信归属的 Codex 进程会被保守视为未解析实例并阻止受管变更。

## 本机数据与隐私

启动器不包含遥测、分析 SDK、崩溃上报或自动更新检查。它会读取当前用户的 AppX 注册信息、已安装 App 文件，以及用户明确选择对比或迁移的 Codex 配置。完整目录与网络行为见 [PRIVACY.md](PRIVACY.md)。

不要在 Issue、PR 或截图中上传：

- `auth.json`、API Key、Token、Cookie 或环境变量；
- 完整 Codex Home、任务会话、SQLite 或浏览器目录；
- 未脱敏完整日志、用户名、本机绝对路径或组织内部域名。

安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 卸载

1. 完全退出个人与工作空间 Codex 实例以及多开器；
2. 删除本地源码或自行生成的构建输出；
3. 删除 `%LOCALAPPDATA%\CodexChannelLauncher`，移除工作空间、Electron 数据、快照、合并基线、状态、日志以及可能存在的旧版 `runtime-cache`。

不要删除 `%USERPROFILE%\.codex`，除非你明确希望移除个人 Codex 配置和数据。

## 已知限制

- Codex App 更新可能改变 MSIX 激活、Electron 参数或 `CODEX_HOME` 行为；已通过 OpenAI.Codex `26.924.2738.0` 验证，但无法保证未来版本兼容；
- 隔离启动会在很短的受锁时间窗内修改当前 Windows 用户的 `CODEX_HOME` / `CODEX_SQLITE_HOME`，随后恢复。其他不受本启动器锁控制的 Codex/Codex CLI 若恰在该时间窗启动，可能继承临时值；
- 异常终止时会保留环境恢复 journal，并在后续启动尝试恢复；若检测到外部同时修改了这些环境变量，会保留外部修改并停止自动覆盖；
- MSIX 激活可能把请求交给已有实例。新隔离空间若没有获得新的、可验证 PID，会被拒绝登记，不会静默复用；
- 当前 MSIX 路径不再支持通过复制运行文件实现的托盘图标角标；
- 当前没有自动更新、安装器、代码签名或稳定二进制发行；
- 本项目不绕过 Provider 认证、服务限制、组织策略或第三方条款。
## Contributing

贡献前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。架构和测试边界见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

Copyright © 2026 yyyyyp233. Released under the [MIT License](LICENSE).
