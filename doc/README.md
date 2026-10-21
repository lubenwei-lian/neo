# 内核补丁（sing-box 1.14.2-noGate）

本目录保存 TUN 多节点并发所需的自编译 sing-box 补丁，供 GPL 对应源码与可复现构建使用。

- `files/`：相对上游 v1.14.2 需要新增/覆盖的文件（`protocol/group/balancer.go`、`option/balancer.go`、`constant/balancer.go`）
- 构建脚本还会在 `include/registry.go` 中注册 `group.RegisterBalancer(registry)`
- 一键构建：`pwsh -NoProfile -File tools/build-singbox.ps1`（本机 go 版本低于 go.mod 要求时加 `-AllowToolchainDownload`）
- 产物：`dist/kernel/neo-singbox-<版本>-windows-amd64.zip` + `.sha256`

补丁内容：基于上游 balancer 快照，移除了「比最快者慢 3 倍以上的成员被剔除」的质量门槛，
让慢节点也能参与分摊；应用层通过份额（pool_size / 权重）实现「快节点多分、慢节点少分」。
