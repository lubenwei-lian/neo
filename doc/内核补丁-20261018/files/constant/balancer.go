package constant

import "time"

// neo 补丁新增的 balancer 出站类型与常量（上游 v1.14.2 没有）。
// 这些字符串值必须与 SingBoxConfigBuilder 写入 JSON 的字面量完全一致：
//   strategy: roundRobin / leastPing / random
//   sticky_key: process / domain / dest_ip / dest_port / source_ip / none
const (
	TypeBalancer = "balancer"
)

const (
	BalancerStrategyRoundRobin = "roundRobin"
	BalancerStrategyLeastPing  = "leastPing"
	BalancerStrategyRandom     = "random"
)

const (
	BalancerStickyProcess  = "process"
	BalancerStickyDomain   = "domain"
	BalancerStickyDestIP   = "dest_ip"
	BalancerStickyDestPort = "dest_port"
	BalancerStickySourceIP = "source_ip"
	BalancerStickyNone     = "none"
)

const (
	BalancerDefaultPoolSize      = 3
	BalancerDefaultDialAttempts  = 3
	BalancerDefaultStickyTimeout = 30 * time.Minute
)
