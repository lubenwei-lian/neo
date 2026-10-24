//! DNS 分流模型（与 SingBoxConfigBuilder.BuildDns 对齐的纯数据 + sing-box 序列化）。

use serde_json::{json, Value};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DnsServer {
    FakeIp {
        tag: String,
        ipv6: bool,
    },
    /// 本地 UDP 解析器；`domain_resolver` 用于「服务器本身是域名」时指向 bootstrap。
    Direct {
        tag: String,
        address: String,
        domain_resolver: Option<String>,
    },
    /// 走代理的 DoH 解析器（`detour` 必须是真实存在的出站/策略组 tag）。
    Remote {
        tag: String,
        address: String,
        detour: String,
    },
    /// 自定义解析器（用户 DNS 为 https:// / tls:// 时使用）。
    Custom {
        tag: String,
        kind: String,
        server: String,
        server_port: Option<u16>,
        path: Option<String>,
        detour: Option<String>,
        domain_resolver: Option<String>,
    },
}

impl DnsServer {
    pub fn tag(&self) -> &str {
        match self {
            DnsServer::FakeIp { tag, .. }
            | DnsServer::Direct { tag, .. }
            | DnsServer::Remote { tag, .. }
            | DnsServer::Custom { tag, .. } => tag,
        }
    }

