
//! 路由/分流规则的纯数据模型（骨架）。
//!
//! 目标：把 XrayConfigBuilder / SingBoxConfigBuilder 里的「分流意图」抽成与内核无关的模型，
//!      后续由各内核的 config builder 负责序列化。本模块只放数据与判定逻辑，不生成 JSON 配置。

use serde_json::{json, Map, Value};
use std::fmt;

/// 出站决策。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Outbound {
    /// 走代理
    Proxy,
    /// 直连（默认）
    #[default]
    Direct,
    /// 拒绝
    Block,
    /// 具名出站 / 策略组 tag（sing-box 的 selector / urltest / endpoint 都按 tag 引用）。
    ///
    /// 用 `&'static str` 而不是 String：组名与固定 tag 都是编译期常量，
    /// 这样 `Outbound` 仍可保持 Copy，不会波及现有调用点。
    Named(&'static str),
}

impl Outbound {
    pub fn as_str(self) -> &'static str {
        match self {
            Outbound::Proxy => "proxy",
            Outbound::Direct => "direct",
            Outbound::Block => "block",
            Outbound::Named(tag) => tag,
        }
    }
}

impl fmt::Display for Outbound {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// 单条分流规则（域名为主要匹配维度；IP/进程规则在 后续补齐）。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RouteRule {
    /// 规则名（日志/诊断用）
    pub name: String,
    /// 域名后缀，如 ".cn" / ".local"（不含前导点的裸域名同样支持）
    pub domain_suffix: Vec<String>,
    /// 精确域名（sing-box 的 domain；用于 NCSI / Steam 等 SNI 规则）
    pub domain: Vec<String>,
    /// 域名正则（sing-box 的 domain_regex；如单标签主机名 "^[^.]+$"）
    pub domain_regex: Vec<String>,
    /// 规则集名（如 "geosite-cn" / "geoip-cn"）
    pub rule_set: Vec<String>,
    /// 目标 IP 是否为私网（sing-box 的 ip_is_private）
    pub ip_is_private: bool,
    /// 进程名（sing-box 的 process_name；Windows 下为 exe 名）
    pub process_name: Vec<String>,
    /// 命中后的出站
    pub outbound: Outbound,
}

impl RouteRule {
    pub fn proxy(name: &str) -> Self {
        Self { name: name.into(), outbound: Outbound::Proxy, ..Default::default() }
    }

    pub fn direct(name: &str) -> Self {
        Self { name: name.into(), outbound: Outbound::Direct, ..Default::default() }
    }

    pub fn with_domain_suffix(mut self, suffixes: &[&str]) -> Self {
        self.domain_suffix = suffixes.iter().map(|s| s.to_string()).collect();
        self
    }

    pub fn with_rule_set(mut self, sets: &[&str]) -> Self {
        self.rule_set = sets.iter().map(|s| s.to_string()).collect();
        self
    }

    pub fn with_domain(mut self, domains: &[&str]) -> Self {
        self.domain = domains.iter().map(|s| s.to_string()).collect();
        self
    }

    pub fn with_domain_regex(mut self, patterns: &[&str]) -> Self {
        self.domain_regex = patterns.iter().map(|s| s.to_string()).collect();
        self
    }

    pub fn with_ip_is_private(mut self) -> Self {
        self.ip_is_private = true;
        self
    }

    pub fn with_process_name(mut self, processes: &[String]) -> Self {
        self.process_name = processes.to_vec();
        self
    }

    /// 覆盖出站（用于把 proxy 规则改到 selector 组，或把直连规则指向具名 tag）。
    pub fn with_outbound(mut self, outbound: Outbound) -> Self {
        self.outbound = outbound;
        self
    }

    pub fn block(name: &str) -> Self {
        Self { name: name.into(), outbound: Outbound::Block, ..Default::default() }
    }

    /// 序列化为 sing-box route 规则（只包含本模块已建模的字段）。
    pub fn to_singbox_rule(&self) -> Value {
        let mut m = Map::new();
        if !self.domain_suffix.is_empty() {
            m.insert("domain_suffix".into(), json!(self.domain_suffix));
        }
        if !self.domain.is_empty() {
            m.insert("domain".into(), json!(self.domain));
        }
        if !self.domain_regex.is_empty() {
            m.insert("domain_regex".into(), json!(self.domain_regex));
        }
        if !self.rule_set.is_empty() {
            m.insert("rule_set".into(), json!(self.rule_set));
        }
        if self.ip_is_private {
            m.insert("ip_is_private".into(), json!(true));
        }
        if !self.process_name.is_empty() {
            m.insert("process_name".into(), json!(self.process_name));
        }
        // sing-box 1.11+ 的 reject 用 action 表达；direct/proxy 用 outbound 名。
        match self.outbound {
            Outbound::Block => {
                m.insert("action".into(), json!("reject"));
            }
            _ => {
                m.insert("outbound".into(), json!(self.outbound.as_str()));
            }
        }
        Value::Object(m)
    }

