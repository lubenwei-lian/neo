# 长稳与多内核回归

这三组测试面向 2026-11-07 的长稳和深层耦合修复。需要 Windows、.NET 10 SDK 和 Python；不调用部署脚本，不修改安装版或系统代理。

## 逻辑与 TCP 中继

```powershell
dotnet run --project tests/Neo.Regression/Neo.Regression.csproj --verbosity quiet
```

当前 66 项断言覆盖：独立代理修复开关、外部代理保护、取消/断开、自动切换、卡死恢复、空闲 API 判活、并发恢复、订阅刷新互斥、跨天用量、重复错误日志、能力映射、采样代号隔离、慢响应半关闭和中继停止回收，以及 25 项网络可用检测检查。

网络检查直接链接完整生产 `NetworkAvailabilityProbe` 和 `NetworkHealthService`，以注入的 HTTP 传输边界验证 HTTPS 204、登录页/重定向拒绝、多服务商回退、TLS 错误分类、单目标/整轮超时、取消、HTTP/SOCKS 部分失败、连接池对象复用、禁用自动切换时仍检测、真实系统代理配置判定、TUN 不依赖系统代理、连续失败告警/恢复、过期会话隔离、排队检查失效、通知去重、订阅者隔离和首次立即检测。手动检测只更新结果，不累计自动切换决策。测试不访问公网、不改本机代理，也不代表真实 TUN 网卡故障注入或所有网站可用；Windows UI 和真实公网请求另作安装验收。

`prepare.py` 在构建时原样提取 `ConnectionService` 的三个策略方法及 `AppState` 的两个刷新方法。`PolicyHarness.txt` 和 `SubscriptionHarness.txt` 替换 OS/连接/网络边界；看门狗调度间隔缩短为 5ms，恢复等待与自检等待保持生产值。其他服务文件直接链接生产源码。它验证策略逻辑，不冒充完整 WinUI、注册表或真实故障注入测试。

结果写入 `tests/Neo.Regression/results.json`，方法摘要写入 `source-methods.json`；生成文件、构建目录已忽略。

## 已安装内核与真实回环转发

```powershell
dotnet run --project tests/Neo.KernelChecks/Neo.KernelChecks.csproj -p:Platform=x64 -- `
  'E:/Program Files/neo/app/runtime' `
  'E:/桌面/codex工作区【重要】/neo-fixes-20261107/kernel-checks'
```

第二个路径必须是隔离输出目录。测试只读取 runtime 中的内核/规则资产，生成合成节点配置；独立内核仅监听临时回环端口，退出时只回收测试自己创建的进程。

当前 36 项覆盖：protobuf 边界、gRPC 与 CLI 等值/不重置计数、空闲 API 响应、失败回退冷却、双槽位配置、TUN 配置检查、控制器端口并发隔离、Xray TLS 版本兼容、两内核 SOCKS/HTTP 实际 Trojan/TLS 转发、跨内核中继切换保留旧流、UDP 关联跨切换保持及控制通道关闭回收，以及每种真实内核各五次启动就绪测量。另含逐站检测的目标清单、状态码分类、路由 protobuf、真实 Xray TestRoute 的直连/代理/拦截/无匹配默认出口、误导性 tag 与真实协议分类、Clash 源端口和目标域名关联，以及真实 sing-box 连接链路观测。路由 API 测试不请求公网目标。

TLS 服务器、HTTP 源站和 SOCKS UDP 后端都由测试在回环地址创建；证书不加入系统信任库。测试中的密码、UUID、密钥均为合成值。TUN 只调用 `check`，不会创建虚拟网卡。跨内核测试操作实际 `PortRelay` 与独立内核，不操纵正在使用的 WinUI 会话。

`kernel-check-results.json` 包含计数、边界说明和同一独立 Xray 的顺序取数对照；该短测衡量统计传输开销，不衡量代理吞吐或长期泄漏。

## 深层耦合与生命周期

```powershell
dotnet run --project tests/Neo.DeepRegression/Neo.DeepRegression.csproj --verbosity quiet -- `
  'E:/Program Files/neo/app/runtime/xray.exe'
