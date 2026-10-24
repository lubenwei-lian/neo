//! 订阅抓取：HTTP(S) 拉取 + UA 链 + `subscription-userinfo` 解析。
//!
//! 语义对照（只读参考）：
//! - Windows 端 `Neo.Core/Parsing/SubscriptionFetcher.cs`：直连优先 → 代理兜底；
//!   UA `v2rayN/7.24.8`；额度探测 UA 链 `ClashforWindows/0.20.39` → `sing-box/1.14.2`；
//! - Android 端 SubGuard.open：先试 127.0.0.1:7891，再试 21809 的本机代理候选。
//!
//! 设计要点：
//! - 网络层抽象成 [`HttpTransport`]，单测用 Mock 覆盖「直连失败→代理」「UA 链」等分支，
//!   不依赖外网；真实实现 [`MinreqTransport`] 用 minreq + native-tls（Windows 走 schannel）。
//! - 正文解析（base64 双层、元信息行、占位订阅识别）是纯函数，可独立单测。
//! - 只支持「分享链接」订阅；Clash YAML / SIP008 明确报错。

use std::fmt;

use base64::engine::general_purpose::STANDARD;
use base64::Engine as _;

use crate::node::Node;
use crate::share_link::parse_share_link;
use crate::subscription::{detect_placeholder, PlaceholderReason};
use crate::usage::{parse_userinfo, SubscriptionUsage};

/// 拉取订阅正文的 UA（与Windows 端一致）。
pub const CONTENT_USER_AGENT: &str = "v2rayN/7.24.8";
/// 额度探测 UA 链（与Windows 端一致）。
pub const USAGE_PROBE_USER_AGENTS: &[&str] = &["ClashforWindows/0.20.39", "sing-box/1.14.2"];
/// 本机代理候选（手机 SubGuard.open：7891 → 21809）。
pub const DEFAULT_PROXY_CANDIDATES: &[&str] = &["http://127.0.0.1:7891", "http://127.0.0.1:21809"];
/// 单次 HTTP 请求默认超时（秒）。直连失败 + 两个代理候选，最坏约 36s。
pub const DEFAULT_TIMEOUT_SECS: u64 = 12;
/// 正文大小上限（防止被异常服务端灌爆内存）。
pub const DEFAULT_MAX_BODY_BYTES: usize = 8 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FetchOptions {
    /// 拉正文的 UA。
    pub user_agent: String,
    /// 正文没有 subscription-userinfo 时依次尝试的额度探测 UA。
    pub usage_user_agents: Vec<String>,
    /// 直连失败后依次尝试的本地代理。
    pub proxy_candidates: Vec<String>,
    /// 是否先直连（默认 true）。
    pub allow_direct: bool,
    pub timeout_secs: u64,
    pub max_body_bytes: usize,
}

impl Default for FetchOptions {
    fn default() -> Self {
        Self {
            user_agent: CONTENT_USER_AGENT.into(),
            usage_user_agents: USAGE_PROBE_USER_AGENTS.iter().map(|s| s.to_string()).collect(),
            proxy_candidates: DEFAULT_PROXY_CANDIDATES.iter().map(|s| s.to_string()).collect(),
            allow_direct: true,
            timeout_secs: DEFAULT_TIMEOUT_SECS,
            max_body_bytes: DEFAULT_MAX_BODY_BYTES,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpRequest {
    pub url: String,
    pub user_agent: String,
    pub proxy: Option<String>,
    pub timeout_secs: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: String,
}

impl HttpResponse {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value.as_str())
    }
}

/// HTTP 传输层抽象（真实实现见 [`MinreqTransport`]，测试用 Mock）。
pub trait HttpTransport {
    fn get(&self, request: &HttpRequest) -> Result<HttpResponse, FetchError>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FetchError {
    InvalidUrl(String),
    Transport(String),
    HttpStatus { status: u16, url: String },
    EmptyBody,
    NotSubscription { reason: String },
    BodyTooLarge { bytes: usize, limit: usize },
}

impl fmt::Display for FetchError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            FetchError::InvalidUrl(url) => write!(f, "订阅地址无效：{url}（只支持 http:// 或 https://）"),
            FetchError::Transport(message) => write!(f, "网络请求失败：{message}"),
            FetchError::HttpStatus { status, url } => write!(f, "订阅返回 HTTP {status}：{url}"),
            FetchError::EmptyBody => f.write_str("订阅内容为空"),
            FetchError::NotSubscription { reason } => write!(f, "订阅内容无法识别：{reason}"),
            FetchError::BodyTooLarge { bytes, limit } => {
                write!(f, "订阅正文过大（{bytes} 字节，上限 {limit}）")
            }
        }
    }
}