    /// 域名是否命中本规则（后缀匹配：host 等于后缀或以 "."+后缀 结尾；匹配忽略大小写）。
    pub fn matches_domain(&self, host: &str) -> bool {
        if host.is_empty() {
            return false;
        }
        let h = host.trim_end_matches('.').to_ascii_lowercase();
        self.domain_suffix.iter().any(|raw| {
            let mut s = raw.to_ascii_lowercase();
            if s.is_empty() {
                return false;
            }
            if !s.starts_with('.') {
                s.insert(0, '.');
            }
            h == s[1..] || h.ends_with(&s)
        })
    }
}

/// 按顺序匹配：返回第一条命中的规则（与 sing-box route.rules 的语义一致）。
pub fn first_match<'a>(rules: &'a [RouteRule], host: &str) -> Option<&'a RouteRule> {
    rules.iter().find(|r| r.matches_domain(host))
}

/// neo Windows 端默认分流策略的 Rust 等价物（与 SingBoxConfigBuilder.BuildRoute 顺序对齐）：
///   ads 拒绝 → 私网直连 → geosite-cn/geoip-cn 直连 → 其余走代理。
pub fn neo_default_plan(bypass_private: bool, bypass_cn: bool, block_ads: bool) -> RoutePlan {
    let mut plan = RoutePlan::new(Outbound::Proxy);
    if block_ads {
        plan = plan.push(RouteRule::block("ads").with_rule_set(&["geosite-ads"]));
    }
    if bypass_private {
        plan = plan.push(
            RouteRule::direct("private").with_domain_suffix(&[
                ".local", ".lan", ".home.arpa", ".home", ".corp", ".intranet", ".internal", ".localdomain",
            ]),
        );
    }
    if bypass_cn {
        plan = plan.push(RouteRule::direct("cn").with_rule_set(&["geosite-cn"]));
        plan = plan.push(RouteRule::direct("cn-ip").with_rule_set(&["geoip-cn"]));
    }
    plan
}

/// 完整分流计划：有序规则 + 兜底出站（对应 sing-box route.rules + route.final）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoutePlan {
    pub rules: Vec<RouteRule>,
    pub final_outbound: Outbound,
}

impl RoutePlan {
    pub fn new(final_outbound: Outbound) -> Self {
        Self { rules: Vec::new(), final_outbound }
    }

    pub fn push(mut self, rule: RouteRule) -> Self {
        self.rules.push(rule);
        self
    }

    /// 给定目标域名返回最终出站：命中规则按规则，否则用兜底。
    pub fn decide(&self, host: &str) -> Outbound {
        first_match(&self.rules, host)
            .map(|r| r.outbound)
            .unwrap_or(self.final_outbound)
    }