```

当前 83 项断言覆盖：过期自动决策、TUN 下同一决策的候选重试、切换提交与断开竞争、会话状态写入顺序、配置所有权、TLS 配置指纹与显式授权、规则/直连预检拒绝、保存与运行端口解耦、订阅并发合并、代理租约和部分写入恢复、退役内核用量、TUN 生命周期屏障、退出等待及吞吐读取时限。退出清理另覆盖注册表与 WinINet 单边失步、外部代理/PAC 保护、全部端点存活检查、IPv6-only 监听保护与死端点回收、旧看门狗恢复重试。完整连接方法另覆盖成功提交、预检/代理失败清理、自己的失败后重试、断开后续体、残留 TUN 拒绝、就绪握手期间取消/断开，以及回退切换收尾不覆盖后继连接状态。

工程直接引用当前 `Neo.Core` 项目，链接完整生产服务文件，并原样提取 `ConnectionService` 的 16 个方法，包含完整 `ConnectSessionAsync`。连接创建的进程、中继、UI、探测、内核校验和计数端点为受控边界，完整连接测试使用合成普通节点，不代表所有协议或全 WinUI 集成测试；故障转移中的部分连接策略仍使用替身。完整系统代理源码通过内存注册表别名运行，保留真实 Notify，只有 WinINet 配置读写和其他进程的存活查询为受控替身；补测原生端点/PAC/绕过规则失步、其他代理和后继会话保护、跨会话投递失效、恢复重试及快照保留。看门狗三个生命周期方法原样提取，进程存活、清理动作和 TUN 操作为受控替身，不执行真实终止或网卡删除。真实 Xray 另外执行合成配置预检。吞吐方法只替换目标 URL，通过本地 SOCKS 停滞响应器验证超时和取消。TUN 测试验证屏障和生成的清理计划，不执行网卡删除。

`acceptance-results.json` 记录结果；`source-binding.json` 绑定当前源码及提取方法摘要；生成文件和隔离状态均忽略。`tun-cleanup-plan.ps1` 只供 PowerShell Parser 语法检查，不应执行。

## 性能策略验收与回环基准

```powershell
dotnet run --project tests/Neo.PerformanceChecks/Neo.PerformanceChecks.csproj -c Release -- `
  'E:/桌面/codex工作区【重要】/neo-perf-20261107/performance-checks'
dotnet run --project tests/Neo.Performance/Neo.Performance.csproj -c Release -- `
  'E:/桌面/codex工作区【重要】/neo-perf-20261107/benchmark-new-run' optimized
