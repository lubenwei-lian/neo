//! 分享链接解析。
//!
//! 分享链接解析实现。
//! 与 版一致的关键规则：
//! - 大小写不敏感的前缀分发；
//! - vmess 的 base64(JSON)；
//! - ss 的 SIP002（base64/明文 userinfo）与旧式整段 base64；
//! - hy2 端口跳跃归一化、gecko 默认分片；
//! - IPv6 字面量去方括号；
//! - 所有错误返回 ParseError，不 panic。

use std::fmt;

use base64::engine::general_purpose::STANDARD;
use base64::Engine as _;
use serde_json::Value;

use crate::node::{Node, Protocol};

/// 解析失败原因。调用方可以保留原文并在 UI/日志中展示。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParseError {
    message: String,
}

impl ParseError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for ParseError {}

type Result<T> = std::result::Result<T, ParseError>;

/// 解析单条分享链接。空串、含换行、未知协议、结构损坏都返回 Err。
pub fn parse_share_link(raw: &str) -> Result<Node> {
    let link = raw.trim();
    if link.is_empty() {
        return Err(ParseError::new("链接为空"));
    }
    if link.contains('\r') || link.contains('\n') {
        return Err(ParseError::new("链接包含换行符"));
    }
    let (scheme, rest) = link
        .split_once("://")
        .ok_or_else(|| ParseError::new("缺少 :// 协议前缀"))?;
    let scheme = scheme.to_ascii_lowercase();
    match scheme.as_str() {
        "vmess" => parse_vmess(rest.trim()),
        "vless" => parse_vless(rest),
        "trojan" => parse_trojan(rest),
        "ss" => parse_shadowsocks(rest),
        "hysteria2" | "hy2" => parse_hysteria2(rest),
        "tuic" => parse_tuic(rest),
        "anytls" => parse_anytls(rest),
        "socks" | "socks5" | "socks5h" => parse_socks(rest),
        "naive+https" => parse_naive(rest),
        "wireguard" | "wg" => parse_wireguard(rest),
        other => Err(ParseError::new(format!("不支持的协议: {other}"))),
    }
}

/// 批量解析：逐行尝试，坏行跳过，保留成功节点（与 ParseMany 语义一致）。
pub fn parse_many<I, S>(lines: I) -> Vec<Node>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    lines
        .into_iter()
        .filter_map(|line| parse_share_link(line.as_ref()).ok())
        .collect()
}

// ---------------------------------------------------------------------------
// 公共 URL 片段处理
// ---------------------------------------------------------------------------

type Query = Vec<(String, String)>;

struct LinkParts<'a> {
    authority: &'a str,
    query: Query,
    name: String,
}

fn split_link_parts(rest: &str) -> LinkParts<'_> {
    let (before_fragment, fragment) = rest.split_once('#').unwrap_or((rest, ""));
    let (authority, query_raw) = before_fragment
        .split_once('?')
        .unwrap_or((before_fragment, ""));
    LinkParts {
        authority,
        query: parse_query(query_raw),
        name: pct_decode(fragment),
    }
}

fn parse_query(raw: &str) -> Query {
    let raw = raw.trim_start_matches('?');
    if raw.is_empty() {
        return Vec::new();
    }
    let mut out = Vec::new();
    for pair in raw.split('&') {
        if pair.is_empty() {
            continue;
        }
        let (key, value) = pair.split_once('=').unwrap_or((pair, ""));
        out.push((pct_decode(key), pct_decode(value)));
    }
    out
}

/// 大小写不敏感地取参数；同一参数出现多次时取最后一个（与 Dictionary 赋值语义一致）。
fn qget<'a>(query: &'a Query, key: &str) -> Option<&'a str> {
    query
        .iter()
        .rev()
        .find(|(k, _)| k.eq_ignore_ascii_case(key))
        .map(|(_, v)| v.as_str())
}

fn qnonempty<'a>(query: &'a Query, key: &str) -> Option<&'a str> {
    qget(query, key).filter(|v| !v.is_empty())
}

fn opt_string(value: Option<&str>) -> Option<String> {
    value.filter(|v| !v.is_empty()).map(str::to_string)
}

