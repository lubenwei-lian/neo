# neo

Windows 代理客户端（WinUI 3）。内置 xray 与 sing-box 双内核：系统代理走 xray，TUN 走 sing-box；支持订阅导入、规则分流、故障转移与内核更新。

## 功能

- 订阅导入与节点管理：VMess / VLESS / Trojan / Shadowsocks / Hysteria2 / TUIC / AnyTLS / SOCKS5 / WireGuard；Clash 订阅与自定义 xray / sing-box 配置直通
- 连接模式：系统代理、TUN 全局接管
- 规则分流：绕过局域网/大陆、广告拦截、自定义规则
- 节点测速（真实延迟 / TCP 握手）、故障转移、流量统计
- 内核检查与更新（SHA-256 校验、失败回滚）、可选 mihomo / naive 内核按需下载
- 托盘常驻、开机自启、安全模式

## 下载

见本仓库 [Releases](https://github.com/lubenwei-lian/neo/releases)。

## 构建

环境：.NET SDK 10、Windows App SDK（NuGet 自动获取）。

```powershell
dotnet build src/Neo.App/Neo.App.csproj -c Release -p:Platform=x64
```

打包：`tools\publish.ps1` → `iscc installer\neo.iss`（顺序不能反，缺资产会直接失败）。

## 项目结构

| 目录 | 内容 |
|---|---|
| `src/Neo.App` | WinUI 3 界面与服务 |
| `src/Neo.Core` | 内核配置生成、订阅解析、测速等 |
| `src/Neo.Cli` | 命令行工具 |
| `tools/` | 打包、启动器、分流数据生成 |
| `installer/` | Inno Setup 安装脚本 |

## 内核与许可

- **xray**：官方发行版（MPL-2.0），运行时从官方发布更新。
- **sing-box**：官方发行版（GPL-3.0），运行时从本项目发布通道更新。

本仓库自有代码采用 MIT 许可证（见 [LICENSE](LICENSE)）；第三方组件与内核许可证见 [NOTICE](NOTICE)。
## 源码结构

- src/ : WinUI 3 主程序（C#）
- rust/ : Rust 核心（neo-core / neo-cli / neo-jni）
- installer/、tools/ : 打包与维护脚本