    pub fn to_singbox(&self) -> Value {
        match self {
            DnsServer::FakeIp { tag, ipv6 } => {
                let mut v = json!({ "type": "fakeip", "tag": tag, "inet4_range": "198.18.0.0/15" });
                if *ipv6 {
                    v["inet6_range"] = json!("fc00::/18");
                }
                v
            }
            DnsServer::Direct { tag, address, domain_resolver } => {
                let mut v = json!({ "type": "udp", "tag": tag, "server": address });
                if let Some(r) = domain_resolver {
                    v["domain_resolver"] = json!(r);
                }
                v
            }
            DnsServer::Remote { tag, address, detour } => {
                json!({ "type": "https", "tag": tag, "server": address, "detour": detour })
            }
            DnsServer::Custom { tag, kind, server, server_port, path, detour, domain_resolver } => {
                let mut v = json!({ "type": kind, "tag": tag, "server": server });
                if let Some(port) = server_port {
                    v["server_port"] = json!(port);
                }
                if let Some(p) = path {
                    v["path"] = json!(p);
                }
                if let Some(d) = detour {
                    v["detour"] = json!(d);
                }
                if let Some(r) = domain_resolver {
                    v["domain_resolver"] = json!(r);
                }
                v
            }
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DnsRule {
    pub domain_suffix: Vec<String>,
    pub domain_regex: Vec<String>,
    pub query_type: Vec<String>,
    pub rule_set: Vec<String>,
    pub server: String,
    /// 非空时表示「动作型规则」（如 predefined），此时不写 server。
    pub action: Option<String>,
    pub rcode: Option<String>,
}

impl DnsRule {
    pub fn suffix(server: &str, suffixes: &[&str]) -> Self {
        Self {
            domain_suffix: suffixes.iter().map(|s| s.to_string()).collect(),
            server: server.into(),
            ..Default::default()
        }
    }

    pub fn query_type(server: &str, types: &[&str]) -> Self {
        Self {
            query_type: types.iter().map(|s| s.to_string()).collect(),
            server: server.into(),
            ..Default::default()
        }
    }

    pub fn regex(server: &str, patterns: &[&str]) -> Self {
        Self {
            domain_regex: patterns.iter().map(|s| s.to_string()).collect(),
            server: server.into(),
            ..Default::default()
        }
    }

    pub fn rule_set(server: &str, sets: &[&str]) -> Self {
        Self {
            rule_set: sets.iter().map(|s| s.to_string()).collect(),
            server: server.into(),
            ..Default::default()
        }
    }

    /// TUN 仅 IPv4 时对 AAAA 回 NODATA（NOERROR + 空答案），
    /// 与Windows 端 `SingBoxConfigBuilder` 的 修复同构。
    pub fn aaaa_nodata() -> Self {
        Self {
            query_type: vec!["AAAA".into()],
            action: Some("predefined".into()),
            rcode: Some("NOERROR".into()),
            ..Default::default()
        }
    }

    pub fn to_singbox(&self) -> Value {
        let mut v = json!({});
        if !self.server.is_empty() {
            v["server"] = json!(self.server);
        }
        if let Some(action) = &self.action {
            v["action"] = json!(action);
        }
        if let Some(rcode) = &self.rcode {
            v["rcode"] = json!(rcode);
        }
        if !self.domain_suffix.is_empty() {
            v["domain_suffix"] = json!(self.domain_suffix);
        }
        if !self.domain_regex.is_empty() {
            v["domain_regex"] = json!(self.domain_regex);
        }
        if !self.query_type.is_empty() {
            v["query_type"] = json!(self.query_type);
        }
        if !self.rule_set.is_empty() {
            v["rule_set"] = json!(self.rule_set);
        }
        v
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DnsPlan {
    pub servers: Vec<DnsServer>,
    pub rules: Vec<DnsRule>,
    pub final_server: String,
}

impl DnsPlan {
    /// tun = 只下发 fake IPv4；include_rule_set = 是否引用 geosite-cn（BuildTest 时为 false）。
    pub fn neo_default(tun: bool, include_rule_set: bool) -> Self {
        Self::neo_default_with(tun, include_rule_set, "223.5.5.5", None, "proxy")
    }

    /// 与Windows 端 `SingBoxConfigBuilder.BuildDns` 同构的完整计划：
    /// - `direct_server` 是用户 DNS（IP 或域名；域名需 `direct_resolver` 指向 bootstrap）；
    /// - `remote_detour` 指向真实存在的出站/策略组 tag（DNS 远程查询走代理）；
    /// - 规则顺序：私网 → （可选）geosite-cn → NCSI → HTTPS/SVCB → 单标签主机名 → fakeip(A)。
    ///
    /// fakeip 只在 tun=true 时生成，且只有 IPv4 range（仅 IPv4）。
    pub fn neo_default_with(
        tun: bool,
        include_rule_set: bool,
        direct_server: &str,
        direct_resolver: Option<&str>,
        remote_detour: &str,
    ) -> Self {
        let mut servers = Vec::new();
        if tun {
            servers.push(DnsServer::FakeIp { tag: "dns-fakeip".into(), ipv6: false });
        }
        servers.push(DnsServer::Direct {
            tag: "dns-direct".into(),
            address: direct_server.into(),
            domain_resolver: direct_resolver.map(str::to_string),
        });
        servers.push(DnsServer::Remote {
            tag: "dns-remote".into(),
            address: "8.8.8.8".into(),
            detour: remote_detour.into(),
        });

        // TUN 只有 IPv4（无 inet6_address/路由）时，AAAA 一律回 NODATA：
        // 应用立即回落 IPv4，不再经代理 DoH 拿无法接管的真实 IPv6（与Windows 端 同构）。
        let mut rules = Vec::new();
        if tun {
            rules.push(DnsRule::aaaa_nodata());
        }
        rules.push(DnsRule::suffix(
            "dns-direct",
            &[".local", ".lan", ".home.arpa", ".home", ".corp", ".intranet", ".internal", ".localdomain"],
        ));
        if include_rule_set {
            rules.push(DnsRule::rule_set("dns-direct", &["geosite-cn"]));
        }
        // Windows 连通性探测走直连解析。
        rules.push(DnsRule::suffix("dns-direct", &[".msftconnecttest.com", ".msftncsi.com"]));
        // HTTPS/SVCB 查询不绕代理 DoH。
        rules.push(DnsRule::query_type("dns-direct", &["HTTPS", "SVCB"]));
        // 单标签内网主机名（nas、printer）不能落到 final=dns-remote。
        rules.push(DnsRule::regex("dns-direct", &["^[^.]+$"]));
        // 其余 A 查询交给 fakeip；AAAA 已在最前面被 NODATA 规则拦掉。
        if tun {
            rules.push(DnsRule::query_type("dns-fakeip", &["A"]));
        }
        Self { servers, rules, final_server: "dns-remote".into() }
    }

    pub fn to_singbox(&self) -> Value {
        json!({
            "servers": self.servers.iter().map(|s| s.to_singbox()).collect::<Vec<_>>(),
            "rules": self.rules.iter().map(|r| r.to_singbox()).collect::<Vec<_>>(),
            "final": self.final_server,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tun_plan_has_no_fake_ipv6_and_only_a_query() {
        let plan = DnsPlan::neo_default(true, false);
        let v = plan.to_singbox();
        let fake = v["servers"].as_array().unwrap().iter().find(|s| s["type"] == "fakeip").unwrap();
        assert!(fake.get("inet6_range").is_none());
        let last = v["rules"].as_array().unwrap().last().unwrap();
        assert_eq!(last["query_type"], json!(["A"]));
        assert_eq!(last["server"], json!("dns-fakeip"));
    }

    #[test]
    fn tun_plan_aaaa_nodata_rule_first() {
        let plan = DnsPlan::neo_default(true, false);
        let v = plan.to_singbox();
        let first = &v["rules"].as_array().unwrap()[0];
        assert_eq!(first["query_type"], json!(["AAAA"]));
        assert_eq!(first["action"], json!("predefined"));
        assert_eq!(first["rcode"], json!("NOERROR"));
        assert!(first.get("server").is_none(), "动作型规则不应带 server");
        // 非 TUN 不应有该规则
        let non = DnsPlan::neo_default(false, false).to_singbox();
        assert!(non["rules"].as_array().unwrap().iter().all(|r| r["action"] != json!("predefined")));
    }

    #[test]
    fn non_tun_plan_has_no_fakeip() {
        let plan = DnsPlan::neo_default(false, false);
        let v = plan.to_singbox();
        assert!(v["servers"].as_array().unwrap().iter().all(|s| s["type"] != "fakeip"));
        assert!(v["rules"].as_array().unwrap().iter().all(|r| r["server"] != "dns-fakeip"));
        assert_eq!(v["final"], json!("dns-remote"));
    }

    #[test]
    fn private_suffix_and_https_svcb_precede_fakeip() {
        let plan = DnsPlan::neo_default(true, true);
        let v = plan.to_singbox();
        let rules = v["rules"].as_array().unwrap();
        let priv_idx = rules.iter().position(|r| {
            r["domain_suffix"].as_array().map_or(false, |a| a.iter().any(|x| x == ".local"))
        }).unwrap();
        let fake_idx = rules.iter().position(|r| r["server"] == "dns-fakeip").unwrap();
        assert!(priv_idx < fake_idx);
        assert!(rules.iter().any(|r| r["query_type"] == json!(["HTTPS", "SVCB"])));
    }

    #[test]
    fn single_label_hostname_rule_exists_in_both_modes() {
        for tun in [false, true] {
            let v = DnsPlan::neo_default(tun, false).to_singbox();
            assert!(
                v["rules"].as_array().unwrap().iter().any(|r| r["domain_regex"] == json!(["^[^.]+$"])),
                "tun={tun} 缺少单标签主机名规则"
            );
        }
    }

    #[test]
    fn custom_bootstrap_and_detour_are_serialized() {
        let plan = DnsPlan::neo_default_with(true, true, "dns.example.com", Some("dns-bootstrap"), "🚀 节点选择");
        let v = plan.to_singbox();
        let direct = v["servers"].as_array().unwrap().iter().find(|s| s["tag"] == "dns-direct").unwrap();
        assert_eq!(direct["server"], json!("dns.example.com"));
        assert_eq!(direct["domain_resolver"], json!("dns-bootstrap"));
        let remote = v["servers"].as_array().unwrap().iter().find(|s| s["tag"] == "dns-remote").unwrap();
        assert_eq!(remote["detour"], json!("🚀 节点选择"));
        assert!(v["rules"].as_array().unwrap().iter().any(|r| r["rule_set"] == json!(["geosite-cn"])));
    }

    #[test]
    fn custom_doh_server_serializes_port_path_and_detour() {
        let server = DnsServer::Custom {
            tag: "dns-direct".into(),
            kind: "https".into(),
            server: "1.1.1.1".into(),
            server_port: Some(8443),
            path: Some("/dns-query".into()),
            detour: None,
            domain_resolver: None,
        };
        let v = server.to_singbox();
        assert_eq!(v["type"], json!("https"));
        assert_eq!(v["server_port"], json!(8443));
        assert_eq!(v["path"], json!("/dns-query"));
        assert!(v.get("detour").is_none());
    }
}