impl std::error::Error for FetchError {}

/// 一次成功拉取的结果（正文与额度头）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FetchOutcome {
    pub url: String,
    pub status: u16,
    pub body: String,
    pub headers: Vec<(String, String)>,
    /// 解析后的额度（没有 `subscription-userinfo` 时为 None）。
    pub usage: Option<SubscriptionUsage>,
    /// 原始 `subscription-userinfo` 响应头。
    pub userinfo: Option<String>,
    /// 实际命中额度头的 UA（正文 UA 命中时为 None）。
    pub usage_user_agent: Option<String>,
    /// 实际使用正文 UA 的 UA。
    pub content_user_agent: String,
    /// None = 直连；Some = 命中的本地代理。
    pub used_proxy: Option<String>,
}

/// 解析后的订阅正文。
#[derive(Debug, Clone, PartialEq)]
pub struct ParsedSubscription {
    pub nodes: Vec<Node>,
    /// 实际用于解析的文本（base64 订阅为解码后文本）。
    pub text: String,
    pub was_base64: bool,
    /// 服务商写在正文前几行的流量/到期提示。
    pub traffic_info: Option<String>,
    /// 占位/异常订阅判定（Android 端 SubGuard.check 同源逻辑）。
    pub placeholder: Option<PlaceholderReason>,
    /// 含 :// 但解析失败的行数。
    pub skipped: usize,
}

/// 从响应头解析 `subscription-userinfo`（返回额度与原始头）。
pub fn usage_from_headers(headers: &[(String, String)]) -> Option<(SubscriptionUsage, String)> {
    headers
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case("subscription-userinfo"))
        .and_then(|(_, value)| parse_userinfo(value).map(|usage| (usage, value.clone())))
}

/// 真实 HTTP(S) 传输：minreq + native-tls（Windows schannel）。
pub struct MinreqTransport;

impl HttpTransport for MinreqTransport {
    fn get(&self, request: &HttpRequest) -> Result<HttpResponse, FetchError> {
        let mut req = minreq::get(request.url.as_str())
            .with_header("User-Agent", request.user_agent.as_str())
            .with_timeout(request.timeout_secs);
        if let Some(proxy) = &request.proxy {
            let parsed = minreq::Proxy::new(proxy)
                .map_err(|error| FetchError::Transport(format!("代理地址无效 {proxy}：{error}")))?;
            req = req.with_proxy(parsed);
        }
        let response = req
            .send()
            .map_err(|error| FetchError::Transport(error.to_string()))?;
        let status = u16::try_from(response.status_code).unwrap_or(0);
        let headers = response
            .headers
            .iter()
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect();
        let body = response
            .as_str()
            .map_err(|error| FetchError::Transport(format!("响应正文不是 UTF-8：{error}")))?
            .to_string();
        Ok(HttpResponse { status, headers, body })
    }
}

/// 直连优先、代理兜底地拉取订阅。
pub fn fetch_subscription(url: &str, opts: &FetchOptions) -> Result<FetchOutcome, FetchError> {
    fetch_subscription_with(&MinreqTransport, url, opts)
}

/// 使用指定传输层拉取（单测注入 Mock）。
pub fn fetch_subscription_with<T: HttpTransport + ?Sized>(
    transport: &T,
    url: &str,
    opts: &FetchOptions,
) -> Result<FetchOutcome, FetchError> {
    validate_url(url)?;
    let mut candidates: Vec<Option<String>> = Vec::new();
    if opts.allow_direct {
        candidates.push(None);
    }
    candidates.extend(opts.proxy_candidates.iter().cloned().map(Some));
    if candidates.is_empty() {
        return Err(FetchError::InvalidUrl(format!(
            "{url}（allow_direct=false 且没有代理候选）"
        )));
    }

    let mut first_success: Option<FetchOutcome> = None;
    let mut last_error: Option<FetchError> = None;
    for proxy in candidates {
        match fetch_once(transport, url, proxy.as_deref(), opts) {
            Ok(outcome) => {
                if outcome.usage.is_some() {
                    return Ok(outcome);
                }
                if first_success.is_none() {
                    first_success = Some(outcome);
                }
            }
            Err(error) => last_error = Some(error),
        }
    }
    first_success.ok_or_else(|| {
        last_error.unwrap_or_else(|| FetchError::Transport("全部直连/代理候选均失败".into()))
    })
}

