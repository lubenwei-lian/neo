//! 节点模型。
//!
//! 节点数据结构定义。
//! JSON 对外采用 camelCase，enum 使用分享链接里的协议名。

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

/// 分享链接可解析出的协议。Unknown 用于反序列化/未来扩展。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum Protocol {
    #[default]
    #[serde(rename = "unknown")]
    Unknown,
    #[serde(rename = "vmess")]
    VMess,
    #[serde(rename = "vless")]
    VLESS,
    #[serde(rename = "trojan")]
    Trojan,
    #[serde(rename = "ss")]
    Shadowsocks,
    #[serde(rename = "hysteria2")]
    Hysteria2,
    #[serde(rename = "tuic")]
    TUIC,
    #[serde(rename = "anytls")]
    AnyTLS,
    #[serde(rename = "socks")]
    Socks,
    #[serde(rename = "naive")]
    Naive,
    #[serde(rename = "wireguard")]
    WireGuard,
}

impl Protocol {
    pub fn as_str(self) -> &'static str {
        match self {
            Protocol::Unknown => "unknown",
            Protocol::VMess => "vmess",
            Protocol::VLESS => "vless",
            Protocol::Trojan => "trojan",
            Protocol::Shadowsocks => "ss",
            Protocol::Hysteria2 => "hysteria2",
            Protocol::TUIC => "tuic",
            Protocol::AnyTLS => "anytls",
            Protocol::Socks => "socks",
            Protocol::Naive => "naive",
            Protocol::WireGuard => "wireguard",
        }
    }

    /// UDP 协议没有可测的 TCP 端口，测速时应跳过。
    pub fn is_udp(self) -> bool {
        matches!(
            self,
            Protocol::Hysteria2 | Protocol::TUIC | Protocol::WireGuard
        )
    }
}

/// 兼容别名，便于未来与 Windows 端的 NodeProtocol 命名对照。
pub type NodeProtocol = Protocol;

/// 单个代理节点。字符串可缺省字段用 Option 表示，集合保持确定序（BTreeMap）。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Node {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub protocol: Protocol,
    #[serde(default)]
    pub server: String,
    #[serde(default)]
    pub port: u16,

    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub uuid: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub method: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub username: Option<String>,
    #[serde(default)]
    pub alter_id: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cipher: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub flow: Option<String>,

    #[serde(default = "default_network")]
    pub network: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub host_header: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub service_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub header_type: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub grpc_authority: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub grpc_mode: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub xhttp_mode: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub xhttp_extra: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kcp_seed: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kcp_mtu: Option<u32>,

    #[serde(default = "default_security")]
    pub security: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sni: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub public_key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub short_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub spider_x: Option<String>,
    #[serde(default)]
    pub allow_insecure: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub vless_encryption: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mldsa65_verify: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ech_config_list: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub verify_peer_cert_by_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cert_sha256: Option<String>,

    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_public_key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_preshared_key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_address: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_mtu: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_reserved: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_dns: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wg_keepalive: Option<u32>,

    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub gecko_min_packet_size: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub gecko_max_packet_size: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub plugin: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub plugin_opts: Option<String>,

    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub extras: BTreeMap<String, String>,

    #[serde(default)]
    pub tcp_delay_ms: i32,
    #[serde(default)]
    pub real_delay_ms: i32,
}

fn default_network() -> String {
    "tcp".to_string()
}

fn default_security() -> String {
    "none".to_string()
}

impl Default for Node {
    fn default() -> Self {
        Self {
            name: String::new(),
            protocol: Protocol::Unknown,
            server: String::new(),
            port: 0,
            uuid: None,
            password: None,
            method: None,
            username: None,
            alter_id: 0,
            cipher: None,
            flow: None,
            network: default_network(),
            path: None,
            host_header: None,
            service_name: None,
            header_type: None,
            grpc_authority: None,
            grpc_mode: None,
            xhttp_mode: None,
            xhttp_extra: None,
            kcp_seed: None,
            kcp_mtu: None,
            security: default_security(),
            sni: None,
            fingerprint: None,
            public_key: None,
            short_id: None,
            spider_x: None,
            allow_insecure: false,
            vless_encryption: None,
            mldsa65_verify: None,
            ech_config_list: None,
            verify_peer_cert_by_name: None,
            cert_sha256: None,
            wg_public_key: None,
            wg_preshared_key: None,
            wg_address: None,
            wg_mtu: None,
            wg_reserved: None,
            wg_dns: None,
            wg_keepalive: None,
            gecko_min_packet_size: None,
            gecko_max_packet_size: None,
            plugin: None,
            plugin_opts: None,
            extras: BTreeMap::new(),
            tcp_delay_ms: 0,
            real_delay_ms: 0,
        }
    }
}

impl Node {
    pub fn is_valid(&self) -> bool {
        !self.server.is_empty() && self.port > 0 && self.protocol != Protocol::Unknown
    }

    pub fn endpoint(&self) -> String {
        if self.server.contains(':') {
            format!("[{}]:{}", self.server, self.port)
        } else {
            format!("{}:{}", self.server, self.port)
        }
    }

    pub fn to_json(&self) -> Result<String, serde_json::Error> {
        serde_json::to_string(self)
    }

    pub fn to_json_pretty(&self) -> Result<String, serde_json::Error> {
        serde_json::to_string_pretty(self)
    }

    pub fn from_json(json: &str) -> Result<Self, serde_json::Error> {
        serde_json::from_str(json)
    }
}
