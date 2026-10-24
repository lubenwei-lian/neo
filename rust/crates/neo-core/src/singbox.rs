//! sing-box 1.14.x 配置生成（把 node + route + dns 组装成完整 JSON）。
//!
//! 参考实现（只读）：`neo/src/Neo.Core/Core/SingBoxConfigBuilder.cs`（版本）。
//! 与 版逐字段对齐的部分：
//! - 日志级别映射（none/error→error、warning→warn、info、debug）；
//! - 各协议 outbound 字段（vless 的 flow / vmess 的 alter_id / ss 的 SIP003 插件 /
//!   hy2 的 obfs+server_ports / tuic 的 congestion_control / anytls / naive / socks）；
//! - TLS（server_name 回退顺序、insecure、utls 指纹、REALITY、ALPN、ECH）；
//! - 传输（ws/grpc/http/h2/httpupgrade；xhttp/kcp/quic 明确报错）；
//! - DNS（TUN 只下发 IPv4 fakeip、A 查询走 fakeip、私网/NCSI/HTTPS-SVCB/单标签直连）；
//! - route（TUN 的 sniff + hijack-dns、进程直连、ads/private/NCSI/Steam/cn、rule_set 定义）；
//! - experimental.clash_api（127.0.0.1:21811 / neo-local-stats）。
//!
//! 与 版的**有意差异**（均可在选项里关掉或已在文档记录）：
//! 1. 非 TUN 入口用 `mixed`（任务要求），用 socks + http 两个入口；
//! 2. 默认生成 selector / urltest 策略组（Windows 端只生成单个 `proxy` 出站）；
//!    `emit_groups = false` 时退回 同构（final = proxy）。
//! 3. 未提供 `rule_set_dir` 时不引用/不定义规则集，保证配置在任意机器都能
//!    `sing-box check` 通过；提供后按 的定义输出三个本地 .srs。
//! 4. WireGuard 用 sing-box 1.11+ 的 `endpoints`（与 一致），但要求它是唯一节点
//!    （策略组无法引用 endpoint tag）。

use std::collections::BTreeSet;
use std::fmt;
use std::path::{Path, PathBuf};

use serde_json::{json, Map, Value};

use crate::dns::{DnsPlan, DnsServer};
use crate::node::{Node, Protocol};
use crate::route::{Outbound, RoutePlan, RouteRule};

/// 本地 mixed 入口默认端口（与Windows 端用户端口一致）。
pub const DEFAULT_MIXED_PORT: u16 = 21808;
const DEFAULT_HTTP_PORT: u16 = 21809;
/// Clash API 端口（Windows 端固定值）。
pub const DEFAULT_CLASH_API_PORT: u16 = 21811;
/// Clash API 密钥（Windows 端固定值）。
pub const DEFAULT_CLASH_API_SECRET: &str = "neo-local-stats";
/// 主选择组（selector）。
pub const GROUP_SELECT: &str = "🚀 节点选择";
/// 自动测速组（urltest）。
pub const GROUP_AUTO: &str = "♻️ 自动选择";
/// 兜底组（selector），route.final 指向它。
pub const GROUP_FINAL: &str = "🐟 漏网之鱼";
/// urltest 探测地址（与Android 端恢复配置一致）。
pub const URLTEST_URL: &str = "http://cp.cloudflare.com/generate_204";
/// 直连出站 tag（与 一致）。
pub const DIRECT_OUTBOUND_TAG: &str = "direct";
/// 单节点时的代理出站 tag（与 一致）。
pub const PROXY_OUTBOUND_TAG: &str = "proxy";

const DNS_DIRECT_TAG: &str = "dns-direct";
const DNS_REMOTE_TAG: &str = "dns-remote";
const DNS_BOOTSTRAP_TAG: &str = "dns-bootstrap";
const MIXED_INBOUND_TAG: &str = "mixed-in";
const SOCKS_INBOUND_TAG: &str = "socks-in";
const HTTP_INBOUND_TAG: &str = "http-in";
const TUN_INBOUND_TAG: &str = "tun-in";

/// 代理模式（与Windows 端 state.json 的 ProxyMode 同名）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ProxyMode {
    #[default]
    Rule,
    Global,
    Direct,
}

impl ProxyMode {
    pub fn as_str(self) -> &'static str {
        match self {
            ProxyMode::Rule => "rule",
            ProxyMode::Global => "global",
            ProxyMode::Direct => "direct",
        }
    }

    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "rule" => Some(ProxyMode::Rule),
            "global" => Some(ProxyMode::Global),
            "direct" => Some(ProxyMode::Direct),
            _ => None,
        }
    }
}

/// 配置生成选项。默认值 = Windows 端「规则模式 + 绕过大陆/内网 + 拦截广告」。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SingBoxOptions {
    /// true = TUN 入口（并生成 IPv4 fakeip）；false = 仅 mixed 入口。
    pub tun: bool,
    pub mixed_port: u16,
    /// `socks_http_inbounds=true` 时使用 socks-in + http-in（与 Windows 端同构），
    /// 否则用 mixed-in（默认）。需要与 Windows 端中继端口并存时，必须用前者，否则中继端口无监听。
    pub http_port: u16,
    pub socks_http_inbounds: bool,
    /// true 时 mixed 监听 0.0.0.0（对应Windows 端 allow_lan）。
    pub allow_lan: bool,
    /// none/error/warning/info/debug（大小写不敏感）。
    pub log_level: String,
    pub proxy_mode: ProxyMode,
    pub bypass_private: bool,
    pub bypass_cn: bool,
    pub block_ads: bool,
    /// TUN 下直连的游戏进程名（如 TslGame.exe / cs2.exe）。
    pub game_direct_processes: Vec<String>,
    pub clash_api_port: u16,
    pub clash_api_secret: String,
    /// 用户 DNS：IP、域名、`https://`、`tls://`（对应Windows 端 DnsServer）。
    pub dns_server: String,
    /// Windows 端 runtime 目录（内部拼 `rule-sets/xxx.srs`）。
    /// None = 不引用/不定义规则集，配置可独立 check。
    pub rule_set_dir: Option<PathBuf>,
    /// 是否生成 selector / urltest 策略组（默认 true；false = 同构）。
    pub emit_groups: bool,
}

impl Default for SingBoxOptions {
    fn default() -> Self {
        Self {
            tun: false,
            mixed_port: DEFAULT_MIXED_PORT,
            http_port: DEFAULT_HTTP_PORT,
            socks_http_inbounds: false,
            allow_lan: false,
            log_level: "warning".into(),
            proxy_mode: ProxyMode::Rule,
            bypass_private: true,
            bypass_cn: true,
            block_ads: true,
            game_direct_processes: Vec::new(),
            clash_api_port: DEFAULT_CLASH_API_PORT,
            clash_api_secret: DEFAULT_CLASH_API_SECRET.into(),
            dns_server: "223.5.5.5".into(),
            rule_set_dir: None,
            emit_groups: true,
        }
    }
}

/// 配置生成错误：每一种都给出可执行的下一步，而不是静默生成坏配置。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConfigError {
    NoNodes,
    InvalidNode { name: String, reason: String },
    UnsupportedProtocol { name: String, protocol: String },
    VlessEncryption { name: String, encryption: String },
    UnsupportedTransport { name: String, network: String },
    WireGuardRequiresSingleNode { name: String },
    GroupsRequiredForMultipleNodes { count: usize },
    InvalidDnsServer { value: String, reason: String },
    Serialize(String),
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ConfigError::NoNodes => f.write_str("没有可用节点：至少要有一个有效节点才能生成配置"),
            ConfigError::InvalidNode { name, reason } => {
                write!(f, "节点「{name}」无效：{reason}")
            }
            ConfigError::UnsupportedProtocol { name, protocol } => write!(
                f,
                "节点「{name}」的协议 {protocol} 无法生成 sing-box 配置（未知协议或尚未建模）"
            ),
            ConfigError::VlessEncryption { name, encryption } => write!(
                f,
                "节点「{name}」使用 VLESS 后量子加密（encryption={encryption}），sing-box 1.14 不支持该字段；\
                 请改用 xray 内核（关闭 TUN）"
            ),
            ConfigError::UnsupportedTransport { name, network } => write!(
                f,
                "节点「{name}」的传输方式 {network} 只有 xray 内核支持，sing-box 无法承载；\
                 请关闭 TUN 后改用 xray"
            ),
            ConfigError::WireGuardRequiresSingleNode { name } => write!(
                f,
                "WireGuard 节点「{name}」只能作为唯一节点生成（sing-box 用 endpoints 而非 outbounds，\
                 策略组无法引用 endpoint）"
            ),
            ConfigError::GroupsRequiredForMultipleNodes { count } => write!(
                f,
                "多节点（{count} 个）必须启用策略组（emit_groups=true）；关闭策略组时只能生成单节点配置"
            ),
            ConfigError::InvalidDnsServer { value, reason } => {
                write!(f, "DNS 地址「{value}」无效：{reason}")
            }
            ConfigError::Serialize(message) => write!(f, "配置 JSON 序列化失败：{message}"),
        }
    }
}

