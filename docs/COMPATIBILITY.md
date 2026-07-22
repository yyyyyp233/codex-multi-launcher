# Compatibility and risk notice

> 本项目是非官方实验性工具。它没有得到 OpenAI 或 Microsoft 的认可、支持或兼容承诺。

## Unstable integration surface

工作空间实例依赖 Codex Windows App 当前实现中的独立 Electron 用户数据入口，并为隔离启动准备本机运行副本。这些都不是本项目能够保证长期存在的公开兼容契约。

每次启动工作空间前，多开器会重新定位当前用户安装的 App 包并验证所需入口。检测到 Store App 更新时，会为新版本准备新的运行副本；若入口消失或结构不兼容，会拒绝启动工作空间，避免回退到个人界面目录。

个人入口始终通过原始 Store 注册入口启动。工作空间失败不应改变个人 Codex Home，但两个实例仍共享 Windows 会话、Chrome、前台桌面、工作目录和其他外部资源；不要假设这些资源已隔离。

## Terms and branding

OpenAI 的 [Terms of Use](https://openai.com/policies/terms-of-use/) 包含对修改、复制和分发服务以及逆向工程底层组件的限制。本项目的运行副本机制可能带来条款、许可或组织政策风险，使用者必须自行评估并承担责任。

[OpenAI Design Guidelines](https://openai.com/brand/) 要求第三方不得暗示官方关联或背书。本项目不使用 OpenAI 官方产品图标作为自身品牌，也不声称得到任何官方支持。

## Source-only preview

`1.5.0-preview.1` 首次公开版本只提供源码，不提供 EXE、安装包、GitHub Release 或第三方 App 文件。未来若发布自包含 .NET 二进制，还需要单独处理代码签名、SmartScreen、SHA256、.NET 许可证和 ThirdPartyNotices；这不属于当前版本范围。

## Antivirus

运行副本、插件、MCP、Shell 命令和电脑操作能力可能触发安全软件的行为规则。告警应按文件哈希、数字签名、检测名称、命令行和进程树逐项调查。不要因为使用了 Codex 就把检测一概视为误报，也不要创建宽泛目录白名单。
