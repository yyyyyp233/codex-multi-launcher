# Compatibility and risk notice

> 本项目是非官方实验性工具。它没有得到 OpenAI 或 Microsoft 的认可、支持或兼容承诺。

## Unstable integration surface

当前 `msix-identity-launch` 分支依赖以下 Windows/Codex 行为：

- 已注册的 `OpenAI.Codex` MSIX/AppX 应用可通过 `IApplicationActivationManager` 激活；
- Codex Desktop 接受独立的 Electron `--user-data-dir`；
- Codex Desktop / app-server 在启动时读取 `CODEX_HOME` 与 `CODEX_SQLITE_HOME`；
- 启动后的进程可以通过包身份、PID/创建时间和命令行进行保守归属判断。

这些都不是本项目能够保证长期存在的公开兼容契约。该分支已在 OpenAI.Codex `26.924.2738.0`（Windows x64）上完成构建、测试和人工双实例验收，但未来 Codex 更新仍可能需要重新适配。

当前 MSIX 路径不复制、不修改、不重新打包 Codex App 文件；旧版 `runtime-cache` 仅作为历史兼容数据保留。

## User-environment launch window

隔离 Profile 启动需要在短时间内修改当前 Windows 用户的 `CODEX_HOME` 和 `CODEX_SQLITE_HOME`，通过 `WM_SETTINGCHANGE` 广播，再进行 MSIX 激活；确认目标实例后立即恢复原值。

多开器通过文件锁协调自身实例，并使用恢复 journal 处理异常中断。但它无法阻止用户从其他终端、开始菜单、任务栏或旧版启动器在同一时间窗启动另一个 Codex/Codex CLI。启动进度显示环境切换阶段时，请不要通过其他入口额外启动 Codex。

如果恢复时发现这两个用户环境变量被其他程序/用户同时改成了第三个值，启动器会保留外部修改并停止自动覆盖，而不是强行恢复。

## Terms and branding

OpenAI 的 [Terms of Use](https://openai.com/policies/terms-of-use/) 与 [OpenAI Design Guidelines](https://openai.com/brand/) 仍然适用。本项目不声称得到 OpenAI 或 Microsoft 的官方支持，也不应被用于暗示官方关联或背书。

## Source-only preview

`1.5.1-msix-preview.1` 当前只提供源码，不提供官方签名安装包或稳定二进制发行。自行发布二进制仍需要考虑代码签名、SmartScreen、SHA256、.NET 许可证与 ThirdPartyNotices。

## Antivirus and endpoint security

Codex App、插件、MCP、Shell 命令、WMI 进程查询和电脑操作能力可能触发安全软件规则。告警应按具体哈希、签名、命令行和进程树调查。不要因为使用了 Codex 就把检测一概视为误报，也不要创建宽泛目录白名单。