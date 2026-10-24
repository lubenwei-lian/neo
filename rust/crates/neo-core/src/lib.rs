//! neo-core：neo Rust 的核心库。
//!
//! 本库包含可独立测试的纯逻辑：
//! - 节点模型与 JSON 序列化；
//! - 分享链接解析；
//! - TCP 握手延迟测试。

pub mod clash;
pub mod delay;
pub mod dns;
pub mod failover;
pub mod kernel;
pub mod logging;
pub mod node;
pub mod restart;
// 路由/分流规则的纯数据模型（不依赖内核，可单测）。
pub mod route;
pub mod share_link;
pub mod singbox;
pub mod subscription;
pub mod subscription_fetch;
pub mod usage;

pub use delay::{
    parse_host_port, successful, DelayConfig, DelayError, DelayOutcome, DelayTarget, TcpDelayTester,
};
pub use node::{Node, NodeProtocol, Protocol};
pub use subscription::{detect_placeholder, PlaceholderReason};
pub use usage::{format_bytes, parse_userinfo, SubscriptionUsage};
pub use route::{first_match, Outbound, RouteRule};
pub use share_link::{parse_many, parse_share_link, ParseError};
pub use singbox::{
    build_config, build_config_json, map_log_level, ConfigError, ProxyMode, SingBoxOptions,
    GROUP_AUTO, GROUP_FINAL, GROUP_SELECT,
};
pub use subscription_fetch::{
    fetch_subscription, parse_subscription_body, FetchError, FetchOptions, FetchOutcome,
    HttpTransport, ParsedSubscription,
};
