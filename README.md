# neo

Windows 代理客户端。当前正式桌面界面采用原生 Windows 窗口 + WebView2，界面源码为 HTML / CSS / JavaScript，连接、系统代理、TUN、统计与更新服务由 C# / .NET 10 实现；工程保留 WinUI 3 页面和离线检查入口。内置 Xray 与 sing-box，并可按需下载 mihomo / Naive 辅助内核。

## 功能

- 订阅导入与节点管理：VMess / VLESS / Trojan / Shadowsocks / SSR / Hysteria2 / TUIC / AnyTLS / SOCKS5 / HTTP(S) / WireGuard / Naive HTTPS、QUIC；Clash 订阅与自定义 xray / sing-box 配置直通
- 支持粘贴节点／配置、文件导入和本地文件订阅，详见 [节点与订阅支持](docs/节点与订阅支持.md)
- 连接模式：系统代理、TUN 全局接管
- 规则分流：绕过局域网/大陆、广告拦截、自定义规则
- 节点测速（真实延迟 / TCP 握手）、故障转移、流量统计
- 内核检查与更新（SHA-256 校验、失败回滚）、可选 mihomo / naive 内核按需下载
- 托盘常驻、开机自启、安全模式

## 下载

见本仓库 [Releases](https://github.com/lubenwei-lian/neo/releases)（含 Windows 安装包与 Android APK）。

## 构建

环境：Windows、.NET SDK 10、Windows SDK 10.0.26100；Windows App SDK 和其它 NuGet 依赖自动还原。运行 HTML 界面需要 Microsoft Edge WebView2 Runtime。

```powershell
dotnet build src/Neo.App/Neo.App.csproj -c Release -p:Platform=x64
```

正式界面和动效已完整放在 `src/Neo.App/Assets/HtmlShell/`，构建直接使用这些文件，不需要本机的外部 HTML 原型目录。`tools/prepare-html-shell.py --source <原型目录>` 仅用于开发时重新绑定原型；不要为了普通构建运行它。

打包：`pwsh -NoProfile -File tools/release.ps1`。脚本依次发布、调用 Inno Setup、生成 SHA-256 并验证；只打印 GitHub 发布命令，不自动上传。需要 Inno Setup、Python、VC++ x64 CRT 和 `runtime/` 中的内核/规则资产；内核二进制不存入 Git。Release 发布启用 ReadyToRun 预编译，减少首次进入页面时 UI 线程的 JIT 开销。

`Program.cs` 在启动 WinUI 前分流看门狗、TUN 清理和日志导出命令；这些无界面模式不创建 XAML 应用。最小化/隐藏窗口时，自动测速会取消临时探针并保留待测节点，恢复后续测；代理内核与手动测速不受此可见性策略影响。

## 项目结构

| 目录 | 内容 |
|---|---|
| `src/Neo.App` | 原生窗口、WebView2 桥接、保留的 WinUI 页面与服务 |
| `src/Neo.App/Assets/HtmlShell` | 正式桌面 HTML / CSS / JavaScript 界面与动效 |
| `src/Neo.Core` | 内核配置生成、订阅解析、测速等 |
| `src/Neo.Cli` | 命令行工具 |
| `tools/` | 打包、启动器、分流数据生成 |
| `installer/` | Inno Setup 安装脚本 |
| `tests/` | 协议、会话、代理清理、更新与界面桥接回归 |

## 内核与许可

- **xray**：官方发行版（MPL-2.0），运行时从官方发布更新。
- **sing-box**：官方发行版（GPL-3.0），运行时从上游官方稳定发布更新。

0.2.9 发布说明与源码快照说明见 [docs/releases/0.2.9.md](docs/releases/0.2.9.md)。回归测试入口和边界见 [tests/README.md](tests/README.md)。本机数据、订阅缓存、构建输出和内部验收记录不进入源码仓库。

本仓库自有代码采用 MIT 许可证（见 [LICENSE](LICENSE)）；第三方组件与内核许可证见 [NOTICE](NOTICE)。
## 源码结构

- src/ : WinUI 3 主程序（C#）
- rust/ : Rust 核心（neo-core / neo-cli / neo-jni）
- installer/、tools/ : 打包与维护脚本


