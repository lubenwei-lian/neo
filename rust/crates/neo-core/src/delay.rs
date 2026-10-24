//! TCP 握手延迟测试（Tcping）。
//!
//! TCP 握手延迟测试实现。
//! 语义子集：
//! - 并发跑多个目标（信号量限制并发）；
//! - 每个目标多次取样，逐样本超时；
//! - 只统计成功的 TCP 建连耗时，得到 min / median；
//! - DNS 解析不计入握手耗时（先解析，再计时 connect）；
//! - 失败不 panic，返回结构化错误，调用方可以过滤。

use std::fmt;
use std::net::{IpAddr, SocketAddr};
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use tokio::net::TcpStream;
use tokio::sync::Semaphore;
use tokio::time::timeout;

/// 延迟测试目标（host + port）。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DelayTarget {
    pub host: String,
    pub port: u16,
}

impl DelayTarget {
    pub fn new(host: impl Into<String>, port: u16) -> Self {
        Self {
            host: host.into(),
            port,
        }
    }

    /// 解析 host:port 或 [IPv6]:port。
    pub fn parse(input: &str) -> Result<Self, DelayError> {
        let (host, port) = parse_host_port(input)?;
        Ok(Self { host, port })
    }

    pub fn endpoint(&self) -> String {
        if self.host.contains(':') {
            format!("[{}]:{}", self.host, self.port)
        } else {
            format!("{}:{}", self.host, self.port)
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DelayError {
    message: String,
}

impl DelayError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for DelayError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for DelayError {}

/// 解析 <host>:<port>；IPv6 必须加方括号。端口必须是 1..=65535。
pub fn parse_host_port(input: &str) -> Result<(String, u16), DelayError> {
    let text = input.trim();
    if text.is_empty() {
        return Err(DelayError::new("地址为空"));
    }
    if text.contains("://") {
        return Err(DelayError::new("只接受 host:port，不要带协议前缀"));
    }
    let host;
    let port_text;
    if let Some(rest) = text.strip_prefix('[') {
        let close = rest
            .find(']')
            .ok_or_else(|| DelayError::new("IPv6 地址缺少 ]"))?;
        host = &rest[..close];
        let after = &rest[close + 1..];
        port_text = after
            .strip_prefix(':')
            .ok_or_else(|| DelayError::new("IPv6 地址后缺少 :port"))?;
        if host.is_empty() {
            return Err(DelayError::new("主机名为空"));
        }
    } else {
        let (h, p) = text
            .rsplit_once(':')
            .ok_or_else(|| DelayError::new("缺少端口，应写成 host:port"))?;
        if h.is_empty() {
            return Err(DelayError::new("主机名为空"));
        }
        if h.contains(':') {
            return Err(DelayError::new("IPv6 地址必须用方括号包裹，例如 [::1]:443"));
        }
        host = h;
        port_text = p;
    }
    if host.chars().any(char::is_whitespace) || host.contains('/') {
        return Err(DelayError::new(format!("主机名无效: {host}")));
    }
    if port_text.is_empty() {
        return Err(DelayError::new("缺少端口"));
    }
    let port: u32 = port_text
        .parse()
        .map_err(|_| DelayError::new(format!("端口不是数字: {port_text}")))?;
    if port == 0 || port > u16::MAX as u32 {
        return Err(DelayError::new(format!("端口超出范围: {port}")));
    }
    Ok((host.to_string(), port as u16))
}

/// 测速参数。
#[derive(Debug, Clone)]
pub struct DelayConfig {
    /// 同时进行的最大目标数。
    pub concurrency: usize,
    /// 单次 TCP 建连超时。
    pub timeout: Duration,
    /// 每个目标的取样次数。
    pub samples: usize,
    /// 整批总预算；None 表示不设总预算。
    pub total_timeout: Option<Duration>,
}

impl Default for DelayConfig {
    fn default() -> Self {
        Self {
            concurrency: 16,
            timeout: Duration::from_millis(3000),
            samples: 3,
            total_timeout: Some(Duration::from_secs(15)),
        }
    }
}

impl DelayConfig {
    pub fn validate(&self) -> Result<(), DelayError> {
        if self.concurrency == 0 {
            return Err(DelayError::new("并发数必须大于 0"));
        }
        if self.timeout.is_zero() {
            return Err(DelayError::new("超时时间必须大于 0"));
        }
        if self.samples == 0 {
            return Err(DelayError::new("取样次数必须大于 0"));
        }
        if let Some(total) = self.total_timeout {
            if total.is_zero() {
                return Err(DelayError::new("总预算必须大于 0"));
            }
        }
        Ok(())
    }
}

/// 一个目标的测试结果。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DelayOutcome {
    pub target: DelayTarget,
    pub ok: bool,
    /// 成功的取样（毫秒，按完成顺序）。
    pub samples_ms: Vec<u32>,
    /// 未成功/未执行的样本数。
    pub failures: usize,
    pub min_ms: Option<u32>,
    pub median_ms: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

impl DelayOutcome {
    fn build(
        target: DelayTarget,
        samples_ms: Vec<u32>,
        failures: usize,
        error: Option<String>,
    ) -> Self {
        let min_ms = samples_ms.iter().copied().min();
        let median_ms = median(&samples_ms);
        Self {
            target,
            ok: !samples_ms.is_empty(),
            samples_ms,
            failures,
            min_ms,
            median_ms,
            error,
        }
    }

    fn all_failed(target: DelayTarget, failures: usize, error: impl Into<String>) -> Self {
        Self::build(target, Vec::new(), failures, Some(error.into()))
    }
}

/// 失败过滤：调用方展示时只保留成功目标。返回引用，避免复制。
pub fn successful(outcomes: &[DelayOutcome]) -> Vec<&DelayOutcome> {
    outcomes.iter().filter(|outcome| outcome.ok).collect()
}

/// 可复用的测速器；内部无全局状态，clone 成本低。
#[derive(Debug, Clone)]
pub struct TcpDelayTester {
    config: DelayConfig,
}

impl TcpDelayTester {
    pub fn new(config: DelayConfig) -> Result<Self, DelayError> {
        config.validate()?;
        Ok(Self { config })
    }

    pub fn with_defaults() -> Self {
        Self {
            config: DelayConfig::default(),
        }
    }

    pub fn config(&self) -> &DelayConfig {
        &self.config
    }

    /// 并发测试一批目标；返回顺序与输入一致（含失败项）。
    pub async fn test_targets(&self, targets: &[DelayTarget]) -> Vec<DelayOutcome> {
        if targets.is_empty() {
            return Vec::new();
        }
        let deadline = self
            .config
            .total_timeout
            .and_then(|budget| Instant::now().checked_add(budget));
        let semaphore = Arc::new(Semaphore::new(self.config.concurrency));
        let mut handles = Vec::with_capacity(targets.len());
        for (index, target) in targets.iter().enumerate() {
            let target = target.clone();
            let tester = self.clone();
            let sem = Arc::clone(&semaphore);
            handles.push((
                index,
                tokio::spawn(async move { tester.run_target(target, sem, deadline).await }),
            ));
        }
        let mut results: Vec<Option<DelayOutcome>> = (0..targets.len()).map(|_| None).collect();
        for (index, handle) in handles {
            match handle.await {
                Ok(outcome) => results[index] = Some(outcome),
                Err(_) => {
                    results[index] = Some(DelayOutcome::all_failed(
                        targets[index].clone(),
                        1,
                        "测速任务异常终止",
                    ));
                }
            }
        }
        results.into_iter().flatten().collect()
    }

    /// 单目标便捷入口。
    pub async fn test_target(&self, target: DelayTarget) -> DelayOutcome {
        let mut outcomes = self.test_targets(&[target]).await;
        outcomes
            .pop()
            .unwrap_or_else(|| DelayOutcome::all_failed(DelayTarget::new("", 0), 1, "没有结果"))
    }

    async fn run_target(
        &self,
        target: DelayTarget,
        semaphore: Arc<Semaphore>,
        deadline: Option<Instant>,
    ) -> DelayOutcome {
        // 等待并发槽位，同时受总预算约束。
        let permit = if let Some(deadline) = deadline {
            match remaining(deadline) {
                Some(left) => match timeout(left, semaphore.acquire()).await {
                    Ok(Ok(permit)) => Some(permit),
                    Ok(Err(_)) => {
                        return DelayOutcome::all_failed(
                            target,
                            self.config.samples,
                            "并发控制器已关闭",
                        );
                    }
                    Err(_) => {
                        return DelayOutcome::all_failed(
                            target,
                            self.config.samples,
                            "总预算耗尽，未开始测速",
                        );
                    }
                },
                None => {
                    return DelayOutcome::all_failed(
                        target,
                        self.config.samples,
                        "总预算耗尽，未开始测速",
                    );
                }
            }
        } else {
            match semaphore.acquire().await {
                Ok(permit) => Some(permit),
                Err(_) => {
                    return DelayOutcome::all_failed(
                        target,
                        self.config.samples,
                        "并发控制器已关闭",
                    );
                }
            }
        };
        let _permit = permit;

        let resolve_budget = deadline.and_then(remaining).unwrap_or(self.config.timeout);
        let addrs = match resolve_target(&target.host, target.port, resolve_budget).await {
            Ok(addrs) if !addrs.is_empty() => addrs,
            Ok(_) => {
                return DelayOutcome::all_failed(target, self.config.samples, "DNS 未返回任何地址");
            }
            Err(error) => {
                return DelayOutcome::all_failed(target, self.config.samples, error);
            }
        };

        let mut samples_ms = Vec::with_capacity(self.config.samples);
        let mut last_error: Option<String> = None;
        let mut budget_hit = false;
        for _ in 0..self.config.samples {
            let connect_budget = match deadline {
                Some(deadline) => match remaining(deadline) {
                    Some(left) => left.min(self.config.timeout),
                    None => {
                        budget_hit = true;
                        break;
                    }
                },
                None => self.config.timeout,
            };
            match connect_any(&addrs, connect_budget).await {
                Ok(ms) => samples_ms.push(ms),
                Err(error) => last_error = Some(error),
            }
        }
        let failures = self.config.samples.saturating_sub(samples_ms.len());
        let error = if samples_ms.is_empty() {
            Some(last_error.unwrap_or_else(|| {
                if budget_hit {
                    "总预算耗尽".to_string()
                } else {
                    "所有样本均失败".to_string()
                }
            }))
        } else {
            None
        };
        DelayOutcome::build(target, samples_ms, failures, error)
    }
}

impl Default for TcpDelayTester {
    fn default() -> Self {
        Self::with_defaults()
    }
}

fn remaining(deadline: Instant) -> Option<Duration> {
    let now = Instant::now();
    if now >= deadline {
        None
    } else {
        Some(deadline - now)
    }
}

async fn resolve_target(
    host: &str,
    port: u16,
    budget: Duration,
) -> Result<Vec<SocketAddr>, String> {
    if let Ok(ip) = host.parse::<IpAddr>() {
        return Ok(vec![SocketAddr::new(ip, port)]);
    }
    let lookup = tokio::net::lookup_host((host, port));
    match timeout(budget, lookup).await {
        Ok(Ok(addrs)) => Ok(addrs.collect()),
        Ok(Err(error)) => Err(format!("DNS 解析失败: {error}")),
        Err(_) => Err("DNS 解析超时".to_string()),
    }
}

async fn connect_any(addrs: &[SocketAddr], budget: Duration) -> Result<u32, String> {
    if addrs.is_empty() {
        return Err("没有可连接的地址".to_string());
    }
    let started = Instant::now();
    let attempt = async {
        let mut last_error = "所有地址均连接失败".to_string();
        for addr in addrs {
            match TcpStream::connect(*addr).await {
                Ok(stream) => {
                    drop(stream);
                    return Ok(());
                }
                Err(error) => {
                    last_error = format!("{addr}: {error}");
                }
            }
        }
        Err::<(), String>(last_error)
    };
    match timeout(budget, attempt).await {
        Ok(Ok(())) => {
            let millis = started.elapsed().as_millis();
            Ok(millis.clamp(1, u32::MAX as u128) as u32)
        }
        Ok(Err(error)) => Err(error),
        Err(_) => Err("TCP 连接超时".to_string()),
    }
}

fn median(samples: &[u32]) -> Option<u32> {
    if samples.is_empty() {
        return None;
    }
    let mut sorted = samples.to_vec();
    sorted.sort_unstable();
    let middle = sorted.len() / 2;
    if sorted.len() % 2 == 0 {
        Some((((sorted[middle - 1] as u64) + (sorted[middle] as u64)) / 2) as u32)
    } else {
        Some(sorted[middle])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_host_port_and_ipv6() {
        assert_eq!(
            parse_host_port("example.com:443").unwrap(),
            ("example.com".to_string(), 443)
        );
        assert_eq!(
            parse_host_port(" [2001:db8::1]:8443 ").unwrap(),
            ("2001:db8::1".to_string(), 8443)
        );
        assert_eq!(
            DelayTarget::parse("127.0.0.1:1").unwrap().endpoint(),
            "127.0.0.1:1"
        );
        assert_eq!(
            DelayTarget::parse("[::1]:1080").unwrap().endpoint(),
            "[::1]:1080"
        );
    }

    #[test]
    fn rejects_bad_targets_without_panicking() {
        for input in [
            "",
            "example.com",
            "example.com:0",
            "example.com:65536",
            "https://example.com:443",
            "2001:db8::1:443",
            "[2001:db8::1]",
            "host:abc",
            "[::1]443",
        ] {
            let result = std::panic::catch_unwind(|| parse_host_port(input));
            assert!(result.is_ok(), "parse_host_port panicked on {input:?}");
            assert!(result.unwrap().is_err(), "accepted bad target {input:?}");
        }
    }

    #[test]
    fn median_handles_even_and_odd() {
        assert_eq!(median(&[]), None);
        assert_eq!(median(&[9]), Some(9));
        assert_eq!(median(&[9, 1, 5]), Some(5));
        assert_eq!(median(&[10, 2, 8, 4]), Some(6));
    }

    #[tokio::test]
    async fn samples_local_listener_and_reports_min_median() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .expect("bind local listener");
        let addr = listener.local_addr().expect("local addr");
        let server = tokio::spawn(async move {
            loop {
                if listener.accept().await.is_err() {
                    break;
                }
            }
        });
        let tester = TcpDelayTester::new(DelayConfig {
            concurrency: 4,
            timeout: Duration::from_millis(500),
            samples: 3,
            total_timeout: Some(Duration::from_secs(5)),
        })
        .unwrap();
        let outcomes = tester
            .test_targets(&[DelayTarget::new("127.0.0.1", addr.port())])
            .await;
        server.abort();
        assert_eq!(outcomes.len(), 1);
        let outcome = &outcomes[0];
        assert!(outcome.ok, "expected success: {outcome:?}");
        assert_eq!(outcome.samples_ms.len(), 3);
        assert_eq!(outcome.failures, 0);
        assert!(outcome.min_ms.unwrap() >= 1);
        assert!(outcome.median_ms.unwrap() >= outcome.min_ms.unwrap());
    }

    #[tokio::test]
    async fn closed_port_is_a_structured_failure() {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").expect("bind");
        let port = listener.local_addr().unwrap().port();
        drop(listener);
        let tester = TcpDelayTester::new(DelayConfig {
            concurrency: 1,
            timeout: Duration::from_millis(200),
            samples: 2,
            total_timeout: Some(Duration::from_secs(3)),
        })
        .unwrap();
        let outcomes = tester
            .test_targets(&[DelayTarget::new("127.0.0.1", port)])
            .await;
        assert_eq!(outcomes.len(), 1);
        let outcome = &outcomes[0];
        assert!(!outcome.ok);
        assert!(outcome.samples_ms.is_empty());
        assert_eq!(outcome.failures, 2);
        assert!(outcome.min_ms.is_none());
        assert!(outcome.error.is_some());
        assert!(successful(&outcomes).is_empty());
    }

    #[tokio::test]
    async fn multiple_targets_keep_input_order() {
        // 用两个 127.0.0.1 监听器：早期版本用 "localhost" 做第二个目标，在同时存在
        // ::1/127.0.0.1 的机器上会因解析顺序偶发失败（flaky），与排序语义无关。
        async fn spawn_listener() -> u16 {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
                .await
                .expect("bind");
            let port = listener.local_addr().unwrap().port();
            tokio::spawn(async move {
                loop {
                    if listener.accept().await.is_err() {
                        break;
                    }
                }
            });
            port
        }
        let p1 = spawn_listener().await;
        let p2 = spawn_listener().await;
        let tester = TcpDelayTester::new(DelayConfig {
            concurrency: 2,
            timeout: Duration::from_millis(500),
            samples: 1,
            total_timeout: Some(Duration::from_secs(5)),
        })
        .unwrap();
        let outcomes = tester
            .test_targets(&[
                DelayTarget::new("127.0.0.1", p1),
                DelayTarget::new("127.0.0.1", p2),
            ])
            .await;
        assert_eq!(outcomes.len(), 2);
        assert_eq!(outcomes[0].target.port, p1);
        assert_eq!(outcomes[1].target.port, p2);
        assert!(outcomes.iter().all(|outcome| outcome.ok));
    }

    #[test]
    fn config_validation_rejects_zero() {
        let no_samples = DelayConfig {
            samples: 0,
            ..DelayConfig::default()
        };
        assert!(no_samples.validate().is_err());
        let zero_timeout = DelayConfig {
            timeout: Duration::ZERO,
            ..DelayConfig::default()
        };
        assert!(zero_timeout.validate().is_err());
    }
}