fn pct_decode(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Some(hi), Some(lo)) = (hex_val(bytes[i + 1]), hex_val(bytes[i + 2])) {
                out.push((hi << 4) | lo);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

/// 兼容 URL-safe、缺 padding、内含空白的 base64（TryB64Decode 等价物）。
fn b64_decode(input: &str) -> Result<String> {
    if input.is_empty() {
        return Err(ParseError::new("base64 内容为空"));
    }
    let normalized: String = input
        .chars()
        .filter(|c| !c.is_ascii_whitespace())
        .map(|c| match c {
            '-' => '+',
            '_' => '/',
            other => other,
        })
        .collect();
    let trimmed = normalized.trim_end_matches('=');
    if trimmed.is_empty() {
        return Err(ParseError::new("base64 内容无效"));
    }
    let mut padded = trimmed.to_string();
    while padded.len() % 4 != 0 {
        padded.push('=');
    }
    let bytes = STANDARD
        .decode(padded.as_bytes())
        .map_err(|e| ParseError::new(format!("base64 解码失败: {e}")))?;
    String::from_utf8(bytes).map_err(|_| ParseError::new("base64 解码结果不是 UTF-8"))
}

fn parse_port(raw: &str) -> Result<u16> {
    let value: u32 = raw
        .trim()
        .parse()
        .map_err(|_| ParseError::new(format!("端口不是数字: {raw}")))?;
    if value == 0 || value > u16::MAX as u32 {
        return Err(ParseError::new(format!("端口超出范围: {value}")));
    }
    Ok(value as u16)
}

/// 拆分 host[:port]。IPv6 必须写成 [addr]:port；缺省端口由 default_port 决定。
fn split_host_port(host_port: &str, default_port: Option<u16>) -> Result<(String, u16)> {
    let text = host_port.trim().trim_end_matches('/');
    if text.is_empty() {
        return Err(ParseError::new("缺少主机名"));
    }
    if text.starts_with('[') {
        let close = text
            .find(']')
            .ok_or_else(|| ParseError::new("IPv6 地址缺少 ]"))?;
        let host = &text[1..close];
        if host.is_empty() {
            return Err(ParseError::new("IPv6 地址为空"));
        }
        let rest = text[close + 1..].trim();
        if rest.is_empty() {
            return match default_port {
                Some(port) => Ok((host.to_string(), port)),
                None => Err(ParseError::new("缺少端口")),
            };
        }
        let port_text = rest
            .strip_prefix(':')
            .ok_or_else(|| ParseError::new("IPv6 地址后缺少冒号端口"))?;
        return Ok((host.to_string(), parse_port(port_text)?));
    }
    if let Some((host, port)) = text.rsplit_once(':') {
        if host.is_empty() {
            return Err(ParseError::new("缺少主机名"));
        }
        if host.contains(':') {
            return Err(ParseError::new("IPv6 地址必须用方括号包裹"));
        }
        return Ok((host.to_string(), parse_port(port)?));
    }
    match default_port {
        Some(port) => Ok((text.to_string(), port)),
        None => Err(ParseError::new("缺少端口")),
    }
}

/// 拆分 host 与“端口表达式”（hy2 允许 443,5000-6000），表达式可为空。
fn split_host_port_expr(host_port: &str) -> Result<(String, String)> {
    let text = host_port.trim().trim_end_matches('/');
    if text.is_empty() {
        return Err(ParseError::new("缺少主机名"));
    }
    if text.starts_with('[') {
        let close = text
            .find(']')
            .ok_or_else(|| ParseError::new("IPv6 地址缺少 ]"))?;
        let host = &text[1..close];
        if host.is_empty() {
            return Err(ParseError::new("IPv6 地址为空"));
        }
        let rest = text[close + 1..].trim();
        if rest.is_empty() {
            return Ok((host.to_string(), String::new()));
        }
        let port = rest
            .strip_prefix(':')
            .ok_or_else(|| ParseError::new("IPv6 地址后缺少冒号端口"))?;
        return Ok((host.to_string(), port.trim().to_string()));
    }
    if let Some((host, port)) = text.rsplit_once(':') {
        if host.is_empty() {
            return Err(ParseError::new("缺少主机名"));
        }
        if host.contains(':') {
            return Err(ParseError::new("IPv6 地址必须用方括号包裹"));
        }
        return Ok((host.to_string(), port.trim().to_string()));
    }
    Ok((text.to_string(), String::new()))
}

fn split_userinfo(authority: &str) -> Result<(String, String)> {
    let (userinfo, host_port) = authority
        .rsplit_once('@')
        .ok_or_else(|| ParseError::new("缺少 user@host 结构"))?;
    if userinfo.is_empty() {
        return Err(ParseError::new("缺少认证信息"));
    }
    Ok((pct_decode(userinfo), host_port.to_string()))
}

fn normalize_port_spec(expr: &str) -> Result<String> {
    if expr.trim().is_empty() {
        return Ok(String::new());
    }
    let mut out = Vec::new();
    for part in expr.split(',').map(str::trim).filter(|p| !p.is_empty()) {
        let normalized = part.replace('-', ":");
        let mut ranges = normalized.split(':').map(str::trim);
        let Some(first) = ranges.next().filter(|s| !s.is_empty()) else {
            continue;
        };
        let Ok(a) = parse_port(first) else {
            continue;
        };
        match ranges.next().filter(|s| !s.is_empty()) {
            Some(second) => {
                if let Ok(b) = parse_port(second) {
                    out.push(format!("{a}:{b}"));
                } else {
                    out.push(a.to_string());
                }
            }
            None => out.push(a.to_string()),
        }
    }
    if out.is_empty() {
        return Err(ParseError::new(format!("端口表达式无效: {expr}")));
    }
    Ok(out.join(","))
}

fn first_port(spec: &str) -> u16 {
    spec.split(',')
        .next()
        .and_then(|s| s.split(':').next())
        .and_then(|s| s.parse().ok())
        .unwrap_or(0)
}

fn parse_bool(value: Option<&str>) -> bool {
    matches!(
        value.map(|v| v.to_ascii_lowercase()).as_deref(),
        Some("1") | Some("true")
    )
}

fn ensure_endpoint(node: &Node) -> Result<()> {
    if node.server.is_empty() {
        return Err(ParseError::new("缺少服务器地址"));
    }
    if node.port == 0 {
        return Err(ParseError::new("缺少端口"));
    }
    Ok(())
}

fn require_nonempty(value: &Option<String>, what: &str) -> Result<()> {
    if value.as_deref().unwrap_or("").is_empty() {
        return Err(ParseError::new(format!("缺少{what}")));
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// vmess
// ---------------------------------------------------------------------------

fn parse_vmess(body: &str) -> Result<Node> {
    let decoded = b64_decode(body.trim())?;
    let value: Value = serde_json::from_str(&decoded)
        .map_err(|e| ParseError::new(format!("vmess JSON 无效: {e}")))?;
    if !value.is_object() {
        return Err(ParseError::new("vmess JSON 顶层必须是对象"));
    }
    let field = |key: &str| json_field(&value, key);
    let server = field("add");
    if server.is_empty() {
        return Err(ParseError::new("vmess 缺少 add"));
    }
    let port = parse_port(&field("port"))?;
    let uuid = field("id");
    if uuid.is_empty() {
        return Err(ParseError::new("vmess 缺少 id"));
    }
    let alter_id = field("aid").parse::<u32>().unwrap_or(0);
    let network_raw = field("net");
    let network = if network_raw.is_empty() {
        "tcp".to_string()
    } else {
        network_raw.to_ascii_lowercase()
    };
    let cipher = field("scy");
    let security = field("tls");
    let mut node = Node {
        protocol: Protocol::VMess,
        name: field("ps"),
        server: server.clone(),
        port,
        uuid: Some(uuid),
        alter_id,
        cipher: Some(if cipher.is_empty() {
            "auto".to_string()
        } else {
            cipher
        }),
        network,
        security: if security.eq_ignore_ascii_case("tls") {
            "tls".to_string()
        } else {
            "none".to_string()
        },
        sni: opt_string(nonempty(field("sni").as_str())).or_else(|| {
            let peer = field("peer");
            opt_string(nonempty(peer.as_str()))
        }),
        fingerprint: opt_string(nonempty(field("fp").as_str())),
        path: opt_string(nonempty(field("path").as_str())),
        host_header: opt_string(nonempty(field("host").as_str())),
        header_type: opt_string(nonempty(field("type").as_str())),
        ..Node::default()
    };
    if node.network.eq_ignore_ascii_case("grpc") && node.service_name.is_none() {
        node.service_name = node.path.clone();
    }
    let alpn = field("alpn");
    if !alpn.is_empty() {
        node.extras.insert("alpn".to_string(), alpn);
    }
    ensure_endpoint(&node)?;
    require_nonempty(&node.uuid, "vmess id")?;
    Ok(node)
}

fn json_field(value: &Value, key: &str) -> String {
    match value.get(key) {
        Some(Value::String(s)) => s.trim().to_string(),
        Some(Value::Number(n)) => n.to_string(),
        _ => String::new(),
    }
}

fn nonempty(value: &str) -> Option<&str> {
    if value.is_empty() {
        None
    } else {
        Some(value)
    }
}

// ---------------------------------------------------------------------------
// vless / trojan / anytls
// ---------------------------------------------------------------------------

fn parse_vless(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (auth, host_port) = split_userinfo(parts.authority)?;
    let (server, port) = split_host_port(&host_port, None)?;
    let mut node = Node {
        protocol: Protocol::VLESS,
        name: parts.name,
        server,
        port,
        uuid: Some(auth),
        ..Node::default()
    };
    apply_common_query(&mut node, &parts.query);
    ensure_endpoint(&node)?;
    require_nonempty(&node.uuid, "vless uuid")?;
    Ok(node)
}

fn parse_trojan(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (password, host_port) = split_userinfo(parts.authority)?;
    let (server, port) = split_host_port(&host_port, None)?;
    let mut node = Node {
        protocol: Protocol::Trojan,
        name: parts.name,
        server,
        port,
        password: Some(password),
        security: "tls".to_string(),
        ..Node::default()
    };
    apply_common_query(&mut node, &parts.query);
    node.security = if node.security.is_empty() {
        "tls".to_string()
    } else {
        node.security
    };
    ensure_endpoint(&node)?;
    require_nonempty(&node.password, "trojan 密码")?;
    Ok(node)
}

fn parse_anytls(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (password, host_port) = split_userinfo(parts.authority)?;
    let (server, port) = split_host_port(&host_port, Some(443))?;
    let mut node = Node {
        protocol: Protocol::AnyTLS,
        name: parts.name,
        server,
        port,
        password: Some(password),
        security: "tls".to_string(),
        ..Node::default()
    };
    apply_common_query(&mut node, &parts.query);
    node.security = "tls".to_string();
    ensure_endpoint(&node)?;
    require_nonempty(&node.password, "anytls 密码")?;
    Ok(node)
}

fn apply_common_query(node: &mut Node, query: &Query) {
    if let Some(value) = qnonempty(query, "type") {
        node.network = value.to_ascii_lowercase();
    }
    if let Some(value) = qnonempty(query, "security") {
        node.security = value.to_ascii_lowercase();
    }
    node.sni = opt_string(qnonempty(query, "sni"))
        .or_else(|| opt_string(qnonempty(query, "peer")))
        .or_else(|| node.sni.take());
    node.fingerprint = opt_string(qnonempty(query, "fp")).or_else(|| node.fingerprint.take());
    if let Some(value) = qnonempty(query, "pbk") {
        node.public_key = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "sid") {
        node.short_id = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "spx") {
        node.spider_x = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "flow") {
        node.flow = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "path") {
        node.path = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "host") {
        node.host_header = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "serviceName") {
        node.service_name = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "headerType") {
        node.header_type = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "encryption") {
        if !value.eq_ignore_ascii_case("none") {
            node.vless_encryption = Some(value.to_string());
        }
    }
    if let Some(value) = qnonempty(query, "pqv") {
        node.mldsa65_verify = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "ech") {
        node.ech_config_list = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "vcn") {
        node.verify_peer_cert_by_name = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "pcs") {
        node.cert_sha256 = Some(value.to_string());
    }
    match node.network.as_str() {
        "kcp" => {
            if let Some(value) = qnonempty(query, "seed") {
                node.kcp_seed = Some(value.to_string());
            }
            if let Some(value) = qnonempty(query, "mtu").and_then(|v| v.parse::<u32>().ok()) {
                if value > 0 {
                    node.kcp_mtu = Some(value);
                }
            }
        }
        "xhttp" => {
            if let Some(value) = qnonempty(query, "mode") {
                node.xhttp_mode = Some(value.to_string());
            }
            if let Some(value) = qnonempty(query, "extra") {
                node.xhttp_extra = Some(value.to_string());
            }
        }
        "grpc" => {
            if let Some(value) = qnonempty(query, "authority") {
                node.grpc_authority = Some(value.to_string());
            }
            if let Some(value) = qnonempty(query, "mode") {
                node.grpc_mode = Some(value.to_string());
            }
        }
        _ => {}
    }
    node.allow_insecure =
        parse_bool(qget(query, "allowInsecure")) || parse_bool(qget(query, "insecure"));
    for (key, value) in query {
        if is_consumed_common(key) {
            continue;
        }
        node.extras.insert(key.clone(), value.clone());
    }
}

fn is_consumed_common(key: &str) -> bool {
    matches!(
        key.to_ascii_lowercase().as_str(),
        "type"
            | "security"
            | "sni"
            | "peer"
            | "fp"
            | "pbk"
            | "sid"
            | "spx"
            | "flow"
            | "path"
            | "host"
            | "servicename"
            | "headertype"
            | "allowinsecure"
            | "insecure"
            | "encryption"
            | "pqv"
            | "ech"
            | "vcn"
            | "pcs"
            | "seed"
            | "mtu"
            | "extra"
            | "authority"
    )
}

// ---------------------------------------------------------------------------
// Shadowsocks
// ---------------------------------------------------------------------------

fn parse_shadowsocks(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let authority = parts.authority;
    let (method, password, host, port) =
        if let Some((userinfo, host_port)) = authority.rsplit_once('@') {
            let userinfo = if userinfo.contains(':') {
                pct_decode(userinfo)
            } else {
                b64_decode(userinfo)?
            };
            let (method, password) = userinfo
                .split_once(':')
                .ok_or_else(|| ParseError::new("ss userinfo 缺少 method:password"))?;
            let (host, port) = split_host_port(host_port, None)?;
            (method.to_string(), password.to_string(), host, port)
        } else {
            let decoded = b64_decode(authority)?;
            let (userinfo, host_port) = decoded
                .rsplit_once('@')
                .ok_or_else(|| ParseError::new("ss 缺少 @host:port"))?;
            let (method, password) = userinfo
                .split_once(':')
                .ok_or_else(|| ParseError::new("ss userinfo 缺少 method:password"))?;
            let (host, port) = split_host_port(host_port, None)?;
            (method.to_string(), password.to_string(), host, port)
        };
    if method.is_empty() {
        return Err(ParseError::new("ss 缺少加密方法"));
    }
    let mut node = Node {
        protocol: Protocol::Shadowsocks,
        name: parts.name,
        server: host,
        port,
        method: Some(method),
        password: Some(password),
        ..Node::default()
    };
    if !parts.query.is_empty() {
        let raw_query = serde_urlencoded_raw(&parts.query);
        node.extras.insert("query".to_string(), raw_query);
    }
    apply_ss_plugin(&mut node, &parts.query);
    ensure_endpoint(&node)?;
    Ok(node)
}

fn serde_urlencoded_raw(query: &Query) -> String {
    // 仅用于排障的原始 query：用解码后的 k=v 重新拼接，避免引入额外依赖。
    query
        .iter()
        .map(|(k, v)| format!("{k}={v}"))
        .collect::<Vec<_>>()
        .join("&")
}

fn apply_ss_plugin(node: &mut Node, query: &Query) {
    let Some(raw) = qnonempty(query, "plugin") else {
        return;
    };
    let mut parts = raw
        .split(';')
        .map(str::trim)
        .filter(|part| !part.is_empty());
    let Some(name) = parts.next() else {
        return;
    };
    node.plugin = Some(name.to_string());
    let mut opts: Vec<String> = parts.map(str::to_string).collect();
    if let Some(extra) = qnonempty(query, "plugin-opts").or_else(|| qnonempty(query, "plugin_opts"))
    {
        opts.extend(
            extra
                .split(';')
                .map(str::trim)
                .filter(|part| !part.is_empty())
                .map(str::to_string),
        );
    }
    if !opts.is_empty() {
        node.plugin_opts = Some(opts.join(";"));
    }
}

// ---------------------------------------------------------------------------
// hysteria2 / tuic
// ---------------------------------------------------------------------------

fn parse_hysteria2(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (auth, host_port) = split_userinfo(parts.authority)?;
    let (host, port_expr) = split_host_port_expr(&host_port)?;
    let port_spec = normalize_port_spec(&port_expr)?;
    let port = if port_spec.is_empty() {
        443
    } else {
        first_port(&port_spec)
    };
    if port == 0 {
        return Err(ParseError::new("hy2 端口无效"));
    }
    let query = &parts.query;
    let mut node = Node {
        protocol: Protocol::Hysteria2,
        name: parts.name,
        server: host,
        port,
        password: Some(auth),
        security: "tls".to_string(),
        sni: opt_string(qnonempty(query, "sni")).or_else(|| opt_string(qnonempty(query, "peer"))),
        allow_insecure: parse_bool(qget(query, "insecure"))
            || parse_bool(qget(query, "allowinsecure")),
        ..Node::default()
    };
    if !port_spec.is_empty() {
        node.extras.insert("server_ports".to_string(), port_spec);
    }
    if let Some(value) = qnonempty(query, "pinSHA256") {
        node.cert_sha256 = Some(value.to_string());
    }
    if let Some(value) = qnonempty(query, "ech") {
        node.ech_config_list = Some(value.to_string());
    }
    for key in [
        "obfs",
        "obfs-password",
        "alpn",
        "mport",
        "pinsha256",
        "up",
        "down",
    ] {
        if let Some(value) = qnonempty(query, key) {
            node.extras.insert(key.to_string(), value.to_string());
        }
    }
    if qget(query, "obfs")
        .map(|v| v.eq_ignore_ascii_case("gecko"))
        .unwrap_or(false)
    {
        node.gecko_min_packet_size = Some(
            qnonempty(query, "minPacketSize")
                .and_then(|v| v.parse::<u32>().ok())
                .filter(|v| *v > 0)
                .unwrap_or(512),
        );
        node.gecko_max_packet_size = Some(
            qnonempty(query, "maxPacketSize")
                .and_then(|v| v.parse::<u32>().ok())
                .filter(|v| *v > 0)
                .unwrap_or(1200),
        );
    }
    ensure_endpoint(&node)?;
    require_nonempty(&node.password, "hy2 认证密码")?;
    Ok(node)
}

fn parse_tuic(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (auth, host_port) = split_userinfo(parts.authority)?;
    let (host, port) = split_host_port(&host_port, None)?;
    let query = &parts.query;
    let (uuid, password) = match auth.split_once(':') {
        Some((uuid, password)) => (uuid.to_string(), password.to_string()),
        None => (auth, qnonempty(query, "password").unwrap_or("").to_string()),
    };
    if uuid.is_empty() {
        return Err(ParseError::new("tuic 缺少 uuid"));
    }
    if password.is_empty() {
        return Err(ParseError::new("tuic 缺少密码"));
    }
    let mut node = Node {
        protocol: Protocol::TUIC,
        name: parts.name,
        server: host,
        port,
        uuid: Some(uuid),
        password: Some(password),
        security: "tls".to_string(),
        sni: opt_string(qnonempty(query, "sni")).or_else(|| opt_string(qnonempty(query, "peer"))),
        allow_insecure: parse_bool(qget(query, "insecure"))
            || parse_bool(qget(query, "allowinsecure")),
        ..Node::default()
    };
    for key in ["congestion_control", "udp_relay_mode", "alpn"] {
        if let Some(value) = qnonempty(query, key) {
            node.extras.insert(key.to_string(), value.to_string());
        }
    }
    ensure_endpoint(&node)?;
    Ok(node)
}

// ---------------------------------------------------------------------------
// naive / wireguard / socks
// ---------------------------------------------------------------------------

fn parse_naive(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (userinfo, host_port) = split_userinfo(parts.authority)?;
    let (username, password) = match userinfo.split_once(':') {
        Some((user, pass)) => (user.to_string(), pass.to_string()),
        None => (userinfo, String::new()),
    };
    if username.is_empty() {
        return Err(ParseError::new("naive 缺少用户名"));
    }
    let (server, port) = split_host_port(&host_port, Some(443))?;
    if server.is_empty() {
        return Err(ParseError::new("naive 缺少服务器地址"));
    }
    let query = &parts.query;
    let mut node = Node {
        protocol: Protocol::Naive,
        name: parts.name,
        server: server.clone(),
        port,
        username: Some(username),
        password: Some(password),
        security: "tls".to_string(),
        sni: opt_string(qnonempty(query, "sni")).or(Some(server)),
        ..Node::default()
    };
    if node.name.is_empty() {
        node.name = format!("naive {}", node.endpoint());
    }
    ensure_endpoint(&node)?;
    Ok(node)
}

fn parse_wireguard(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let (private_key, host_port) = split_userinfo(parts.authority)?;
    let (server, port) = split_host_port(&host_port, Some(51820))?;
    let query = &parts.query;
    let mut node = Node {
        protocol: Protocol::WireGuard,
        name: parts.name,
        server: server.clone(),
        port,
        password: Some(private_key),
        security: "none".to_string(),
        network: "udp".to_string(),
        wg_public_key: opt_string(qnonempty(query, "publickey")),
        wg_preshared_key: opt_string(qnonempty(query, "presharedkey")),
        wg_address: opt_string(qnonempty(query, "address")),
        wg_reserved: opt_string(qnonempty(query, "reserved")),
        wg_dns: opt_string(qnonempty(query, "dns")),
        ..Node::default()
    };
    if let Some(value) = qnonempty(query, "mtu").and_then(|v| v.parse::<u32>().ok()) {
        if value > 0 {
            node.wg_mtu = Some(value);
        }
    }
    if let Some(value) = qnonempty(query, "keepalive").and_then(|v| v.parse::<u32>().ok()) {
        if value > 0 {
            node.wg_keepalive = Some(value);
        }
    }
    if node.name.is_empty() {
        node.name = format!("WireGuard {}", node.endpoint());
    }
    ensure_endpoint(&node)?;
    require_nonempty(&node.password, "WireGuard 私钥")?;
    Ok(node)
}

fn parse_socks(body: &str) -> Result<Node> {
    let parts = split_link_parts(body);
    let authority = parts.authority;
    let mut node = Node {
        protocol: Protocol::Socks,
        name: parts.name,
        security: "none".to_string(),
        ..Node::default()
    };
    let host_port: String;
    if let Some((userinfo_raw, host_port_raw)) = authority.rsplit_once('@') {
        host_port = host_port_raw.to_string();
        let mut userinfo = pct_decode(userinfo_raw);
        if !userinfo.contains(':') {
            if let Ok(decoded) = b64_decode(&userinfo) {
                if decoded.contains(':') {
                    userinfo = decoded;
                }
            }
        }
        match userinfo.split_once(':') {
            Some((user, pass)) => {
                node.username = Some(user.to_string());
                node.password = Some(pass.to_string());
            }
            None if !userinfo.is_empty() => node.username = Some(userinfo),
            None => {}
        }
    } else if let Ok(decoded) = b64_decode(authority) {
        // 旧写法：socks://base64("user:pass@host:port")
        let (userinfo, host_port_raw) = decoded
            .rsplit_once('@')
            .ok_or_else(|| ParseError::new("socks 旧格式缺少 @host:port"))?;
        host_port = host_port_raw.to_string();
        match userinfo.split_once(':') {
            Some((user, pass)) => {
                node.username = Some(user.to_string());
                node.password = Some(pass.to_string());
            }
            None => node.username = Some(userinfo.to_string()),
        }
    } else {
        // 标准无认证写法：socks5://host:port
        host_port = authority.to_string();
    }
    let (host, port_expr) = split_host_port_expr(&host_port)?;
    node.server = host;
    node.port = if port_expr.is_empty() {
        1080
    } else {
        parse_port(&port_expr)?
    };
    if node.name.is_empty() {
        node.name = node.endpoint();
    }
    if !parts.query.is_empty() {
        node.extras
            .insert("query".to_string(), serde_urlencoded_raw(&parts.query));
    }
    ensure_endpoint(&node)?;
    Ok(node)
}

// ---------------------------------------------------------------------------
// 测试
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    // 测试样例说明：
    //   - 字段语义与 Windows 端约定一致；
    //   - 主机/密码均为示例或假值，不指向真实服务。

    #[test]
    fn parses_vmess_json_base64() {
        let payload = r#"{"v":"2","ps":"vmess-sample","add":"vm.example.com","port":"443","id":"11111111-1111-4111-8111-111111111111","aid":"0","scy":"auto","net":"ws","type":"none","host":"vm.example.com","path":"/ws","tls":"tls","sni":"vm.example.com","alpn":"h2,http/1.1"}"#;
        let link = format!("vmess://{}", STANDARD.encode(payload));
        let node = parse_share_link(&link).expect("vmess sample should parse");
        assert_eq!(node.protocol, Protocol::VMess);
        assert_eq!(node.server, "vm.example.com");
        assert_eq!(node.port, 443);
        assert_eq!(node.network, "ws");
        assert_eq!(node.path.as_deref(), Some("/ws"));
        assert_eq!(node.security, "tls");
        assert_eq!(
            node.extras.get("alpn").map(String::as_str),
            Some("h2,http/1.1")
        );
    }

    #[test]
    fn parses_vless_ipv6_reality_grpc() {
        let link = "vless://11111111-1111-4111-8111-111111111111@[2001:db8::1]:443?encryption=none&security=reality&sni=example.com&fp=chrome&pbk=pubkey&sid=0123&type=grpc&serviceName=grpcsvc&flow=xtls-rprx-vision#%E6%B5%8B%E8%AF%95%E8%8A%82%E7%82%B9";
        let node = parse_share_link(link).expect("vless sample should parse");
        assert_eq!(node.protocol, Protocol::VLESS);
        assert_eq!(node.server, "2001:db8::1");
        assert_eq!(node.port, 443);
        assert_eq!(node.name, "测试节点");
        assert_eq!(node.security, "reality");
        assert_eq!(node.network, "grpc");
        assert_eq!(node.service_name.as_deref(), Some("grpcsvc"));
        assert_eq!(node.flow.as_deref(), Some("xtls-rprx-vision"));
        assert_eq!(node.public_key.as_deref(), Some("pubkey"));
    }

    #[test]
    fn parses_trojan_and_percent_encoded_password() {
        let link = "trojan://p%40ss@trojan.example.com:443?security=tls&sni=t.example.com&allowInsecure=1#Trojan";
        let node = parse_share_link(link).expect("trojan sample should parse");
        assert_eq!(node.protocol, Protocol::Trojan);
        assert_eq!(node.password.as_deref(), Some("p@ss"));
        assert_eq!(node.security, "tls");
        assert!(node.allow_insecure);
        assert_eq!(node.name, "Trojan");
    }

    #[test]
    fn parses_ss_sip002_base64_userinfo_with_plugin() {
        let userinfo = STANDARD.encode("aes-256-gcm:p@ss");
        let link = format!(
            "ss://{userinfo}@ss.example.com:8388?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com#SS"
        );
        let node = parse_share_link(&link).expect("ss SIP002 sample should parse");
        assert_eq!(node.protocol, Protocol::Shadowsocks);
        assert_eq!(node.method.as_deref(), Some("aes-256-gcm"));
        assert_eq!(node.password.as_deref(), Some("p@ss"));
        assert_eq!(node.plugin.as_deref(), Some("obfs-local"));
        assert_eq!(
            node.plugin_opts.as_deref(),
            Some("obfs=http;obfs-host=bing.com")
        );
    }

    #[test]
    fn parses_ss_legacy_whole_body_base64() {
        let payload = STANDARD.encode("chacha20-ietf-poly1305:secret@1.2.3.4:8389");
        let link = format!("ss://{payload}#Old");
        let node = parse_share_link(&link).expect("legacy ss sample should parse");
        assert_eq!(node.method.as_deref(), Some("chacha20-ietf-poly1305"));
        assert_eq!(node.password.as_deref(), Some("secret"));
        assert_eq!(node.server, "1.2.3.4");
        assert_eq!(node.port, 8389);
        assert_eq!(node.name, "Old");
    }

    #[test]
    fn parses_hysteria2_port_hop_and_gecko() {
        let link = "hysteria2://secret@hy.example.com:443,5000-6000/?sni=hy.example.com&insecure=1&obfs=gecko&minPacketSize=640&maxPacketSize=1400#HY2";
        let node = parse_share_link(link).expect("hy2 sample should parse");
        assert_eq!(node.protocol, Protocol::Hysteria2);
        assert_eq!(node.port, 443);
        assert_eq!(
            node.extras.get("server_ports").map(String::as_str),
            Some("443,5000:6000")
        );
        assert_eq!(node.gecko_min_packet_size, Some(640));
        assert_eq!(node.gecko_max_packet_size, Some(1400));
        assert!(node.allow_insecure);
        assert_eq!(node.name, "HY2");
    }

    #[test]
    fn parses_tuic_uuid_password() {
        let link = "tuic://uuid-1:pass@tuic.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=t.example.com&insecure=1#TUIC";
        let node = parse_share_link(link).expect("tuic sample should parse");
        assert_eq!(node.protocol, Protocol::TUIC);
        assert_eq!(node.uuid.as_deref(), Some("uuid-1"));
        assert_eq!(node.password.as_deref(), Some("pass"));
        assert_eq!(
            node.extras.get("congestion_control").map(String::as_str),
            Some("bbr")
        );
        assert!(node.allow_insecure);
    }

    #[test]
    fn parses_anytls_default_port() {
        let node = parse_share_link("anytls://secret@any.example.com?sni=any.example.com#AnyTLS")
            .expect("anytls sample should parse");
        assert_eq!(node.protocol, Protocol::AnyTLS);
        assert_eq!(node.port, 443);
        assert_eq!(node.security, "tls");
    }

    #[test]
    fn parses_socks5_auth() {
        let node = parse_share_link("socks5://user:p%40ss@127.0.0.1:1080#Socks")
            .expect("socks sample should parse");
        assert_eq!(node.protocol, Protocol::Socks);
        assert_eq!(node.username.as_deref(), Some("user"));
        assert_eq!(node.password.as_deref(), Some("p@ss"));
        assert_eq!(node.port, 1080);

        let no_auth = parse_share_link("socks5://127.0.0.1:1081").expect("no-auth socks");
        assert_eq!(no_auth.protocol, Protocol::Socks);
        assert!(no_auth.username.is_none());
        assert_eq!(no_auth.port, 1081);
    }

    #[test]
    fn parses_naive_https_and_wireguard() {
        let naive = parse_share_link(
            "naive+https://user:p%40ss@naive.example.com:443?sni=naive.example.com#Naive",
        )
        .expect("naive sample should parse");
        assert_eq!(naive.protocol, Protocol::Naive);
        assert_eq!(naive.username.as_deref(), Some("user"));
        assert_eq!(naive.password.as_deref(), Some("p@ss"));
        assert_eq!(naive.sni.as_deref(), Some("naive.example.com"));

        let wg = parse_share_link("wireguard://private-key@wg.example.com:51820?publickey=pub&address=10.0.0.2%2F32&mtu=1280#WG")
            .expect("wireguard sample should parse");
        assert_eq!(wg.protocol, Protocol::WireGuard);
        assert_eq!(wg.wg_public_key.as_deref(), Some("pub"));
        assert_eq!(wg.wg_address.as_deref(), Some("10.0.0.2/32"));
        assert_eq!(wg.wg_mtu, Some(1280));
        assert_eq!(wg.network, "udp");
    }

    #[test]
    fn rejects_bad_input_without_panicking() {
        let bad = [
            "",
            "   ",
            "hello",
            "vless://uuid@host",
            "vless://uuid@host:0",
            "vmess://not-base64",
            "vmess://e30=",
            "ss://not-base64",
            "hy2://secret@host:70000",
            "tuic://uuid@host:443",
            "socks5://host:99999",
            "vless://uuid@[2001:db8::1:443",
            "vless://uuid@host:443\n#name",
        ];
        for link in bad {
            let result = std::panic::catch_unwind(|| parse_share_link(link));
            assert!(result.is_ok(), "parser panicked on {link:?}");
            assert!(
                result.unwrap().is_err(),
                "parser accepted bad link {link:?}"
            );
        }
    }

    #[test]
    fn json_round_trip() {
        let node = parse_share_link(
            "vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls&sni=example.com#JSON",
        )
        .unwrap();
        let json = node.to_json().unwrap();
        let back = Node::from_json(&json).unwrap();
        assert_eq!(node, back);
        assert!(json.contains("\"protocol\":\"vless\""));
        assert!(json.contains("\"port\":443"));
    }

    #[test]
    fn parse_many_skips_bad_lines() {
        let nodes = parse_many([
            "vless://uuid@a.example.com:443#A",
            "garbage",
            "trojan://pass@b.example.com:443#B",
        ]);
        assert_eq!(nodes.len(), 2);
        assert_eq!(nodes[0].name, "A");
        assert_eq!(nodes[1].name, "B");
    }

    #[test]
    fn vmess_grpc_path_becomes_service_name() {
        let payload = r#"{"ps":"g","add":"g.example.com","port":443,"id":"uuid","net":"grpc","path":"svc","tls":"tls"}"#;
        let link = format!("vmess://{}", STANDARD.encode(payload));
        let node = parse_share_link(&link).unwrap();
        assert_eq!(node.service_name.as_deref(), Some("svc"));
    }

    #[test]
    fn rejects_balanced_but_bad_ports() {
        assert!(parse_share_link("vless://uuid@host:65536").is_err());
        assert!(parse_share_link("hysteria2://pass@host:0").is_err());
        assert!(parse_share_link("ss://YWVzLTI1Ni1nY206cGFzcw==@host:0").is_err());
    }
}