```

输出目录应每轮更换，避免把上轮合成日志计入本轮。43 项性能策略检查直接引用生产的就绪检测、质量历史、批量写入器，链接后台测速、延迟缓存、日志、中继和实际测速调度器；只有节点网络请求、UI 和统计采样被受控替身替换。覆盖握手/端口/取消/过期会话、双向随机大流量与半关闭、质量证据过期/容量/抖动/错误来源、日志排队/背压/刷盘/故障恢复，以及后台优先级、缓存、并发、重试、忙时暂停与退出取消。额外验证最小化后停止在途自动探针、不把暂停记成节点失败、恢复后续测同一节点、可见性代际与退出取消独立、手动测速不受影响，以及版本展示仅读缓存、版本子进程双管道并发排空、硬超时回收、并发缓存与更新失效；版本探针使用复制的合成测试 apphost，不执行安装版内核。

基准记录 128 MiB 批量传输、短连接、分配量、Gen0、进程 CPU 时间、日志提交和排空时间。`AppServiceSource` 属性可以指向保存的旧服务源码目录构建基线。可选 `BaselineRelaySource` 指向仅更改命名空间的旧中继副本，使新旧实现在同一进程、同一服务器和交替顺序下比较；这是可复核的源码适配，不能替换成重新编写的基线。基准只用回环端口及合成日志，不代表公网速度、端到端网页耗时或长时间运行结果。

## 第二轮刷新开销检查

```powershell
dotnet run --project tests/Neo.RefreshChecks/Neo.RefreshChecks.csproj -c Release -- '你的隔离输出目录'
```

26 项检查覆盖流量图像的像素、DPI、档位、空格、缓冲复用、内存上限与释放恢复、日志突发/并发/清空/释放/投递失败，以及内存样本的会话、PID、断开和采样代号隔离，并保持 Xray 统计过滤与空闲判活语义。测试直接链接完整生产采样器和统计服务；UI 方法从当前源码原样提取，以计数控件和可控调度队列执行，不启动真实 WinUI 窗口。源码摘要保存在输出目录的 `source-binding.json`。

可选 `-p:BaselineRoot='修改前源码快照根目录'` 启用同进程对照；快照须保留 `src/Neo.App/...` 路径。旧代码只修改类名并使用相同边界，不重写旧算法。`benchmarks.json` 记录三轮计数、耗时和分配；不传基线时只执行正确性检查。界面属性写入/调度计数不等于真实渲染耗时，合成统计端点也不代表公网速度。

## 逐站详情生命周期

```powershell
dotnet run --project tests/Neo.NetworkDiagnosticsChecks/Neo.NetworkDiagnosticsChecks.csproj -c Release -- '你的隔离输出目录'
```

20 项检查直接链接完整生产 `NetworkDiagnosticsService`，以合成连接会话和传输边界验证八站完整性及顺序、最多三路并发、重复点击合并、主动取消、断开清空、后继会话保护、订阅者异常隔离、坏配置恢复及断开态不发请求。复测检查使用生产协调器及真实回环 HTTP/HTTPS：最多三次、共用总时限、取消后不复测、首次失败证据保留、复测恢复单列计数、不重试 HTTP 限制/错误/错误 204、TLS EOF 使用新连接且不更换域名、自签证书仍被系统拒绝、只读响应头。测试不改变系统代理或证书信任、不启动 WinUI、不发公网请求。真实网站响应与详情页入口由安装版另行验收。

Xray 详情的出口证据为当前运行内核 `TestRoute` 的规则判定，取运行配置的入站标签、TCP/TLS 嗅探状态及该检测请求的源端口；无匹配只有确认返回 `common.ErrNoClue` 后才取该配置首个默认出口。它是规则判定，不冒充连接抓包。sing-box/mihomo 使用 `/connections` 中源端口及目标域名匹配的实际链路，未匹配时显示未确认。不会按国内/国外或出口 tag 名称直接预填直连/代理，不为检测开启全局 access 日志。

## 应用构建

### 原生首页展示语义与曲线

```powershell
dotnet run --project tests/Neo.PresentationChecks/Neo.PresentationChecks.csproj -c Release
# 可附最终发布的 neo.dll，额外验证正式二进制不能开启离线验收入口：
dotnet run --project tests/Neo.PresentationChecks/Neo.PresentationChecks.csproj -c Release -- 'dist/publish/app/neo.dll'
```

17 项检查直接链接生产的 `ConnectivityDisplay` 和 `TrafficCurveMath`，覆盖旧会话、检测中、取消、未知和受限的百分比语义，以及 10000 组随机区间的保形插值、端点和零流量。附发布程序集时为 18 项，检查 `NativeUiReview.Enabled` 的最终 IL 恒返回 false。这些检查不启动 WinUI、不请求公网、不改系统代理。

首页已使用原生 PathGeometry 曲线。前文 RefreshChecks 中的位图检查仍验证保留的 TrafficChartRaster 组件，不代表新版首页渲染成本；日志、采样与内存 getter 检查仍适用。原生窗口布局、原型对照和合成数据的验收证据见 `planning/2026-11-14-native-ui/验收与交接.md`。

离线窗口仅在显式 `-p:NeoUiReview=true` 编译并传入 `--ui-review` 时启用，使用独立临时数据目录。正式 publish 强制关闭编译开关；正式二进制收到该参数直接返回 64，不回落至生产启动。禁止借旧的 `--takeover` 脚本启动验收实例。

```powershell
dotnet build src/Neo.App/Neo.App.csproj -c Release -p:Platform=x64 `
  -p:OutDir='E:/桌面/codex工作区【重要】/neo-fixes-20261107/build/'
```

构建通过不等于已部署。替换安装版、重启长期运行会话、真实 TUN 接管及修复版本的长时间运行，需要单独安排部署和验收。