fn fetch_once<T: HttpTransport + ?Sized>(
    transport: &T,
    url: &str,
    proxy: Option<&str>,
    opts: &FetchOptions,
) -> Result<FetchOutcome, FetchError> {
    let request = |user_agent: &str| HttpRequest {
        url: url.to_string(),
        user_agent: user_agent.to_string(),
        proxy: proxy.map(str::to_string),
        timeout_secs: opts.timeout_secs,
    };

    let response = transport.get(&request(&opts.user_agent))?;
    ensure_ok(&response, url, opts)?;

    let (mut usage, mut userinfo, mut usage_user_agent) = match usage_from_headers(&response.headers) {
        Some((usage, raw)) => (Some(usage), Some(raw), None),
        None => (None, None, None),
    };

    if usage.is_none() {
        for ua in &opts.usage_user_agents {
            if ua == &opts.user_agent {
                continue;
            }
            let Ok(probe) = transport.get(&request(ua)) else {
                continue;
            };
            if ensure_ok(&probe, url, opts).is_err() {
                continue;
            }
            if let Some((probed, raw)) = usage_from_headers(&probe.headers) {
                usage = Some(probed);
                userinfo = Some(raw);
                usage_user_agent = Some(ua.clone());
                break;
            }
        }
    }

    Ok(FetchOutcome {
        url: url.to_string(),
        status: response.status,
        body: response.body,
        headers: response.headers,
        usage,
        userinfo,
        usage_user_agent,
        content_user_agent: opts.user_agent.clone(),
        used_proxy: proxy.map(str::to_string),
    })
}

fn ensure_ok(response: &HttpResponse, url: &str, opts: &FetchOptions) -> Result<(), FetchError> {
    if !(200..300).contains(&response.status) {
        return Err(FetchError::HttpStatus { status: response.status, url: url.to_string() });
    }
    if response.body.len() > opts.max_body_bytes {
        return Err(FetchError::BodyTooLarge {
            bytes: response.body.len(),
            limit: opts.max_body_bytes,
        });
    }
    Ok(())
}

fn validate_url(url: &str) -> Result<(), FetchError> {
    let trimmed = url.trim();
    let lower = trimmed.to_ascii_lowercase();
    for scheme in ["http://", "https://"] {
        if let Some(rest) = lower.strip_prefix(scheme) {
            let original_rest = &trimmed[scheme.len()..];
            if rest.is_empty() || original_rest.starts_with('/') {
                return Err(FetchError::InvalidUrl(url.to_string()));
            }
            return Ok(());
        }
    }
    Err(FetchError::InvalidUrl(url.to_string()))
}

/// 解析订阅正文：base64（含双层）→ 分享链接行；同时提取元信息行与占位判定。
pub fn parse_subscription_body(body: &str) -> Result<ParsedSubscription, FetchError> {
    let trimmed = body.trim();
    if trimmed.is_empty() {
        return Err(FetchError::EmptyBody);
    }
    let lower = trimmed.to_ascii_lowercase();
    if trimmed.starts_with('<') || lower.contains("<html") {
        return Err(FetchError::NotSubscription {
            reason: "返回的是网页而非节点内容（订阅可能已失效或需要续费）".into(),
        });
    }

    let compact: String = trimmed.chars().filter(|c| !c.is_whitespace()).collect();
    let decoded_once = try_b64_text(&compact);
    let decoded_twice = decoded_once.as_deref().and_then(|text| {
        let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect();
        try_b64_text(&compact)
    });

    let mut candidates: Vec<(String, bool)> = Vec::new();
    if let Some(text) = &decoded_once {
        if text != trimmed {
            candidates.push((text.clone(), true));
        }
    }
    candidates.push((trimmed.to_string(), false));
    if let Some(text) = &decoded_twice {
        if text != trimmed && Some(text) != decoded_once.as_ref() {
            candidates.push((text.clone(), true));
        }
    }

    let mut last_reason = String::new();
    for (text, was_base64) in &candidates {
        if looks_like_clash_yaml(text) {
            last_reason = "Clash YAML 订阅暂未支持（当前只支持分享链接订阅）".into();
            continue;
        }
        let parsed = parse_share_links(text);
        if parsed.nodes.is_empty() {
            if last_reason.is_empty() {
                last_reason = "未解析出任何有效分享链接（格式可能不支持）".into();
            }
            continue;
        }
        return Ok(ParsedSubscription {
            nodes: parsed.nodes,
            text: text.clone(),
            was_base64: *was_base64,
            traffic_info: parsed.traffic_info,
            placeholder: detect_placeholder(text),
            skipped: parsed.skipped,
        });
    }
    Err(FetchError::NotSubscription { reason: last_reason })
}