impl std::error::Error for ConfigError {}

/// 生成完整 sing-box 配置（serde_json::Value）。
pub fn build_config(nodes: &[Node], opts: &SingBoxOptions) -> Result<Value, ConfigError> {
    if nodes.is_empty() {
        return Err(ConfigError::NoNodes);
    }
    for node in nodes {
        validate_node(node)?;
    }
    let wireguard = nodes.iter().any(|n| n.protocol == Protocol::WireGuard);
    if wireguard {
        if nodes.len() != 1 {
            let name = nodes
                .iter()
                .find(|n| n.protocol == Protocol::WireGuard)
                .map(|n| display_name(n, 0))
                .unwrap_or_default();
            return Err(ConfigError::WireGuardRequiresSingleNode { name });
        }
    } else if !opts.emit_groups && nodes.len() > 1 {
        return Err(ConfigError::GroupsRequiredForMultipleNodes { count: nodes.len() });
    }

    let tags = assign_tags(nodes);
    let groups = opts.emit_groups && !wireguard;

    let proxy_anchor = if groups {
        GROUP_SELECT.to_string()
    } else {
        tags.first().cloned().unwrap_or_else(|| PROXY_OUTBOUND_TAG.to_string())
    };
    let route_final = match opts.proxy_mode {
        ProxyMode::Direct => Outbound::Direct,
        ProxyMode::Rule | ProxyMode::Global => {
            if groups {
                Outbound::Named(GROUP_FINAL)
            } else {
                Outbound::Proxy
            }
        }
    };

    let mut outbounds: Vec<Value> = Vec::new();
    let mut endpoints: Vec<Value> = Vec::new();
    for (node, tag) in nodes.iter().zip(tags.iter()) {
        if node.protocol == Protocol::WireGuard {
            endpoints.push(build_wireguard_endpoint(node, tag)?);
        } else {
            outbounds.push(Value::Object(build_outbound(node, tag)?));
        }
    }
    outbounds.push(json!({ "type": "direct", "tag": DIRECT_OUTBOUND_TAG }));
    if groups {
        outbounds.extend(build_groups(&tags));
    }

    let dns = build_dns(opts, &proxy_anchor)?;
    let route = build_route(opts, route_final);
    let inbounds = build_inbounds(opts);

    let mut root = Map::new();
    root.insert(
        "log".into(),
        json!({ "level": map_log_level(&opts.log_level), "timestamp": true }),
    );
    root.insert("dns".into(), dns);
    root.insert("inbounds".into(), inbounds);
    root.insert("outbounds".into(), Value::Array(outbounds));
    root.insert("route".into(), route);
    root.insert(
        "experimental".into(),
        json!({
            "clash_api": {
                "external_controller": format!("127.0.0.1:{}", opts.clash_api_port),
                "secret": opts.clash_api_secret,
            }
        }),
    );
    if !endpoints.is_empty() {
        root.insert("endpoints".into(), Value::Array(endpoints));
    }
    Ok(Value::Object(root))
}

/// 生成 JSON 文本（pretty，末尾带换行）。
pub fn build_config_json(nodes: &[Node], opts: &SingBoxOptions) -> Result<String, ConfigError> {
    let value = build_config(nodes, opts)?;
    let mut text = serde_json::to_string_pretty(&value)
        .map_err(|error| ConfigError::Serialize(error.to_string()))?;
    text.push('\n');
    Ok(text)
}

/// 日志级别映射（与 `MapLevel` 一致）。
pub fn map_log_level(level: &str) -> &'static str {
    match level.trim().to_ascii_lowercase().as_str() {
        "none" | "error" => "error",
        "warning" | "warn" => "warn",
        "info" => "info",
        "debug" => "debug",
        _ => "warn",
    }
}

fn validate_node(node: &Node) -> Result<(), ConfigError> {
    let name = display_name(node, 0);
    if node.server.trim().is_empty() {
        return Err(ConfigError::InvalidNode { name, reason: "缺少服务器地址".into() });
    }
    if node.port == 0 {
        return Err(ConfigError::InvalidNode { name, reason: "缺少端口".into() });
    }
    let require = |value: &Option<String>, field: &str| -> Result<(), ConfigError> {
        if value.as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none() {
            return Err(ConfigError::InvalidNode { name: display_name(node, 0), reason: format!("缺少 {field}") });
        }
        Ok(())
    };
    match node.protocol {
        Protocol::Unknown => {
            return Err(ConfigError::UnsupportedProtocol {
                name,
                protocol: "unknown".into(),
            });
        }
        Protocol::VLESS | Protocol::VMess | Protocol::TUIC => {
            require(&node.uuid, "uuid")?;
            let uuid = node.uuid.as_deref().unwrap_or("").trim();
            if !is_uuid(uuid) {
                return Err(ConfigError::InvalidNode {
                    name,
                    reason: format!("uuid 格式无效（sing-box 要求 36 位 UUID，收到 {uuid}）"),
                });
            }
        }
        Protocol::Trojan | Protocol::Shadowsocks | Protocol::Hysteria2 | Protocol::AnyTLS | Protocol::Naive => {
            require(&node.password, "密码")?
        }
        Protocol::Socks | Protocol::WireGuard => {}
    }
    if node.protocol == Protocol::WireGuard {
        require(&node.password, "私钥")?;
        require(&node.wg_public_key, "对端公钥")?;
    }
    Ok(())
}

fn is_uuid(raw: &str) -> bool {
    if raw.len() == 36 {
        raw.chars().enumerate().all(|(index, ch)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                ch == '-'
            } else {
                ch.is_ascii_hexdigit()
            }
        })
    } else if raw.len() == 32 {
        raw.chars().all(|ch| ch.is_ascii_hexdigit())
    } else {
        false
    }
}

fn display_name(node: &Node, index: usize) -> String {
    let trimmed = node.name.trim();
    if trimmed.is_empty() {
        format!("节点{}", index + 1)
    } else {
        trimmed.to_string()
    }
}

fn assign_tags(nodes: &[Node]) -> Vec<String> {
    if nodes.len() == 1 {
        return vec![PROXY_OUTBOUND_TAG.to_string()];
    }
    let mut used: BTreeSet<String> = BTreeSet::new();
    used.insert(DIRECT_OUTBOUND_TAG.to_string());
    used.insert(PROXY_OUTBOUND_TAG.to_string());
    used.insert(GROUP_SELECT.to_string());
    used.insert(GROUP_AUTO.to_string());
    used.insert(GROUP_FINAL.to_string());
    nodes
        .iter()
        .enumerate()
        .map(|(index, node)| {
            let base = sanitize_tag(&display_name(node, index));
            let mut candidate = base.clone();
            let mut serial = 2u32;
            while used.contains(&candidate) {
                candidate = format!("{base} #{serial}");
                serial += 1;
            }
            used.insert(candidate.clone());
            candidate
        })
        .collect()
}

fn sanitize_tag(raw: &str) -> String {
    let cleaned: String = raw
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect();
    let trimmed = cleaned.trim();
    if trimmed.is_empty() {
        "节点".to_string()
    } else {
        trimmed.to_string()
    }
}

fn strip_brackets(host: &str) -> String {
    let h = host.trim();
    if h.len() >= 2 && h.starts_with('[') && h.ends_with(']') {
        h[1..h.len() - 1].to_string()
    } else {
        h.to_string()
    }
}

