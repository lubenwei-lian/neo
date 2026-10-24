//! neo Android JNI 纯函数桥。
//!
//! 只暴露无状态纯函数，输入输出全部是字符串/布尔；加载失败或任何异常由 Java 侧回退原实现。
//! 当前函数：
//!   - hintOf：订阅占位/到期关键词识别（与 SubGuard.HINTS 同表）
//!   - isLocal：判断主机是否本机地址（127./localhost/::1/0.0.0.0）
//!   - configPorts：从 sing-box 配置 JSON 中提取 mixed/socks/http 入站端口（逗号分隔）
//!   - userinfoParse：解析 subscription-userinfo 头（upload/download/total/expire 的 JSON）

use std::collections::BTreeSet;

use jni::objects::{JClass, JString};
use jni::sys::{jboolean, jstring, JNI_FALSE, JNI_TRUE};
use jni::JNIEnv;

const HINTS: [&str; 7] = [
    "使用的设备过多",
    "请去官网获取新订阅链接",
    "设备过多",
    "请去官网",
    "官网地址",
    "套餐已到期",
    "流量已用尽",
];

/// 返回命中的关键词；无命中返回空串。
pub fn hint_of(text: &str) -> &'static str {
    if text.is_empty() {
        return "";
    }
    for hint in HINTS {
        if text.contains(hint) {
            return hint;
        }
    }
    ""
}

/// 与 SubGuard.isLocal 同语义：127.* / localhost / ::1 / 0.0.0.0 前缀。
pub fn is_local(host: &str) -> bool {
    if host.trim().is_empty() {
        // 与 Java 侧一致：空主机名按「本机/无效」处理（占位订阅判定用）
        return true;
    }
    let h = host.trim().to_ascii_lowercase();
    h == "localhost" || h == "::1" || h == "[::1]" || h == "0.0.0.0" || h == "127.0.0.1" || h.starts_with("127.")
}

/// 从 sing-box 配置 JSON 提取 mixed/socks/http 入站端口，按出现顺序逗号拼接。
/// 解析失败返回 Err(原因)；没有可用端口返回空串。
pub fn config_ports(config_json: &str) -> Result<String, String> {
    let value: serde_json::Value =
        serde_json::from_str(config_json).map_err(|e| format!("json: {e}"))?;
    let mut out: Vec<u16> = Vec::new();
    let mut seen: BTreeSet<u16> = BTreeSet::new();
    if let Some(inbounds) = value.get("inbounds").and_then(|v| v.as_array()) {
        for inbound in inbounds {
            let kind = inbound.get("type").and_then(|v| v.as_str()).unwrap_or("");
            if !matches!(kind, "mixed" | "socks" | "http") {
                continue;
            }
            let port = inbound
                .get("listen_port")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            if port == 0 || port > 65535 {
                continue;
            }
            let port = port as u16;
            if seen.insert(port) {
                out.push(port);
            }
        }
    }
    Ok(out
        .iter()
        .map(|p| p.to_string())
        .collect::<Vec<_>>()
        .join(","))
}

/// 解析 subscription-userinfo 头（大小写不敏感，逗号分隔 key=value）。
/// 返回 JSON: {"upload":..,"download":..,"total":..,"expire":..}，缺省字段为 null。
pub fn userinfo_parse(header: &str) -> String {
    let mut upload: Option<i64> = None;
    let mut download: Option<i64> = None;
    let mut total: Option<i64> = None;
    let mut expire: Option<i64> = None;
    for part in header.split(',') {
        let Some((key, value)) = part.split_once('=') else {
            continue;
        };
        let key = key.trim().to_ascii_lowercase();
        let raw = value.trim().trim_matches('"');
        let Ok(num) = raw.parse::<i64>() else { continue };
        match key.as_str() {
            "upload" => upload = Some(num),
            "download" => download = Some(num),
            "total" => total = Some(num),
            "expire" => expire = Some(num),
            _ => {}
        }
    }
    let json = serde_json::json!({
        "upload": upload,
        "download": download,
        "total": total,
        "expire": expire,
    });
    json.to_string()
}

