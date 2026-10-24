//! Clash API 数据解析（sing-box `clash_api` / mihomo 共用，供故障转移/节点切换使用）。

use serde_json::Value;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProxyInfo {
    pub name: String,
    /// Selector / URLTest / Fallback / LoadBalance / 具体协议名
    pub kind: String,
    /// 当前选中项（组才有）
    pub now: Option<String>,
    /// 组成员（组才有）
    pub all: Vec<String>,
}

impl ProxyInfo {
    pub fn is_group(&self) -> bool {
        matches!(self.kind.as_str(), "Selector" | "URLTest" | "Fallback" | "LoadBalance")
    }
}

/// 解析 `GET /proxies` 的响应体（`{"proxies":{name:{type,now,all}}}`）。
pub fn parse_proxies(json: &str) -> Option<Vec<ProxyInfo>> {
    let root: Value = serde_json::from_str(json).ok()?;
    let map = root.get("proxies")?.as_object()?;
    let mut out = Vec::with_capacity(map.len());
    for (name, v) in map {
        let kind = v.get("type").and_then(|x| x.as_str()).unwrap_or("").to_string();
        let now = v.get("now").and_then(|x| x.as_str()).map(|s| s.to_string());
        let all = v
            .get("all")
            .and_then(|x| x.as_array())
            .map(|a| a.iter().filter_map(|x| x.as_str().map(|s| s.to_string())).collect())
            .unwrap_or_default();
        out.push(ProxyInfo { name: name.clone(), kind, now, all });
    }
    Some(out)
}

/// 找主选择组：优先名字含「节点选择/Proxy/PROXY」的 Selector，否则第一个 Selector。
pub fn find_selector<'a>(proxies: &'a [ProxyInfo], preferred: &[&str]) -> Option<&'a ProxyInfo> {
    let selectors: Vec<&ProxyInfo> = proxies.iter().filter(|p| p.kind == "Selector").collect();
    for want in preferred {
        if let Some(p) = selectors.iter().find(|p| p.name.contains(want)) {
            return Some(p);
        }
    }
    selectors.first().copied()
}

/// 组内真实节点（跳过组类型与当前节点）。
pub fn candidate_nodes(proxies: &[ProxyInfo], group: &ProxyInfo) -> Vec<String> {
    let kind_of = |name: &str| proxies.iter().find(|p| p.name == name).map(|p| p.kind.as_str());
    group
        .all
        .iter()
        .filter(|name| {
            if Some(name.as_str()) == group.now.as_deref() {
                return false;
            }
            match kind_of(name) {
                Some(k) => !matches!(k, "Selector" | "URLTest" | "Fallback" | "LoadBalance"),
                None => false,
            }
        })
        .cloned()
        .collect()
}

/// 解析 `GET /proxies/{name}/delay` 的响应（`{"delay":123}`，失败无 delay）。
pub fn parse_delay(json: &str) -> Option<u32> {
    let v: Value = serde_json::from_str(json).ok()?;
    v.get("delay").and_then(|d| d.as_u64()).map(|d| d as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = r#"{
      "proxies": {
        "🚀 节点选择": {"type":"Selector","now":"♻️ 自动选择","all":["♻️ 自动选择","🇯🇵日本01","🇺🇸美国01"]},
        "♻️ 自动选择": {"type":"URLTest","now":"🇯🇵日本01","all":["🇯🇵日本01","🇺🇸美国01"]},
        "🇯🇵日本01": {"type":"VLESS"},
        "🇺🇸美国01": {"type":"Hysteria2"}
      }
    }"#;

    #[test]
    fn parses_and_finds_selector() {
        let proxies = parse_proxies(SAMPLE).unwrap();
        assert_eq!(proxies.len(), 4);
        let sel = find_selector(&proxies, &["节点选择", "Proxy"]).unwrap();
        assert_eq!(sel.name, "🚀 节点选择");
        assert_eq!(sel.now.as_deref(), Some("♻️ 自动选择"));
    }

    #[test]
    fn candidates_skip_groups_and_current() {
        let proxies = parse_proxies(SAMPLE).unwrap();
        let sel = find_selector(&proxies, &["节点选择"]).unwrap();
        let cands = candidate_nodes(&proxies, sel);
        assert_eq!(cands, vec!["🇯🇵日本01".to_string(), "🇺🇸美国01".to_string()]);
    }

    #[test]
    fn parses_delay() {
        assert_eq!(parse_delay(r#"{"delay":123}"#), Some(123));
        assert_eq!(parse_delay(r#"{"message":"timeout"}"#), None);
        assert_eq!(parse_delay("not json"), None);
    }

    #[test]
    fn bad_input_returns_none() {
        assert_eq!(parse_proxies("nope"), None);
        assert_eq!(parse_proxies(r#"{"x":1}"#), None);
    }
}