fn extra<'a>(node: &'a Node, key: &str) -> Option<&'a str> {
    node.extras
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(key))
        .map(|(_, v)| v.as_str())
        .filter(|v| !v.is_empty())
}

fn build_outbound(node: &Node, tag: &str) -> Result<Map<String, Value>, ConfigError> {
    let mut o = Map::new();
    o.insert("tag".into(), json!(tag));
    o.insert("server".into(), json!(strip_brackets(&node.server)));
    o.insert("server_port".into(), json!(node.port));
    o.insert("domain_resolver".into(), json!(DNS_DIRECT_TAG));

    match node.protocol {
        Protocol::VLESS => {
            if let Some(enc) = node
                .vless_encryption
                .as_deref()
                .map(str::trim)
                .filter(|v| !v.is_empty() && !v.eq_ignore_ascii_case("none"))
            {
                return Err(ConfigError::VlessEncryption {
                    name: display_name(node, 0),
                    encryption: enc.to_string(),
                });
            }
            o.insert("type".into(), json!("vless"));
            o.insert("uuid".into(), json!(node.uuid.as_deref().unwrap_or("")));
            if let Some(flow) = node.flow.as_deref().filter(|v| !v.is_empty()) {
                o.insert("flow".into(), json!(flow));
            }
            apply_tls(&mut o, node, false);
            apply_transport(&mut o, node)?;
        }
        Protocol::VMess => {
            o.insert("type".into(), json!("vmess"));
            o.insert("uuid".into(), json!(node.uuid.as_deref().unwrap_or("")));
            o.insert("alter_id".into(), json!(node.alter_id));
            let security = node
                .cipher
                .as_deref()
                .map(str::trim)
                .filter(|v| !v.is_empty())
                .unwrap_or("auto");
            o.insert("security".into(), json!(security));
            apply_tls(&mut o, node, false);
            apply_transport(&mut o, node)?;
        }
        Protocol::Trojan => {
            o.insert("type".into(), json!("trojan"));
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            apply_tls(&mut o, node, true);
            apply_transport(&mut o, node)?;
        }
        Protocol::Shadowsocks => {
            o.insert("type".into(), json!("shadowsocks"));
            let method = node
                .method
                .as_deref()
                .map(str::trim)
                .filter(|v| !v.is_empty())
                .unwrap_or("aes-128-gcm");
            o.insert("method".into(), json!(method));
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            if let Some(plugin) = node.plugin.as_deref().filter(|v| !v.is_empty()) {
                o.insert("plugin".into(), json!(plugin));
                if let Some(opts) = node.plugin_opts.as_deref().filter(|v| !v.is_empty()) {
                    o.insert("plugin_opts".into(), json!(opts));
                }
            }
        }
        Protocol::Hysteria2 => {
            o.insert("type".into(), json!("hysteria2"));
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            apply_tls(&mut o, node, true);
            if let Some(raw_obfs) = extra(node, "obfs") {
                let is_gecko = raw_obfs.eq_ignore_ascii_case("gecko")
                    || node.gecko_min_packet_size.is_some()
                    || node.gecko_max_packet_size.is_some();
                let mut obfs = Map::new();
                obfs.insert(
                    "type".into(),
                    json!(if is_gecko { "gecko" } else { raw_obfs }),
                );
                if let Some(password) = extra(node, "obfs-password") {
                    obfs.insert("password".into(), json!(password));
                }
                if is_gecko {
                    let min = node.gecko_min_packet_size.filter(|v| *v > 0).unwrap_or(512);
                    let max = node.gecko_max_packet_size.filter(|v| *v > 0).unwrap_or(1200);
                    obfs.insert("min_packet_size".into(), json!(min));
                    obfs.insert("max_packet_size".into(), json!(max));
                }
                o.insert("obfs".into(), Value::Object(obfs));
            }
            let ports_raw = extra(node, "server_ports").or_else(|| extra(node, "mport"));
            if let Some(ports) = ports_raw.and_then(normalize_server_ports) {
                o.insert("server_ports".into(), json!(ports));
            }
            if let Some(up) = extra(node, "up").and_then(|v| v.parse::<u32>().ok()) {
                o.insert("up_mbps".into(), json!(up));
            }
            if let Some(down) = extra(node, "down").and_then(|v| v.parse::<u32>().ok()) {
                o.insert("down_mbps".into(), json!(down));
            }
        }
        Protocol::TUIC => {
            o.insert("type".into(), json!("tuic"));
            o.insert("uuid".into(), json!(node.uuid.as_deref().unwrap_or("")));
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            if let Some(cc) = extra(node, "congestion_control") {
                o.insert("congestion_control".into(), json!(cc));
            }
            if let Some(mode) = extra(node, "udp_relay_mode") {
                o.insert("udp_relay_mode".into(), json!(mode));
            }
            apply_tls(&mut o, node, true);
        }
        Protocol::AnyTLS => {
            o.insert("type".into(), json!("anytls"));
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            apply_tls(&mut o, node, true);
        }
        Protocol::Naive => {
            // sing-box 需带 with_naive_outbound 构建标签（桌面 runtime 1.14.3 已带）。
            o.insert("type".into(), json!("naive"));
            if let Some(username) = node.username.as_deref().filter(|v| !v.is_empty()) {
                o.insert("username".into(), json!(username));
            }
            o.insert("password".into(), json!(node.password.as_deref().unwrap_or("")));
            apply_tls(&mut o, node, true);
        }
        Protocol::Socks => {
            o.insert("type".into(), json!("socks"));
            if let Some(username) = node.username.as_deref().filter(|v| !v.is_empty()) {
                o.insert("username".into(), json!(username));
            }
            if let Some(password) = node.password.as_deref().filter(|v| !v.is_empty()) {
                o.insert("password".into(), json!(password));
            }
        }
        Protocol::WireGuard => {
            return Err(ConfigError::InvalidNode {
                name: display_name(node, 0),
                reason: "WireGuard 应生成 endpoint，不能走 outbound 路径".into(),
            });
        }
        Protocol::Unknown => {
            return Err(ConfigError::UnsupportedProtocol {
                name: display_name(node, 0),
                protocol: node.protocol.as_str().into(),
            });
        }
    }
    Ok(o)
}

fn apply_tls(o: &mut Map<String, Value>, node: &Node, force_tls: bool) {
    let security = node.security.trim().to_ascii_lowercase();
    let enabled = force_tls || security == "tls" || security == "reality";
    if !enabled {
        return;
    }
    let mut tls = Map::new();
    tls.insert("enabled".into(), json!(true));
    let server_name = node
        .sni
        .as_deref()
        .filter(|v| !v.is_empty())
        .or_else(|| node.host_header.as_deref().filter(|v| !v.is_empty()))
        .map(str::to_string)
        .unwrap_or_else(|| strip_brackets(&node.server));
    if !server_name.is_empty() {
        tls.insert("server_name".into(), json!(server_name));
    }
    if node.allow_insecure || extra(node, "pinsha256").is_some() {
        // 与 一致：pinSHA256 在 sing-box 1.14 不被识别，证书链无法验证，只能放开校验。
        tls.insert("insecure".into(), json!(true));
    }
    if let Some(fingerprint) = node.fingerprint.as_deref().filter(|v| !v.is_empty()) {
        tls.insert(
            "utls".into(),
            json!({ "enabled": true, "fingerprint": fingerprint }),
        );
    }
    if security == "reality" {
        if let Some(public_key) = node.public_key.as_deref().filter(|v| !v.is_empty()) {
            let mut reality = Map::new();
            reality.insert("enabled".into(), json!(true));
            reality.insert("public_key".into(), json!(public_key));
            if let Some(short_id) = node.short_id.as_deref().filter(|v| !v.is_empty()) {
                reality.insert("short_id".into(), json!(short_id));
            }
            tls.insert("reality".into(), Value::Object(reality));
        }
    }
    if let Some(alpn) = extra(node, "alpn") {
        let items: Vec<&str> = alpn
            .split(',')
            .map(str::trim)
            .filter(|v| !v.is_empty())
            .collect();
        if !items.is_empty() {
            tls.insert("alpn".into(), json!(items));
        }
    }
    if let Some(ech) = node.ech_config_list.as_deref().filter(|v| !v.is_empty()) {
        // 带 :// 的 ECH 配置需要额外 DNS 服务；本版跳过（与 一致）。
        if !ech.contains("://") {
            let pem = format!("-----BEGIN ECH CONFIGS-----\n{ech}\n-----END ECH CONFIGS-----");
            tls.insert(
                "ech".into(),
                json!({ "enabled": true, "config": [pem] }),
            );
        }
    }
    o.insert("tls".into(), Value::Object(tls));
}