    /// 生成 sing-box route 对象（rules + final），供 配置生成器复用。
    pub fn to_singbox_route(&self) -> Value {
        json!({
            "rules": self.rules.iter().map(|r| r.to_singbox_rule()).collect::<Vec<_>>(),
            "final": self.final_outbound.as_str(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn suffix_matches_exact_and_subdomain() {
        let r = RouteRule::direct("cn").with_domain_suffix(&[".cn"]);
        assert!(r.matches_domain("baidu.com.cn"));
        assert!(r.matches_domain("cn"));
        assert!(!r.matches_domain("cn.example.com"));
        assert!(!r.matches_domain(""));
    }

    #[test]
    fn bare_suffix_is_normalized() {
        let r = RouteRule::proxy("lan").with_domain_suffix(&["local"]);
        assert!(r.matches_domain("printer.local"));
        assert!(!r.matches_domain("local.com"));
    }

    #[test]
    fn case_insensitive_and_trailing_dot() {
        let r = RouteRule::direct("ms").with_domain_suffix(&[".msftconnecttest.com"]);
        assert!(r.matches_domain("WWW.MSFTCONNECTTEST.COM."));
    }

    #[test]
    fn first_match_wins() {
        // 兜底出站（route.final）不属于规则列表：没有匹配条件的规则不会命中任何域名。
        let rules = vec![
            RouteRule::direct("cn").with_domain_suffix(&[".cn"]),
            RouteRule::proxy("jp").with_domain_suffix(&[".jp"]),
        ];
        assert_eq!(first_match(&rules, "weibo.cn").map(|r| r.name.as_str()), Some("cn"));
        assert_eq!(first_match(&rules, "example.jp").map(|r| r.name.as_str()), Some("jp"));
        assert!(first_match(&rules, "x.com").is_none());
        assert!(first_match(&rules, "").is_none());
    }

    #[test]
    fn route_plan_decides_with_fallback() {
        let plan = RoutePlan::new(Outbound::Proxy)
            .push(RouteRule::direct("private").with_domain_suffix(&[".local", ".lan"]))
            .push(RouteRule::direct("cn").with_domain_suffix(&[".cn"]))
            .push(RouteRule::block("ads").with_rule_set(&["geosite-ads"]));
        assert_eq!(plan.decide("router.local"), Outbound::Direct);
        assert_eq!(plan.decide("www.baidu.cn"), Outbound::Direct);
        assert_eq!(plan.decide("example.com"), Outbound::Proxy);
        assert_eq!(plan.decide(""), Outbound::Proxy);
    }

    #[test]
    fn serializes_to_singbox_route() {
        let plan = RoutePlan::new(Outbound::Proxy)
            .push(RouteRule::direct("cn").with_domain_suffix(&[".cn"]))
            .push(RouteRule::block("ads").with_rule_set(&["geosite-ads"]));
        let v = plan.to_singbox_route();
        assert_eq!(v["final"], json!("proxy"));
        assert_eq!(v["rules"][0]["domain_suffix"][0], json!(".cn"));
        assert_eq!(v["rules"][0]["outbound"], json!("direct"));
        assert_eq!(v["rules"][1]["rule_set"][0], json!("geosite-ads"));
        assert_eq!(v["rules"][1]["action"], json!("reject"));
    }

    #[test]
    fn neo_default_plan_order_and_decisions() {
        let plan = neo_default_plan(true, true, true);
        let names: Vec<&str> = plan.rules.iter().map(|r| r.name.as_str()).collect();
        assert_eq!(names, vec!["ads", "private", "cn", "cn-ip"]);
        assert_eq!(plan.decide("router.local"), Outbound::Direct);
        assert_eq!(plan.decide("x.com"), Outbound::Proxy);
        assert_eq!(plan.final_outbound, Outbound::Proxy);

        // 全关 = 只有兜底
        let bare = neo_default_plan(false, false, false);
        assert!(bare.rules.is_empty());
        assert_eq!(bare.decide("router.local"), Outbound::Proxy);
    }

    #[test]
    fn outbound_display() {
        assert_eq!(Outbound::Proxy.to_string(), "proxy");
        assert_eq!(Outbound::Direct.to_string(), "direct");
        assert_eq!(Outbound::Block.to_string(), "block");
        assert_eq!(Outbound::Named("🚀 节点选择").to_string(), "🚀 节点选择");
    }

    #[test]
    fn named_outbound_is_used_as_singbox_outbound_tag() {
        let rule = RouteRule::proxy("组").with_outbound(Outbound::Named("🚀 节点选择"));
        let v = rule.to_singbox_rule();
        assert_eq!(v["outbound"], json!("🚀 节点选择"));
        let plan = RoutePlan::new(Outbound::Named("🐟 漏网之鱼"))
            .push(RouteRule::proxy("all").with_outbound(Outbound::Named("🚀 节点选择")));
        assert_eq!(plan.to_singbox_route()["final"], json!("🐟 漏网之鱼"));
    }

    #[test]
    fn serializes_process_and_private_ip_fields() {
        let rule = RouteRule::direct("game")
            .with_process_name(&["TslGame.exe".to_string(), "cs2.exe".to_string()]);
        let v = rule.to_singbox_rule();
        assert_eq!(v["process_name"], json!(["TslGame.exe", "cs2.exe"]));
        assert_eq!(v["outbound"], json!("direct"));

        let private = RouteRule::direct("private").with_ip_is_private();
        assert_eq!(private.to_singbox_rule()["ip_is_private"], json!(true));

        let regex = RouteRule::direct("single-label").with_domain_regex(&["^[^.]+$"]);
        assert_eq!(regex.to_singbox_rule()["domain_regex"], json!(["^[^.]+$"]));

        let exact = RouteRule::direct("exact").with_domain(&["example.com"]);
        assert_eq!(exact.to_singbox_rule()["domain"], json!(["example.com"]));
    }
}