struct ParsedLinks {
    nodes: Vec<Node>,
    traffic_info: Option<String>,
    skipped: usize,
}

fn parse_share_links(text: &str) -> ParsedLinks {
    let lines: Vec<&str> = text
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .collect();
    let mut meta: Vec<String> = lines
        .iter()
        .filter(|line| !line.contains("://"))
        .take(3)
        .map(|line| line.to_string())
        .collect();

    let mut nodes = Vec::new();
    let mut skipped = 0usize;
    for line in lines {
        if !line.contains("://") {
            continue;
        }
        match parse_share_link(line) {
            Ok(node) if node.is_valid() => {
                if is_meta_name(&node.name) {
                    meta.push(node.name.trim().to_string());
                } else {
                    nodes.push(node);
                }
            }
            _ => skipped += 1,
        }
    }
    let traffic_info = if meta.is_empty() { None } else { Some(meta.join(" | ")) };
    ParsedLinks { nodes, traffic_info, skipped }
}

/// 与Windows 端 `SubscriptionFetcher.IsMetaName` 等价的前缀判定（不引入 regex 依赖）。
pub fn is_meta_name(name: &str) -> bool {
    const PREFIXES: &[&str] = &[
        "剩余流量",
        "已用流量",
        "已用",
        "套餐到期",
        "到期时间",
        "到期",
        "距离下次重置",
        "重置时间",
        "官网",
        "订阅地址",
    ];
    let trimmed = name.trim();
    PREFIXES.iter().any(|prefix| {
        trimmed.strip_prefix(prefix).is_some_and(|rest| {
            let rest = rest.trim_start();
            rest.starts_with(':') || rest.starts_with('：')
        })
    })
}

fn looks_like_clash_yaml(text: &str) -> bool {
    text.lines()
        .any(|line| !line.starts_with(char::is_whitespace) && line.starts_with("proxies:"))
}

fn try_b64_text(input: &str) -> Option<String> {
    let normalized: String = input
        .chars()
        .filter(|c| !c.is_whitespace())
        .map(|c| match c {
            '-' => '+',
            '_' => '/',
            other => other,
        })
        .collect();
    let trimmed = normalized.trim_end_matches('=');
    if trimmed.len() < 8 {
        return None;
    }
    let mut padded = trimmed.to_string();
    while padded.len() % 4 != 0 {
        padded.push('=');
    }
    let bytes = STANDARD.decode(padded.as_bytes()).ok()?;
    let text = String::from_utf8(bytes).ok()?;
    // 接受三种结果：分享链接、Clash YAML、或「仍是 base64」的中间层
    // （部分面板会 base64(base64(links))，第二层交给调用方继续解）。
    if text.contains("://") || text.contains("proxies:") || looks_like_plain_base64(&text) {
        Some(text)
    } else {
        None
    }
}