fn apply_transport(o: &mut Map<String, Value>, node: &Node) -> Result<(), ConfigError> {
    let network = node.network.trim().to_ascii_lowercase();
    match network.as_str() {
        "" | "tcp" | "none" => Ok(()),
        "ws" => {
            let path = node.path.as_deref().filter(|v| !v.is_empty()).unwrap_or("/");
            let mut ws = Map::new();
            ws.insert("type".into(), json!("ws"));
            ws.insert("path".into(), json!(path));
            if let Some(host) = node.host_header.as_deref().filter(|v| !v.is_empty()) {
                ws.insert("headers".into(), json!({ "Host": host }));
            }
            o.insert("transport".into(), Value::Object(ws));
            Ok(())
        }
        "grpc" => {
            o.insert(
                "transport".into(),
                json!({ "type": "grpc", "service_name": node.service_name.as_deref().unwrap_or("") }),
            );
            Ok(())
        }
        "http" | "h2" => {
            let host = node
                .host_header
                .as_deref()
                .filter(|v| !v.is_empty())
                .map(str::to_string)
                .unwrap_or_else(|| strip_brackets(&node.server));
            let path = node.path.as_deref().filter(|v| !v.is_empty()).unwrap_or("/");
            o.insert(
                "transport".into(),
                json!({ "type": "http", "host": [host], "path": path }),
            );
            Ok(())
        }
        "httpupgrade" => {
            let path = node.path.as_deref().filter(|v| !v.is_empty()).unwrap_or("/");
            let mut hu = Map::new();
            hu.insert("type".into(), json!("httpupgrade"));
            hu.insert("path".into(), json!(path));
            if let Some(host) = node.host_header.as_deref().filter(|v| !v.is_empty()) {
                hu.insert("host".into(), json!(host));
            }
            o.insert("transport".into(), Value::Object(hu));
            Ok(())
        }
        "xhttp" | "kcp" | "mkcp" | "quic" => Err(ConfigError::UnsupportedTransport {
            name: display_name(node, 0),
            network,
        }),
        _ => Err(ConfigError::UnsupportedTransport {
            name: display_name(node, 0),
            network,
        }),
    }
}

/// "443,5000-6000" / "20000-30000" / "443" → sing-box server_ports（"start:end"）。
/// 单端口且无跳跃时返回 None（沿用 server_port）。
fn normalize_server_ports(raw: &str) -> Option<Vec<String>> {
    let raw = raw.trim();
    if raw.is_empty() {
        return None;
    }
    let mut ports = Vec::new();
    for part in raw.split(',').map(str::trim).filter(|p| !p.is_empty()) {
        let normalized = part.replace('-', ":");
        let segments: Vec<&str> = normalized
            .split(':')
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .collect();
        let Some(first) = segments.first().and_then(|v| v.parse::<u16>().ok()) else {
            continue;
        };
        if first == 0 {
            continue;
        }
        match segments.get(1).and_then(|v| v.parse::<u16>().ok()) {
            Some(end) if end > 0 => ports.push(format!("{first}:{end}")),
            _ => ports.push(format!("{first}:{first}")),
        }
    }
    if ports.is_empty() {
        return None;
    }
    let has_jump = raw.contains(',') || raw.contains('-') || raw.contains(':') || ports.len() > 1;
    if has_jump {
        Some(ports)
    } else {
        None
    }
}

fn build_wireguard_endpoint(node: &Node, tag: &str) -> Result<Value, ConfigError> {
    let addresses = node
        .wg_address
        .as_deref()
        .map(|raw| {
            raw.split(',')
                .map(str::trim)
                .filter(|v| !v.is_empty())
                .map(str::to_string)
                .collect::<Vec<_>>()
        })
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| vec!["172.16.0.2/32".to_string()]);

    let mut peer = Map::new();
    peer.insert("address".into(), json!(strip_brackets(&node.server)));
    peer.insert("port".into(), json!(node.port));
    peer.insert(
        "public_key".into(),
        json!(node.wg_public_key.as_deref().unwrap_or("")),
    );
    peer.insert("allowed_ips".into(), json!(["0.0.0.0/0", "::/0"]));
    if let Some(psk) = node.wg_preshared_key.as_deref().filter(|v| !v.is_empty()) {
        peer.insert("pre_shared_key".into(), json!(psk));
    }
    if let Some(keepalive) = node.wg_keepalive.filter(|v| *v > 0) {
        peer.insert("persistent_keepalive_interval".into(), json!(keepalive));
    }
    if let Some(reserved) = node.wg_reserved.as_deref().and_then(parse_reserved) {
        peer.insert("reserved".into(), json!(reserved));
    }

    let mut endpoint = Map::new();
    endpoint.insert("type".into(), json!("wireguard"));
    endpoint.insert("tag".into(), json!(tag));
    endpoint.insert("address".into(), json!(addresses));
    endpoint.insert(
        "private_key".into(),
        json!(node.password.as_deref().unwrap_or("")),
    );
    endpoint.insert("peers".into(), Value::Array(vec![Value::Object(peer)]));
    if let Some(mtu) = node.wg_mtu.filter(|v| *v > 0) {
        endpoint.insert("mtu".into(), json!(mtu));
    }
    Ok(Value::Object(endpoint))
}

/// WARP reserved：支持 "0,0,0" 与三字节 base64；解析失败返回 None（不写字段）。
fn parse_reserved(raw: &str) -> Option<Vec<u8>> {
    let s = raw.trim();
    if s.is_empty() {
        return None;
    }
    if s.contains(',') {
        let parts: Vec<u8> = s
            .split(',')
            .map(str::trim)
            .filter(|p| !p.is_empty())
            .map(|p| p.parse::<u8>())
            .collect::<Result<_, _>>()
            .ok()?;
        return (parts.len() == 3).then_some(parts);
    }
    use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
    use base64::Engine as _;
    let bytes = STANDARD
        .decode(s)
        .or_else(|_| URL_SAFE_NO_PAD.decode(s))
        .ok()?;
    (bytes.len() == 3).then_some(bytes)
}

fn build_groups(node_tags: &[String]) -> Vec<Value> {
    let mut select_members: Vec<Value> = vec![json!(GROUP_AUTO)];
    select_members.extend(node_tags.iter().map(|t| json!(t)));
    select_members.push(json!(DIRECT_OUTBOUND_TAG));

    let mut auto_members: Vec<Value> = node_tags.iter().map(|t| json!(t)).collect();
    if auto_members.is_empty() {
        auto_members.push(json!(DIRECT_OUTBOUND_TAG));
    }

    vec![
        json!({
            "type": "selector",
            "tag": GROUP_SELECT,
            "outbounds": select_members,
            "default": GROUP_AUTO,
            "interrupt_exist_connections": true,
        }),
        json!({
            "type": "urltest",
            "tag": GROUP_AUTO,
            "outbounds": auto_members,
            "url": URLTEST_URL,
            "interval": "3m",
            "tolerance": 50,
            "interrupt_exist_connections": false,
        }),
        json!({
            "type": "selector",
            "tag": GROUP_FINAL,
            "outbounds": [GROUP_SELECT, DIRECT_OUTBOUND_TAG],
            "default": GROUP_SELECT,
            "interrupt_exist_connections": true,
        }),
    ]
}