// ==================== JNI 导出 ====================

fn read_string(env: &mut JNIEnv, text: &JString) -> String {
    env.get_string(text)
        .map(|s| s.into())
        .unwrap_or_default()
}

fn to_jstring(env: &mut JNIEnv, value: &str) -> jstring {
    match env.new_string(value) {
        Ok(s) => s.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub extern "system" fn Java_io_neoguard_RustBridge_hintOfNative(
    mut env: JNIEnv,
    _class: JClass,
    text: JString,
) -> jstring {
    let input = read_string(&mut env, &text);
    to_jstring(&mut env, hint_of(&input))
}

#[no_mangle]
pub extern "system" fn Java_io_neoguard_RustBridge_isLocalNative(
    mut env: JNIEnv,
    _class: JClass,
    host: JString,
) -> jboolean {
    let input = read_string(&mut env, &host);
    if is_local(&input) {
        JNI_TRUE
    } else {
        JNI_FALSE
    }
}

#[no_mangle]
pub extern "system" fn Java_io_neoguard_RustBridge_configPortsNative(
    mut env: JNIEnv,
    _class: JClass,
    json: JString,
) -> jstring {
    let input = read_string(&mut env, &json);
    match config_ports(&input) {
        Ok(ports) => to_jstring(&mut env, &ports),
        Err(_) => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub extern "system" fn Java_io_neoguard_RustBridge_userinfoParseNative(
    mut env: JNIEnv,
    _class: JClass,
    header: JString,
) -> jstring {
    let input = read_string(&mut env, &header);
    let out = userinfo_parse(&input);
    to_jstring(&mut env, &out)
}

// ==================== 配置优化（订阅配置迁移/优化管线） ====================

use serde_json::{json, Map, Value};

fn contains_token(value: Option<&Value>, token: &str) -> bool {
    match value {
        Some(Value::Array(items)) => items.iter().any(|x| x.as_str() == Some(token)),
        Some(Value::String(s)) => s == token,
        Some(_) => false,
        None => false,
    }
}

fn as_str_field<'a>(obj: &'a Map<String, Value>, key: &str) -> &'a str {
    obj.get(key).and_then(|v| v.as_str()).unwrap_or("")
}

fn optimize_dns(root: &mut Map<String, Value>) -> bool {
    let Some(dns) = root.get_mut("dns").and_then(|v| v.as_object_mut()) else {
        return false;
    };

    let (fakeip_tag, domestic_tag, remote_tag, changed_scan) = {
        let Some(servers) = dns.get_mut("servers").and_then(|v| v.as_array_mut()) else {
            return false;
        };
        let mut fakeip_tag: Option<String> = None;
        let mut domestic_tag: Option<String> = None;
        let mut remote_tag: Option<String> = None;
        let mut changed = false;
        for server in servers.iter_mut() {
            let Some(obj) = server.as_object_mut() else {
                continue;
            };
            let ty = as_str_field(obj, "type").to_string();
            let tag = as_str_field(obj, "tag").to_string();
            if ty == "fakeip" {
                fakeip_tag = Some(tag.clone());
                if obj.remove("inet6_range").is_some() {
                    changed = true;
                }
                if !obj.contains_key("inet4_range") {
                    obj.insert("inet4_range".to_string(), json!("198.18.0.0/15"));
                    changed = true;
                }
            } else if domestic_tag.is_none()
                && !tag.is_empty()
                && !obj.contains_key("detour")
                && matches!(ty.as_str(), "udp" | "https" | "tls")
            {
                domestic_tag = Some(tag.clone());
            }
            if remote_tag.is_none() && !tag.is_empty() && obj.contains_key("detour") && ty != "fakeip" {
                remote_tag = Some(tag);
            }
        }
        (fakeip_tag, domestic_tag, remote_tag, changed)
    };

    let Some(fakeip_tag) = fakeip_tag else {
        return changed_scan;
    };

    let mut changed = changed_scan;
    if as_str_field(dns, "strategy") != "prefer_ipv4" {
        dns.insert("strategy".to_string(), json!("prefer_ipv4"));
        changed = true;
    }

    let old_rules = dns
        .get("rules")
        .and_then(|v| v.as_array())
        .cloned()
        .unwrap_or_default();
    let mut fresh: Vec<Value> = Vec::with_capacity(old_rules.len() + 3);
    let mut has_cn_rule = false;
    for rule in old_rules {
        let Some(obj) = rule.as_object() else {
            fresh.push(rule);
            continue;
        };
        let server = as_str_field(obj, "server").to_string();
        if contains_token(obj.get("rule_set"), "geosite-cn") {
            has_cn_rule = true;
            let mut obj = obj.clone();
            if let Some(domestic) = &domestic_tag {
                if domestic != &server {
                    obj.insert("server".to_string(), Value::String(domestic.clone()));
                    changed = true;
                }
            }
            fresh.push(Value::Object(obj));
            continue;
        }
        if server == fakeip_tag {
            continue; // 旧 fakeip 规则统一重建（仅 A）
        }
        fresh.push(rule);
    }
    if !has_cn_rule {
        if let Some(domestic) = &domestic_tag {
            fresh.push(json!({"rule_set": ["geosite-cn"], "server": domestic}));
            changed = true;
        }
    }
    if let Some(remote) = &remote_tag {
        fresh.push(json!({"query_type": ["AAAA"], "server": remote}));
        fresh.push(json!({"query_type": ["HTTPS", "SVCB"], "server": remote}));
        changed = true;
    }
    fresh.push(json!({"query_type": ["A"], "server": fakeip_tag}));
    changed = true;
    dns.insert("rules".to_string(), Value::Array(fresh));
    if let Some(remote) = &remote_tag {
        dns.insert("final".to_string(), Value::String(remote.clone()));
    } else if let Some(domestic) = &domestic_tag {
        dns.insert("final".to_string(), Value::String(domestic.clone()));
    }
    changed
}

fn ensure_route(root: &mut Map<String, Value>) -> bool {
    let Some(route) = root.get_mut("route").and_then(|v| v.as_object_mut()) else {
        return false;
    };
    let mut rules: Vec<Value> = route
        .get("rules")
        .and_then(|v| v.as_array())
        .cloned()
        .unwrap_or_default();
    let mut has_sniff = false;
    let mut has_hijack = false;
    let mut has_private = false;
    let mut has_cn = false;
    for rule in &rules {
        let Some(obj) = rule.as_object() else {
            continue;
        };
        let action = as_str_field(obj, "action");
        if action == "sniff" {
            has_sniff = true;
        }
        if action == "hijack-dns" {
            has_hijack = true;
        }
        if obj.get("ip_is_private").and_then(|v| v.as_bool()).unwrap_or(false) {
            has_private = true;
        }
        if as_str_field(obj, "outbound") == "direct"
            && (contains_token(obj.get("rule_set"), "geosite-cn")
                || contains_token(obj.get("rule_set"), "geoip-cn"))
        {
            has_cn = true;
        }
    }
    let mut changed = false;
    if !has_sniff {
        rules.insert(0, json!({"action": "sniff"}));
        changed = true;
    }
    if !has_hijack {
        let at = if rules.is_empty() { 0 } else { 1 };
        rules.insert(at, json!({"protocol": "dns", "action": "hijack-dns"}));
        changed = true;
    }
    if !has_private {
        rules.push(json!({"ip_is_private": true, "outbound": "direct"}));
        changed = true;
    }
    if !has_cn {
        rules.push(json!({"rule_set": ["geosite-cn", "geoip-cn"], "outbound": "direct"}));
        changed = true;
    }
    if changed {
        route.insert("rules".to_string(), Value::Array(rules));
    }
    changed
}

fn strip_domain_strategy(root: &mut Map<String, Value>) -> bool {
    let mut changed = false;
    for key in ["outbounds", "endpoints"] {
        let Some(items) = root.get_mut(key).and_then(|v| v.as_array_mut()) else {
            continue;
        };
        for item in items.iter_mut() {
            if let Some(obj) = item.as_object_mut() {
                if obj.remove("domain_strategy").is_some() {
                    changed = true;
                }
            }
        }
    }
    changed
}

/// 订阅配置优化（与 SubGuard.optimizeJavaOnly 逐条等价）：
/// prefer_ipv4、fakeip 仅 IPv4、DNS 规则重建（CN 直连 DNS / 国外 AAAA·HTTPS 走节点侧 DNS）、
/// 路由补齐 sniff/hijack-dns/本机直连/CN 直连、去掉出站 domain_strategy。
/// 解析失败或无改动时原样返回。
pub fn optimize_config(content: &str) -> String {
    let Some(brace) = content.find('{') else {
        return content.to_string();
    };
    let Ok(mut root) = serde_json::from_str::<Value>(&content[brace..]) else {
        return content.to_string();
    };
    let Some(_) = root.as_object_mut() else {
        return content.to_string();
    };
    let mut changed = false;
    {
        let Some(obj) = root.as_object_mut() else {
            return content.to_string();
        };
        changed |= optimize_dns(obj);
        changed |= ensure_route(obj);
        changed |= strip_domain_strategy(obj);
    }
    if changed {
        root.to_string()
    } else {
        content.to_string()
    }
}

#[no_mangle]
pub extern "system" fn Java_io_neoguard_RustBridge_optimizeConfigNative(
    mut env: JNIEnv,
    _class: JClass,
    json: JString,
) -> jstring {
    let input = read_string(&mut env, &json);
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| optimize_config(&input)))
        .unwrap_or_else(|_| input.clone());
    to_jstring(&mut env, &result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hint_matches_java_table() {
        assert_eq!(hint_of(""), "");
        assert_eq!(hint_of("正常节点"), "");
        assert_eq!(hint_of("请去官网获取新订阅链接"), "请去官网获取新订阅链接");
        assert_eq!(hint_of("套餐已到期"), "套餐已到期");
        assert_eq!(hint_of("xx 流量已用尽 yy"), "流量已用尽");
    }

    #[test]
    fn local_host_detection() {
        assert!(is_local("127.0.0.1"));
        assert!(is_local("127.1.2.3"));
        assert!(is_local("localhost"));
        assert!(is_local("::1"));
        assert!(is_local("0.0.0.0"));
        assert!(is_local("[::1]"));
        assert!(is_local("")); // 空 = 本机/无效，与 Java 一致
        assert!(!is_local("192.168.1.1"));
        assert!(!is_local("example.com"));
        assert!(!is_local("0.0.0.0.example.com")); // 精确匹配，不误判前缀
    }

    #[test]
    fn config_ports_extracts_mixed_socks_http() {
        let json = r#"{"inbounds":[
            {"type":"tun","tag":"tun-in"},
            {"type":"mixed","listen_port":7891},
            {"type":"socks","listen_port":7892},
            {"type":"http","listen_port":7893},
            {"type":"mixed","listen_port":7891}
        ]}"#;
        assert_eq!(config_ports(json).unwrap(), "7891,7892,7893");
        assert!(config_ports("not json").is_err());
        assert_eq!(config_ports(r#"{"inbounds":[]}"#).unwrap(), "");
    }

    #[test]
    fn userinfo_parse_case_insensitive() {
        let out = userinfo_parse("upload=1024, download=2048,TOTAL=104857600,expire=1760000000");
        let v: serde_json::Value = serde_json::from_str(&out).unwrap();
        assert_eq!(v["upload"], 1024);
        assert_eq!(v["download"], 2048);
        assert_eq!(v["total"], 104857600);
        assert_eq!(v["expire"], 1760000000);
        let empty: serde_json::Value = serde_json::from_str(&userinfo_parse("foo=bar")).unwrap();
        assert!(empty["upload"].is_null());
    }
}