fn looks_like_plain_base64(text: &str) -> bool {
    let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect();
    compact.len() >= 16
        && compact
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '/' | '=' | '-' | '_'))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;

    const VLESS: &str = "vless://11111111-1111-4111-8111-111111111111@vless.example.com:443?security=tls&type=ws&path=%2Fws&sni=vless.example.com#VLESS-A";
    const TROJAN: &str = "trojan://p%40ss@trojan.example.com:443?sni=t.example.com#TROJAN-B";

    struct MockTransport {
        script: RefCell<Vec<Result<HttpResponse, FetchError>>>,
        requests: RefCell<Vec<HttpRequest>>,
    }

    impl MockTransport {
        fn new(script: Vec<Result<HttpResponse, FetchError>>) -> Self {
            Self { script: RefCell::new(script), requests: RefCell::new(Vec::new()) }
        }
    }

    impl HttpTransport for MockTransport {
        fn get(&self, request: &HttpRequest) -> Result<HttpResponse, FetchError> {
            self.requests.borrow_mut().push(request.clone());
            let mut script = self.script.borrow_mut();
            if script.is_empty() {
                return Err(FetchError::Transport("mock 脚本已用尽".into()));
            }
            script.remove(0)
        }
    }

    fn response(status: u16, headers: &[(&str, &str)], body: &str) -> HttpResponse {
        HttpResponse {
            status,
            headers: headers.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect(),
            body: body.to_string(),
        }
    }

    fn userinfo() -> HttpResponse {
        response(
            200,
            &[(
                "subscription-userinfo",
                "upload=5335693168; download=363024435813; total=1073741824000; expire=1760000000",
            )],
            VLESS,
        )
    }

    #[test]
    fn direct_success_uses_direct_and_parses_userinfo() {
        let mock = MockTransport::new(vec![Ok(userinfo())]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert_eq!(outcome.status, 200);
        assert_eq!(outcome.used_proxy, None);
        let usage = outcome.usage.unwrap();
        assert_eq!(usage.upload, 5_335_693_168);
        assert_eq!(usage.total, 1_073_741_824_000);
        assert!(outcome.userinfo.as_deref().unwrap().contains("total="));
        assert_eq!(mock.requests.borrow().len(), 1);
        assert_eq!(mock.requests.borrow()[0].user_agent, CONTENT_USER_AGENT);
    }

    #[test]
    fn direct_failure_falls_back_to_7891_then_21809() {
        let mock = MockTransport::new(vec![
            Err(FetchError::Transport("timed out".into())),
            Err(FetchError::Transport("refused".into())),
            Ok(userinfo()),
        ]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert_eq!(outcome.used_proxy.as_deref(), Some("http://127.0.0.1:21809"));
        let requests = mock.requests.borrow();
        assert_eq!(requests.len(), 3);
        assert_eq!(requests[0].proxy, None);
        assert_eq!(requests[1].proxy.as_deref(), Some("http://127.0.0.1:7891"));
        assert_eq!(requests[2].proxy.as_deref(), Some("http://127.0.0.1:21809"));
    }

    #[test]
    fn ua_chain_probes_when_content_ua_has_no_quota_header() {
        let mock = MockTransport::new(vec![
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(userinfo()),
        ]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert!(outcome.usage.is_some());
        assert_eq!(outcome.usage_user_agent.as_deref(), Some("sing-box/1.14.2"));
        let requests = mock.requests.borrow();
        assert_eq!(requests.len(), 3);
        assert_eq!(requests[1].user_agent, "ClashforWindows/0.20.39");
        assert_eq!(requests[2].user_agent, "sing-box/1.14.2");
        assert!(requests.iter().all(|r| r.proxy.is_none()));
    }

    #[test]
    fn quota_probe_falls_through_to_next_proxy() {
        let mock = MockTransport::new(vec![
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(userinfo()),
        ]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert!(outcome.usage.is_some());
        assert_eq!(outcome.used_proxy.as_deref(), Some("http://127.0.0.1:7891"));
        assert_eq!(outcome.usage_user_agent, None);
        assert_eq!(mock.requests.borrow().len(), 4);
    }

    #[test]
    fn first_successful_body_is_returned_when_no_quota_anywhere() {
        let mock = MockTransport::new(vec![
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
            Ok(response(200, &[], VLESS)),
        ]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert_eq!(outcome.used_proxy, None);
        assert!(outcome.usage.is_none());
        assert_eq!(outcome.body, VLESS);
    }

    #[test]
    fn http_error_is_reported_and_falls_back() {
        let mock = MockTransport::new(vec![
            Ok(response(404, &[], "not found")),
            Ok(userinfo()),
        ]);
        let outcome = fetch_subscription_with(&mock, "https://sub.example.com/x", &FetchOptions::default()).unwrap();
        assert_eq!(outcome.used_proxy.as_deref(), Some("http://127.0.0.1:7891"));
    }

    #[test]
    fn invalid_url_is_rejected_without_requests() {
        let mock = MockTransport::new(vec![]);
        let error = fetch_subscription_with(&mock, "ftp://example.com/sub", &FetchOptions::default()).unwrap_err();
        assert!(matches!(error, FetchError::InvalidUrl(_)));
        assert!(mock.requests.borrow().is_empty());
    }

    #[test]
    fn body_limit_is_enforced() {
        let mut opts = FetchOptions::default();
        opts.max_body_bytes = 4;
        opts.proxy_candidates.clear();
        let mock = MockTransport::new(vec![Ok(response(200, &[], "0123456789"))]);
        let error = fetch_subscription_with(&mock, "https://sub.example.com/x", &opts).unwrap_err();
        assert!(matches!(error, FetchError::BodyTooLarge { .. }));
    }

    #[test]
    fn parses_plain_link_text_with_meta_lines() {
        let text = format!("剩余流量：1.5 TB\n官网：example.com\n{VLESS}\nvless://broken\n{TROJAN}");
        let parsed = parse_subscription_body(&text).unwrap();
        assert_eq!(parsed.nodes.len(), 2);
        assert_eq!(parsed.nodes[0].name, "VLESS-A");
        assert_eq!(parsed.nodes[1].name, "TROJAN-B");
        assert!(!parsed.was_base64);
        assert_eq!(parsed.skipped, 1);
        let info = parsed.traffic_info.unwrap();
        assert!(info.contains("剩余流量：1.5 TB"));
        assert!(info.contains("官网"));
        assert!(parsed.placeholder.is_none());
    }

    #[test]
    fn parses_base64_and_double_base64() {
        let links = format!("{VLESS}\n{TROJAN}");
        let once = STANDARD.encode(links.as_bytes());
        let twice = STANDARD.encode(once.as_bytes());

        let parsed_once = parse_subscription_body(&once).unwrap();
        assert_eq!(parsed_once.nodes.len(), 2);
        assert!(parsed_once.was_base64);
        assert_eq!(parsed_once.text, links);

        let parsed_twice = parse_subscription_body(&twice).unwrap();
        assert_eq!(parsed_twice.nodes.len(), 2);
        assert!(parsed_twice.was_base64);
        assert_eq!(parsed_twice.text, links);
    }

    #[test]
    fn base64url_without_padding_is_supported() {
        let links = format!("{VLESS}\n{TROJAN}");
        let encoded = STANDARD.encode(links.as_bytes());
        let mut url_safe = encoded.replace('+', "-").replace('/', "_");
        while url_safe.ends_with('=') {
            url_safe.pop();
        }
        let parsed = parse_subscription_body(&url_safe).unwrap();
        assert_eq!(parsed.nodes.len(), 2);
    }

    #[test]
    fn placeholder_subscription_is_flagged() {
        let text = "剩余流量：0 GB\nvless://22222222-2222-4222-8222-222222222222@127.0.0.1:443#使用的设备过多";
        let parsed = parse_subscription_body(text).unwrap();
        assert_eq!(parsed.nodes.len(), 1);
        assert!(parsed.placeholder.is_some());
    }

    #[test]
    fn html_and_empty_and_clash_are_rejected() {
        assert!(matches!(parse_subscription_body("<html>oops</html>").unwrap_err(), FetchError::NotSubscription { .. }));
        assert!(matches!(parse_subscription_body("   ").unwrap_err(), FetchError::EmptyBody));
        let yaml = "proxies:\n  - {name: a, type: ss, server: x, port: 1}";
        let error = parse_subscription_body(yaml).unwrap_err();
        assert!(matches!(error, FetchError::NotSubscription { .. }));
        assert!(error.to_string().contains("Clash YAML"));
        assert!(matches!(parse_subscription_body("just text").unwrap_err(), FetchError::NotSubscription { .. }));
    }

    #[test]
    fn meta_name_prefix_requires_colon() {
        assert!(is_meta_name("剩余流量：10 GB"));
        assert!(is_meta_name("官网: https://example.com"));
        assert!(!is_meta_name("剩余流量节点"));
        assert!(!is_meta_name("日本节点"));
        assert!(!is_meta_name("已用流量"));
    }

    #[test]
    fn default_options_match_desktop_and_subguard() {
        let opts = FetchOptions::default();
        assert_eq!(opts.user_agent, "v2rayN/7.24.8");
        assert_eq!(opts.usage_user_agents, vec!["ClashforWindows/0.20.39", "sing-box/1.14.2"]);
        assert_eq!(
            opts.proxy_candidates,
            vec!["http://127.0.0.1:7891", "http://127.0.0.1:21809"]
        );
        assert!(opts.allow_direct);
    }

    #[test]
    fn userinfo_header_lookup_is_case_insensitive() {
        let headers = vec![("Subscription-UserInfo".to_string(), "upload=1; total=10".to_string())];
        let (usage, raw) = usage_from_headers(&headers).unwrap();
        assert_eq!(usage.upload, 1);
        assert_eq!(raw, "upload=1; total=10");
        assert!(usage_from_headers(&[("x".into(), "y".into())]).is_none());
    }
}