fn build_route(opts: &SingBoxOptions, final_outbound: Outbound) -> Value {
    let mut plan = RoutePlan::new(final_outbound);
    if opts.tun && !opts.game_direct_processes.is_empty() {
        plan = plan.push(
            RouteRule::direct("game")
                .with_process_name(&opts.game_direct_processes)
                .with_outbound(Outbound::Direct),
        );
    }
    if opts.proxy_mode == ProxyMode::Rule {
        let with_rule_sets = opts.rule_set_dir.is_some();
        if with_rule_sets && opts.block_ads {
            plan = plan.push(RouteRule::block("ads").with_rule_set(&["geosite-ads"]));
        }
        if opts.bypass_private {
            plan = plan.push(RouteRule::direct("private").with_ip_is_private());
        }
        // Windows 连通性探测 + Steam 探测/CDN：SNI 规则（sing-box 用 domain_suffix 匹配嗅探到的域名）。
        plan = plan.push(
            RouteRule::direct("ncsi")
                .with_domain_suffix(&[".msftconnecttest.com", ".msftncsi.com"]),
        );
        plan = plan.push(
            RouteRule::direct("steam")
                .with_domain_suffix(&[".steamserver.net", ".steamcontent.com"]),
        );
        if with_rule_sets && opts.bypass_cn {
            plan = plan.push(
                RouteRule::direct("cn").with_rule_set(&["geosite-cn", "geoip-cn"]),
            );
        }
    }

    let mut route = plan.to_singbox_route();
    let mut rules: Vec<Value> = Vec::new();
    if opts.tun {
        rules.push(json!({ "action": "sniff" }));
        rules.push(json!({ "protocol": "dns", "action": "hijack-dns" }));
    }
    rules.extend(plan.rules.iter().map(RouteRule::to_singbox_rule));
    route["rules"] = Value::Array(rules);
    route["auto_detect_interface"] = json!(true);
    route["default_domain_resolver"] = json!(DNS_DIRECT_TAG);
    if let Some(dir) = &opts.rule_set_dir {
        route["rule_set"] = rule_set_definitions(dir);
    }
    route
}

fn rule_set_definitions(runtime_dir: &Path) -> Value {
    let base = runtime_dir.join("rule-sets");
    let local = |tag: &str, file: &str| {
        json!({
            "tag": tag,
            "type": "local",
            "format": "binary",
            "path": base.join(file).to_string_lossy(),
        })
    };
    json!([
        local("geosite-ads", "geosite-category-ads-all.srs"),
        local("geosite-cn", "geosite-cn.srs"),
        local("geoip-cn", "geoip-cn.srs"),
    ])
}

