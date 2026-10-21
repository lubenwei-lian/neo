package option

import "github.com/sagernet/sing/common/json/badoption"

// BalancerOutboundOptions 是 neo 补丁新增的 balancer 出站配置项（上游 v1.14.2 没有）。
// 字段与 protocol/group/balancer.go 的读取一一对应；漏一个字段就会在加载配置时
// 被 badjson 当作未知字段拒绝（或默认值不对）。
type BalancerOutboundOptions struct {
	Outbounds                 []string           `json:"outbounds" reference:"outbound"`
	URL                       string             `json:"url,omitempty"`
	Interval                  badoption.Duration `json:"interval,omitempty"`
	IdleTimeout               badoption.Duration `json:"idle_timeout,omitempty"`
	Strategy                  string             `json:"strategy,omitempty"`
	PoolSize                  int                `json:"pool_size,omitempty"`
	DialAttempts              int                `json:"dial_attempts,omitempty"`
	StickyKey                 []string           `json:"sticky_key,omitempty"`
	StickyProcesses           []string           `json:"sticky_processes,omitempty"`
	StickyTimeout             badoption.Duration `json:"sticky_timeout,omitempty"`
	InterruptExistConnections bool               `json:"interrupt_exist_connections,omitempty"`
}
