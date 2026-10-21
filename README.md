# neo

Windows 上的代理客户端：主内核 xray，TUN 用自编译 sing-box，支持多节点并发加速。

## 功能

- 订阅导入与节点管理（VLESS / VMess / Trojan / Shadowsocks / Hysteria2 / TUIC）
- 规则分流、全局代理、TUN 模式（全局接管）
- 多节点并发：同一地区节点按实测吞吐分配流量（快节点多吃、慢节点也出力）
- 实时流量图、连接详情、自动故障转移、内核一键更新（带 SHA-256 校验）

## 下载

去 [Releases](https://github.com/lubenwei-lian/neo-desktop-release/releases) 下载 `neo-setup-*.exe`。

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
| `tools/` | 打包、启动器、内核构建脚本 |
| `installer/` | Inno Setup 安装脚本 |
| `doc/内核补丁-20261018/` | 自编译 sing-box 的补丁与构建说明 |

## 内核

- **xray**：官方发布版（MPL-2.0），运行时从官方 release 更新。
- **sing-box**：自编译版（GPL-3.0），在官方 1.14.2 基础上加入 balancer 出站，用于 TUN 多节点并发。
  补丁与构建方式见 `doc/内核补丁-20261018/`，构建脚本 `tools/build-singbox.ps1`。

## 许可

本仓库自有代码采用 MIT 许可证（见 [LICENSE](LICENSE)）。
第三方组件与内核的许可证见 [NOTICE](NOTICE)。