fn build_inbounds(opts: &SingBoxOptions) -> Value {
    let listen = if opts.allow_lan { "0.0.0.0" } else { "127.0.0.1" };
    let mut list: Vec<Value> = Vec::new();
    if opts.tun {
        list.push(json!({
            "type": "tun",
            "tag": TUN_INBOUND_TAG,
            "interface_name": "neo-tun",
            "address": ["172.19.0.1/30"],
            "mtu": 9000,
            "auto_route": true,
            "strict_route": true,
            "stack": "mixed",
        }));
    }
    if opts.socks_http_inbounds {
        // 与 Windows 端同构：socks-in + http-in 两个入站（中继分别转发到它们）。
        list.push(json!({
            "type": "socks",
            "tag": SOCKS_INBOUND_TAG,
            "listen": listen,
            "listen_port": opts.mixed_port,
        }));
        list.push(json!({
            "type": "http",
            "tag": HTTP_INBOUND_TAG,
            "listen": listen,
            "listen_port": opts.http_port,
        }));
    } else {
        list.push(json!({
            "type": "mixed",
            "tag": MIXED_INBOUND_TAG,
            "listen": listen,
            "listen_port": opts.mixed_port,
        }));
    }
    Value::Array(list)
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct DnsEndpoint {
    kind: &'static str,
    host: String,
    port: Option<u16>,
    path: Option<String>,
}

fn build_dns(opts: &SingBoxOptions, proxy_anchor: &str) -> Result<Value, ConfigError> {
    let endpoint = parse_dns_endpoint(&opts.dns_server)?;
    let bootstrap = !is_ip_literal(&endpoint.host);
    let resolver = bootstrap.then_some(DNS_BOOTSTRAP_TAG);
    let mut plan = DnsPlan::neo_default_with(
        opts.tun,
        opts.rule_set_dir.is_some(),
        &endpoint.host,
        resolver,
        proxy_anchor,
    );

    if endpoint.kind != "udp" {
        if let Some(slot) = plan.servers.iter_mut().find(|s| s.tag() == DNS_DIRECT_TAG) {
            *slot = DnsServer::Custom {
                tag: DNS_DIRECT_TAG.into(),
                kind: endpoint.kind.into(),
                server: endpoint.host.clone(),
                server_port: endpoint.port,
                path: endpoint.path.clone(),
                detour: None,
                domain_resolver: resolver.map(str::to_string),
            };
        }
    }
    if resolver.is_some() {
        let index = plan
            .servers
            .iter()
            .position(|s| s.tag() == DNS_DIRECT_TAG)
            .unwrap_or(0);
        plan.servers.insert(
            index,
            DnsServer::Direct {
                tag: DNS_BOOTSTRAP_TAG.into(),
                address: "223.5.5.5".into(),
                domain_resolver: None,
            },
        );
    }
    // 自定义 DoH：远程解析也复用同一 DoH（与 一致）；否则固定 8.8.8.8（dns.rs 默认）。
    if endpoint.kind == "https" {
        if let Some(slot) = plan.servers.iter_mut().find(|s| s.tag() == DNS_REMOTE_TAG) {
            *slot = DnsServer::Custom {
                tag: DNS_REMOTE_TAG.into(),
                kind: "https".into(),
                server: endpoint.host.clone(),
                server_port: endpoint.port,
                path: endpoint.path.clone(),
                detour: Some(proxy_anchor.to_string()),
                domain_resolver: resolver.map(str::to_string),
            };
        }
    }
    Ok(plan.to_singbox())
}

fn parse_dns_endpoint(raw: &str) -> Result<DnsEndpoint, ConfigError> {
    let value = raw.trim();
    if value.is_empty() {
        return Err(ConfigError::InvalidDnsServer {
            value: raw.into(),
            reason: "不能为空".into(),
        });
    }
    let invalid = |reason: &str| ConfigError::InvalidDnsServer {
        value: raw.into(),
        reason: reason.into(),
    };

    for (scheme, kind) in [("https://", "https"), ("tls://", "tls")] {
        if let Some(rest) = value.strip_prefix(scheme) {
            let rest = rest.trim_end_matches('/');
            let (authority, path) = match rest.split_once('/') {
                Some((authority, path)) => (authority, Some(format!("/{}", path.split('?').next().unwrap_or("")))),
                None => (rest, None),
            };
            if authority.is_empty() {
                return Err(invalid("缺少主机名"));
            }
            let (host, port) = split_host_port_opt(authority);
            if host.is_empty() {
                return Err(invalid("缺少主机名"));
            }
            return Ok(DnsEndpoint {
                kind,
                host: strip_brackets(&host),
                port,
                path: path.filter(|p| p != "/"),
            });
        }
    }
    if value.contains("://") {
        return Err(invalid("只支持 IP/域名、https:// 或 tls://"));
    }
    let (host, port) = split_host_port_opt(value);
    if host.is_empty() {
        return Err(invalid("缺少主机名"));
    }
    Ok(DnsEndpoint {
        kind: "udp",
        host: strip_brackets(&host),
        port,
        path: None,
    })
}

/// 拆 `host[:port]`，兼容 IPv6 字面量 `[::1]:53` 与裸 `::1`。
fn split_host_port_opt(raw: &str) -> (String, Option<u16>) {
    let raw = raw.trim();
    if raw.starts_with('[') {
        if let Some(end) = raw.find(']') {
            let host = raw[..=end].to_string();
            let rest = raw[end + 1..].trim_start_matches(':');
            let port = rest.parse::<u16>().ok();
            return (host, port);
        }
        return (raw.to_string(), None);
    }
    if let Some((host, port)) = raw.rsplit_once(':') {
        if !host.contains(':') {
            if let Ok(port) = port.parse::<u16>() {
                return (host.to_string(), Some(port));
            }
        }
    }
    (raw.to_string(), None)
}

fn is_ip_literal(host: &str) -> bool {
    strip_brackets(host).parse::<std::net::IpAddr>().is_ok()
}

// ---------------------------------------------------------------------------
// 测试
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    fn node(protocol: Protocol, name: &str) -> Node {
        Node {
            name: name.into(),
            protocol,
            server: "example.com".into(),
            port: 443,
            uuid: Some("11111111-1111-4111-8111-111111111111".into()),
            password: Some("secret".into()),
            ..Node::default()
        }
    }

    fn full_options() -> SingBoxOptions {
        SingBoxOptions {
            tun: true,
            game_direct_processes: vec!["TslGame.exe".into(), "cs2.exe".into()],
            rule_set_dir: Some(PathBuf::from("runtime")),
            ..SingBoxOptions::default()
        }
    }

    #[test]
    fn non_tun_config_has_mixed_and_no_fakeip() {
        let v = build_config(&[node(Protocol::VLESS, "日本 01")], &SingBoxOptions::default()).unwrap();
        let inbounds = v["inbounds"].as_array().unwrap();
        assert_eq!(inbounds.len(), 1);
        assert_eq!(inbounds[0]["type"], json!("mixed"));
        assert_eq!(inbounds[0]["listen"], json!("127.0.0.1"));
        assert_eq!(inbounds[0]["listen_port"], json!(21808));
        let servers = v["dns"]["servers"].as_array().unwrap();
        assert!(servers.iter().all(|s| s["type"] != json!("fakeip")));
        assert!(v["dns"]["rules"].as_array().unwrap().iter().all(|r| r["server"] != json!("dns-fakeip")));
        // 没有 rule_set_dir 时不生成/不引用规则集，第一条规则是私网直连。
        assert_eq!(v["route"]["rules"][0]["ip_is_private"], json!(true));
        assert_eq!(v["route"]["final"], json!(GROUP_FINAL));
        assert_eq!(v["log"]["level"], json!("warn"));
    }

    #[test]
    fn socks_http_inbounds_match_csharp_shape() {
        use crate::node::{Node, Protocol};
        for tun in [false, true] {
            let mut node = Node::default();
            node.server = "example.com".into();
            node.port = 443;
            node.protocol = Protocol::Trojan;
            node.password = Some("pass".into());
            node.security = "tls".into();
            let opts = SingBoxOptions {
                tun,
                socks_http_inbounds: true,
                mixed_port: 21908,
                http_port: 21909,
                rule_set_dir: None,
                ..SingBoxOptions::default()
            };
            let v = build_config(&[node], &opts).unwrap();
            let types: Vec<&str> = v["inbounds"]
                .as_array()
                .unwrap()
                .iter()
                .map(|i| i["type"].as_str().unwrap())
                .collect();
            assert_eq!(types, if tun { vec!["tun", "socks", "http"] } else { vec!["socks", "http"] });
            assert_eq!(
                v["inbounds"].as_array().unwrap().last().unwrap()["listen_port"],
                json!(21909)
            );
        }
    }

    #[test]
    fn tun_config_has_tun_mixed_and_ipv4_only_fakeip() {
        let v = build_config(&[node(Protocol::VLESS, "日本 01")], &full_options()).unwrap();
        let inbounds = v["inbounds"].as_array().unwrap();
        assert_eq!(inbounds.len(), 2);
        assert_eq!(inbounds[0]["type"], json!("tun"));
        assert_eq!(inbounds[0]["tag"], json!("tun-in"));
        assert_eq!(inbounds[0]["interface_name"], json!("neo-tun"));
        assert_eq!(inbounds[0]["address"], json!(["172.19.0.1/30"]));
        assert_eq!(inbounds[0]["mtu"], json!(9000));
        assert_eq!(inbounds[0]["auto_route"], json!(true));
        assert_eq!(inbounds[0]["strict_route"], json!(true));
        assert_eq!(inbounds[0]["stack"], json!("mixed"));
        assert_eq!(inbounds[1]["type"], json!("mixed"));

        let fake = v["dns"]["servers"].as_array().unwrap().iter().find(|s| s["type"] == json!("fakeip")).unwrap();
        assert_eq!(fake["inet4_range"], json!("198.18.0.0/15"));
        assert!(fake.get("inet6_range").is_none());

        let rules = v["dns"]["rules"].as_array().unwrap();
        let fake_rule = rules.iter().find(|r| r["server"] == json!("dns-fakeip")).unwrap();
        assert_eq!(fake_rule["query_type"], json!(["A"]));

        assert_eq!(v["route"]["rules"][0]["action"], json!("sniff"));
        assert_eq!(v["route"]["rules"][1]["protocol"], json!("dns"));
        assert_eq!(v["route"]["rules"][1]["action"], json!("hijack-dns"));
        assert_eq!(v["route"]["rules"][2]["process_name"], json!(["TslGame.exe", "cs2.exe"]));
        assert_eq!(v["route"]["rules"][2]["outbound"], json!("direct"));
    }

    #[test]
    fn groups_are_emitted_with_desktop_names_and_final() {
        let v = build_config(&[node(Protocol::VLESS, "日本 01")], &SingBoxOptions::default()).unwrap();
        let outbounds = v["outbounds"].as_array().unwrap();
        let selector = outbounds.iter().find(|o| o["tag"] == json!(GROUP_SELECT)).unwrap();
        assert_eq!(selector["type"], json!("selector"));
        assert_eq!(selector["outbounds"], json!([GROUP_AUTO, "proxy", "direct"]));
        assert_eq!(selector["default"], json!(GROUP_AUTO));
        let urltest = outbounds.iter().find(|o| o["tag"] == json!(GROUP_AUTO)).unwrap();
        assert_eq!(urltest["type"], json!("urltest"));
        assert_eq!(urltest["outbounds"], json!(["proxy"]));
        assert_eq!(urltest["url"], json!(URLTEST_URL));
        let final_group = outbounds.iter().find(|o| o["tag"] == json!(GROUP_FINAL)).unwrap();
        assert_eq!(final_group["type"], json!("selector"));
        assert_eq!(v["route"]["final"], json!(GROUP_FINAL));
        assert_eq!(v["dns"]["servers"].as_array().unwrap().iter().find(|s| s["tag"] == json!("dns-remote")).unwrap()["detour"], json!(GROUP_SELECT));
    }

    #[test]
    fn no_groups_is_csharp_isomorphic_for_single_node() {
        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        let v = build_config(&[node(Protocol::VLESS, "日本 01")], &opts).unwrap();
        let outbounds = v["outbounds"].as_array().unwrap();
        assert_eq!(outbounds.len(), 2);
        assert_eq!(outbounds[0]["type"], json!("vless"));
        assert_eq!(outbounds[0]["tag"], json!("proxy"));
        assert_eq!(outbounds[1]["type"], json!("direct"));
        assert_eq!(v["route"]["final"], json!("proxy"));
        assert_eq!(v["dns"]["servers"].as_array().unwrap().iter().find(|s| s["tag"] == json!("dns-remote")).unwrap()["detour"], json!("proxy"));
    }

    #[test]
    fn multi_nodes_get_unique_tags_and_all_join_groups() {
        let nodes = vec![
            node(Protocol::VLESS, "重复"),
            node(Protocol::VMess, "重复"),
            node(Protocol::Trojan, "direct"),
        ];
        let v = build_config(&nodes, &SingBoxOptions::default()).unwrap();
        let tags: Vec<&str> = v["outbounds"].as_array().unwrap().iter().filter_map(|o| o["tag"].as_str()).collect();
        assert!(tags.contains(&"重复"));
        assert!(tags.contains(&"重复 #2"));
        assert!(!tags.contains(&"direct") || tags.iter().filter(|t| **t == "direct").count() == 1);
        let urltest = v["outbounds"].as_array().unwrap().iter().find(|o| o["tag"] == json!(GROUP_AUTO)).unwrap();
        assert_eq!(urltest["outbounds"].as_array().unwrap().len(), 3);
    }

    #[test]
    fn vless_fields_match_reference_shape() {
        let mut n = node(Protocol::VLESS, "vless");
        n.network = "ws".into();
        n.security = "tls".into();
        n.path = Some("/ws".into());
        n.host_header = Some("cdn.example.com".into());
        n.sni = Some("cdn.example.com".into());
        n.fingerprint = Some("chrome".into());
        n.flow = Some("xtls-rprx-vision".into());
        n.extras.insert("alpn".into(), "h2,http/1.1".into());
        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        let v = build_config(&[n], &opts).unwrap();
        let o = &v["outbounds"][0];
        assert_eq!(o["type"], json!("vless"));
        assert_eq!(o["uuid"], json!("11111111-1111-4111-8111-111111111111"));
        assert_eq!(o["flow"], json!("xtls-rprx-vision"));
        assert_eq!(o["server"], json!("example.com"));
        assert_eq!(o["server_port"], json!(443));
        assert_eq!(o["domain_resolver"], json!("dns-direct"));
        assert_eq!(o["tls"]["enabled"], json!(true));
        assert_eq!(o["tls"]["server_name"], json!("cdn.example.com"));
        assert_eq!(o["tls"]["utls"]["fingerprint"], json!("chrome"));
        assert_eq!(o["tls"]["alpn"], json!(["h2", "http/1.1"]));
        assert_eq!(o["transport"]["type"], json!("ws"));
        assert_eq!(o["transport"]["path"], json!("/ws"));
        assert_eq!(o["transport"]["headers"]["Host"], json!("cdn.example.com"));
    }

    #[test]
    fn reality_ech_and_pinsha256_are_mapped() {
        let mut n = node(Protocol::VLESS, "reality");
        n.security = "reality".into();
        n.public_key = Some("pubkey".into());
        n.short_id = Some("abcd".into());
        n.fingerprint = Some("chrome".into());
        n.sni = Some("www.example.com".into());
        n.ech_config_list = Some("AEX+".into());
        n.allow_insecure = true;
        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        let v = build_config(&[n], &opts).unwrap();
        let tls = &v["outbounds"][0]["tls"];
        assert_eq!(tls["reality"]["enabled"], json!(true));
        assert_eq!(tls["reality"]["public_key"], json!("pubkey"));
        assert_eq!(tls["reality"]["short_id"], json!("abcd"));
        assert_eq!(tls["ech"]["enabled"], json!(true));
        assert_eq!(tls["ech"]["config"][0], json!("-----BEGIN ECH CONFIGS-----\nAEX+\n-----END ECH CONFIGS-----"));
        assert_eq!(tls["insecure"], json!(true));
    }

    #[test]
    fn ss_plugin_and_hy2_obfs_ports_are_mapped() {
        let mut ss = node(Protocol::Shadowsocks, "ss");
        ss.method = Some("aes-256-gcm".into());
        ss.plugin = Some("obfs-local".into());
        ss.plugin_opts = Some("obfs=http;obfs-host=bing.com".into());

        let mut hy = node(Protocol::Hysteria2, "hy2");
        hy.server = "hy.example.com".into();
        hy.extras.insert("obfs".into(), "gecko".into());
        hy.extras.insert("obfs-password".into(), "pwd".into());
        hy.extras.insert("server_ports".into(), "443,5000-6000".into());
        hy.extras.insert("up".into(), "50".into());
        hy.extras.insert("down".into(), "200".into());

        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        let ss_out = build_config(&[ss], &opts).unwrap();
        let o = &ss_out["outbounds"][0];
        assert_eq!(o["type"], json!("shadowsocks"));
        assert_eq!(o["method"], json!("aes-256-gcm"));
        assert_eq!(o["plugin"], json!("obfs-local"));
        assert_eq!(o["plugin_opts"], json!("obfs=http;obfs-host=bing.com"));

        let hy_out = build_config(&[hy], &opts).unwrap();
        let o = &hy_out["outbounds"][0];
        assert_eq!(o["type"], json!("hysteria2"));
        assert_eq!(o["obfs"]["type"], json!("gecko"));
        assert_eq!(o["obfs"]["password"], json!("pwd"));
        assert_eq!(o["obfs"]["min_packet_size"], json!(512));
        assert_eq!(o["obfs"]["max_packet_size"], json!(1200));
        assert_eq!(o["server_ports"], json!(["443:443", "5000:6000"]));
        assert_eq!(o["up_mbps"], json!(50));
        assert_eq!(o["down_mbps"], json!(200));
        assert_eq!(o["tls"]["enabled"], json!(true));
    }

    #[test]
    fn all_share_link_protocols_build() {
        let cases = [
            (Protocol::VLESS, "vless"),
            (Protocol::VMess, "vmess"),
            (Protocol::Trojan, "trojan"),
            (Protocol::Shadowsocks, "ss"),
            (Protocol::Hysteria2, "hy2"),
            (Protocol::TUIC, "tuic"),
            (Protocol::AnyTLS, "anytls"),
            (Protocol::Socks, "socks"),
            (Protocol::Naive, "naive"),
        ];
        for (i, (protocol, tag)) in cases.iter().enumerate() {
            let mut n = node(*protocol, &format!("n{i}"));
            n.username = Some("user".into());
            n.method = Some("aes-256-gcm".into());
            let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
            let v = build_config(&[n], &opts).unwrap();
            assert_eq!(v["outbounds"][0]["type"], json!(expected_type(*protocol)), "{tag}");
            assert_eq!(v["outbounds"][0]["tag"], json!("proxy"), "{tag}");
        }
    }

    fn expected_type(protocol: Protocol) -> &'static str {
        match protocol {
            Protocol::VLESS => "vless",
            Protocol::VMess => "vmess",
            Protocol::Trojan => "trojan",
            Protocol::Shadowsocks => "shadowsocks",
            Protocol::Hysteria2 => "hysteria2",
            Protocol::TUIC => "tuic",
            Protocol::AnyTLS => "anytls",
            Protocol::Socks => "socks",
            Protocol::Naive => "naive",
            _ => "unknown",
        }
    }

    #[test]
    fn wireguard_becomes_endpoint() {
        let mut n = node(Protocol::WireGuard, "warp");
        n.server = "engage.cloudflareclient.com".into();
        n.port = 2408;
        n.password = Some("privkey".into());
        n.wg_public_key = Some("pubkey".into());
        n.wg_address = Some("172.16.0.2/32,2606:4700::1/128".into());
        n.wg_mtu = Some(1280);
        n.wg_reserved = Some("1,2,3".into());
        n.wg_keepalive = Some(25);
        n.wg_preshared_key = Some("psk".into());
        let v = build_config(&[n], &full_options()).unwrap();
        let endpoint = &v["endpoints"][0];
        assert_eq!(endpoint["type"], json!("wireguard"));
        assert_eq!(endpoint["tag"], json!("proxy"));
        assert_eq!(endpoint["address"], json!(["172.16.0.2/32", "2606:4700::1/128"]));
        assert_eq!(endpoint["private_key"], json!("privkey"));
        assert_eq!(endpoint["mtu"], json!(1280));
        assert_eq!(endpoint["peers"][0]["address"], json!("engage.cloudflareclient.com"));
        assert_eq!(endpoint["peers"][0]["port"], json!(2408));
        assert_eq!(endpoint["peers"][0]["reserved"], json!([1, 2, 3]));
        assert_eq!(endpoint["peers"][0]["persistent_keepalive_interval"], json!(25));
        assert_eq!(v["outbounds"].as_array().unwrap().len(), 1); // 只有 direct
        assert_eq!(v["route"]["final"], json!("proxy"));
    }

    #[test]
    fn wireguard_with_other_nodes_is_rejected() {
        let mut wg = node(Protocol::WireGuard, "warp");
        wg.wg_public_key = Some("pubkey".into());
        let other = node(Protocol::VLESS, "vless");
        let err = build_config(&[wg, other], &SingBoxOptions::default()).unwrap_err();
        assert!(matches!(err, ConfigError::WireGuardRequiresSingleNode { .. }));
    }

    #[test]
    fn unsupported_transport_and_encryption_are_explicit_errors() {
        let mut xhttp = node(Protocol::VLESS, "xhttp");
        xhttp.network = "xhttp".into();
        let err = build_config(&[xhttp], &SingBoxOptions::default()).unwrap_err();
        assert!(matches!(err, ConfigError::UnsupportedTransport { .. }));
        assert!(err.to_string().contains("只有 xray"));

        let mut enc = node(Protocol::VLESS, "pq");
        enc.vless_encryption = Some("mlkem768x25519plus".into());
        let err = build_config(&[enc], &SingBoxOptions::default()).unwrap_err();
        assert!(matches!(err, ConfigError::VlessEncryption { .. }));

        let mut kcp = node(Protocol::VLESS, "kcp");
        kcp.network = "kcp".into();
        assert!(matches!(
            build_config(&[kcp], &SingBoxOptions::default()).unwrap_err(),
            ConfigError::UnsupportedTransport { .. }
        ));
    }

    #[test]
    fn invalid_inputs_return_errors_not_panics() {
        assert!(matches!(build_config(&[], &SingBoxOptions::default()).unwrap_err(), ConfigError::NoNodes));
        let mut bad = node(Protocol::VLESS, "bad");
        bad.port = 0;
        assert!(matches!(build_config(&[bad], &SingBoxOptions::default()).unwrap_err(), ConfigError::InvalidNode { .. }));
        let mut unknown = node(Protocol::Unknown, "unknown");
        unknown.protocol = Protocol::Unknown;
        assert!(matches!(build_config(&[unknown], &SingBoxOptions::default()).unwrap_err(), ConfigError::UnsupportedProtocol { .. }));
        let multi = vec![node(Protocol::VLESS, "a"), node(Protocol::VLESS, "b")];
        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        assert!(matches!(build_config(&multi, &opts).unwrap_err(), ConfigError::GroupsRequiredForMultipleNodes { .. }));

        let mut bad_uuid = node(Protocol::VLESS, "uuid");
        bad_uuid.uuid = Some("uuid-1".into());
        let error = build_config(&[bad_uuid], &SingBoxOptions::default()).unwrap_err();
        assert!(matches!(error, ConfigError::InvalidNode { .. }));
        assert!(error.to_string().contains("uuid"));
    }

    #[test]
    fn route_rules_follow_windows_order() {
        let opts = SingBoxOptions { emit_groups: false, ..full_options() };
        let v = build_config(&[node(Protocol::VLESS, "n")], &opts).unwrap();
        let rules = v["route"]["rules"].as_array().unwrap();
        assert_eq!(rules[0]["action"], json!("sniff"));
        assert_eq!(rules[1]["action"], json!("hijack-dns"));
        assert_eq!(rules[2]["process_name"], json!(["TslGame.exe", "cs2.exe"]));
        assert_eq!(rules[3]["rule_set"], json!(["geosite-ads"]));
        assert_eq!(rules[3]["action"], json!("reject"));
        assert_eq!(rules[4]["ip_is_private"], json!(true));
        assert_eq!(rules[5]["domain_suffix"], json!([".msftconnecttest.com", ".msftncsi.com"]));
        assert_eq!(rules[6]["domain_suffix"], json!([".steamserver.net", ".steamcontent.com"]));
        assert_eq!(rules[7]["rule_set"], json!(["geosite-cn", "geoip-cn"]));
        assert_eq!(rules[7]["outbound"], json!("direct"));
        assert_eq!(
            v["route"]["default_domain_resolver"],
            json!("dns-direct")
        );
        assert_eq!(v["route"]["auto_detect_interface"], json!(true));
        assert_eq!(v["route"]["rule_set"].as_array().unwrap().len(), 3);
        let paths: Vec<&str> = v["route"]["rule_set"].as_array().unwrap().iter().filter_map(|s| s["path"].as_str()).collect();
        assert!(paths.iter().all(|p| p.ends_with(".srs")));
    }

    #[test]
    fn without_rule_set_dir_no_rule_set_keys_are_emitted() {
        let opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
        let v = build_config(&[node(Protocol::VLESS, "n")], &opts).unwrap();
        assert!(v["route"].get("rule_set").is_none());
        let dump = serde_json::to_string(&v).unwrap();
        assert!(!dump.contains("geosite-"), "不应引用未定义规则集: {dump}");
        assert!(!dump.contains("geoip-"));
        assert!(dump.contains("msftconnecttest.com"));
    }

    #[test]
    fn proxy_mode_direct_and_global_change_final_only() {
        let mut direct = full_options();
        direct.proxy_mode = ProxyMode::Direct;
        let v = build_config(&[node(Protocol::VLESS, "n")], &direct).unwrap();
        assert_eq!(v["route"]["final"], json!("direct"));
        assert!(!v["route"]["rules"].as_array().unwrap().iter().any(|r| r["rule_set"] == json!(["geosite-ads"])));

        let mut global = full_options();
        global.proxy_mode = ProxyMode::Global;
        let v = build_config(&[node(Protocol::VLESS, "n")], &global).unwrap();
        assert_eq!(v["route"]["final"], json!(GROUP_FINAL));
        assert!(!v["route"]["rules"].as_array().unwrap().iter().any(|r| r["rule_set"] == json!(["geosite-ads"])));
        // 游戏进程规则在非 rule 模式仍保留（与 Windows 端的 tunMode 分支一致）
        assert!(v["route"]["rules"].as_array().unwrap().iter().any(|r| r["process_name"] == json!(["TslGame.exe", "cs2.exe"])));
    }

    #[test]
    fn custom_doh_dns_is_used_for_both_servers() {
        let mut opts = SingBoxOptions::default();
        opts.dns_server = "https://dns.example.com/dns-query".into();
        let v = build_config(&[node(Protocol::VLESS, "n")], &opts).unwrap();
        let servers = v["dns"]["servers"].as_array().unwrap();
        let direct = servers.iter().find(|s| s["tag"] == json!("dns-direct")).unwrap();
        assert_eq!(direct["type"], json!("https"));
        assert_eq!(direct["server"], json!("dns.example.com"));
        assert_eq!(direct["path"], json!("/dns-query"));
        assert_eq!(direct["domain_resolver"], json!("dns-bootstrap"));
        let remote = servers.iter().find(|s| s["tag"] == json!("dns-remote")).unwrap();
        assert_eq!(remote["type"], json!("https"));
        assert_eq!(remote["server"], json!("dns.example.com"));
        assert_eq!(remote["detour"], json!(GROUP_SELECT));
        assert!(servers.iter().any(|s| s["tag"] == json!("dns-bootstrap") && s["server"] == json!("223.5.5.5")));
    }

    #[test]
    fn custom_dns_rejects_bad_scheme() {
        let mut opts = SingBoxOptions::default();
        opts.dns_server = "quic://1.1.1.1".into();
        let err = build_config(&[node(Protocol::VLESS, "n")], &opts).unwrap_err();
        assert!(matches!(err, ConfigError::InvalidDnsServer { .. }));
    }

    #[test]
    fn log_level_mapping_matches_desktop() {
        assert_eq!(map_log_level("none"), "error");
        assert_eq!(map_log_level("error"), "error");
        assert_eq!(map_log_level("warning"), "warn");
        assert_eq!(map_log_level("warn"), "warn");
        assert_eq!(map_log_level("info"), "info");
        assert_eq!(map_log_level("debug"), "debug");
        assert_eq!(map_log_level("nonsense"), "warn");
    }

    #[test]
    fn allow_lan_listens_on_all_interfaces() {
        let opts = SingBoxOptions { allow_lan: true, ..SingBoxOptions::default() };
        let v = build_config(&[node(Protocol::VLESS, "n")], &opts).unwrap();
        assert_eq!(v["inbounds"][0]["listen"], json!("0.0.0.0"));
        assert_eq!(v["experimental"]["clash_api"]["external_controller"], json!("127.0.0.1:21811"));
        assert_eq!(v["experimental"]["clash_api"]["secret"], json!("neo-local-stats"));
    }

    #[test]
    fn server_ports_normalization_matches_csharp() {
        assert_eq!(normalize_server_ports("443,5000-6000"), Some(vec!["443:443".into(), "5000:6000".into()]));
        assert_eq!(normalize_server_ports("20000-30000"), Some(vec!["20000:30000".into()]));
        assert_eq!(normalize_server_ports("443"), None);
        assert_eq!(normalize_server_ports("5000:6000"), Some(vec!["5000:6000".into()]));
        assert_eq!(normalize_server_ports(""), None);
        assert_eq!(normalize_server_ports("abc"), None);
    }

    #[test]
    fn build_config_json_is_pretty_and_ends_with_newline() {
        let text = build_config_json(&[node(Protocol::VLESS, "n")], &SingBoxOptions::default()).unwrap();
        assert!(text.ends_with('\n'));
        assert!(text.contains("\n  \"log\""));
        let parsed: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(parsed["outbounds"][0]["tag"], json!("proxy"));
    }
}
