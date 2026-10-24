//! 机场流量信息解析（`subscription-userinfo` 响应头，桌面/手机两端共用）。

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SubscriptionUsage {
    pub upload: u64,
    pub download: u64,
    pub total: u64,
    /// 到期时间（Unix 时间戳；0 = 未提供/长期有效由上层解释）
    pub expire: i64,
}

impl SubscriptionUsage {
    pub fn used(&self) -> u64 {
        self.upload.saturating_add(self.download)
    }

    pub fn remaining(&self) -> u64 {
        self.total.saturating_sub(self.used())
    }

    /// 已用比例（0.0–1.0）；total=0（不限量）返回 None。
    pub fn used_ratio(&self) -> Option<f64> {
        if self.total == 0 {
            None
        } else {
            Some((self.used() as f64 / self.total as f64).clamp(0.0, 1.0))
        }
    }
}

/// 解析 `upload=..; download=..; total=..; expire=..` 形式的响应头（未知键忽略）。
pub fn parse_userinfo(header: &str) -> Option<SubscriptionUsage> {
    if header.trim().is_empty() {
        return None;
    }
    let mut u = SubscriptionUsage::default();
    let mut seen = false;
    for part in header.split(';') {
        let Some((k, v)) = part.split_once('=') else { continue };
        let key = k.trim().to_ascii_lowercase();
        let val = v.trim().trim_matches('"');
        let parsed: u64 = match val.parse() {
            Ok(n) => n,
            Err(_) => continue,
        };
        match key.as_str() {
            "upload" => {
                u.upload = parsed;
                seen = true;
            }
            "download" => {
                u.download = parsed;
                seen = true;
            }
            "total" => {
                u.total = parsed;
                seen = true;
            }
            "expire" => {
                u.expire = parsed as i64;
                seen = true;
            }
            _ => {}
        }
    }
    if seen {
        Some(u)
    } else {
        None
    }
}

impl SubscriptionUsage {
    /// 一行摘要（供 CLI / 未来 UI 共用）：已用 X / 总量 Y（百分比）· 剩余 Z
    pub fn summary_line(&self) -> String {
        match self.used_ratio() {
            Some(r) => format!(
                "已用 {} / {}（{:.1}%）· 剩余 {}",
                format_bytes(self.used()),
                format_bytes(self.total),
                r * 100.0,
                format_bytes(self.remaining())
            ),
            None => format!(
                "已用 {}（不限量）· 上传 {} · 下载 {}",
                format_bytes(self.used()),
                format_bytes(self.upload),
                format_bytes(self.download)
            ),
        }
    }
}

/// 人类可读体积（B/KB/MB/GB/TB，保留一位小数）。
pub fn format_bytes(bytes: u64) -> String {
    const UNITS: &[&str] = &["B", "KB", "MB", "GB", "TB"];
    let mut v = bytes as f64;
    let mut idx = 0usize;
    while v >= 1024.0 && idx + 1 < UNITS.len() {
        v /= 1024.0;
        idx += 1;
    }
    if idx == 0 {
        format!("{} {}", bytes, UNITS[0])
    } else {
        format!("{:.1} {}", v, UNITS[idx])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_real_header() {
        let s = "upload=5335693168; download=363024435813; total=1073741824000; expire=";
        let u = parse_userinfo(s).expect("should parse");
        assert_eq!(u.upload, 5_335_693_168);
        assert_eq!(u.download, 363_024_435_813);
        assert_eq!(u.total, 1_073_741_824_000);
        assert_eq!(u.expire, 0);
        assert_eq!(u.used(), 368_360_128_981);
        assert_eq!(u.remaining(), u.total - u.used());
        assert!(u.used_ratio().unwrap() > 0.34 && u.used_ratio().unwrap() < 0.35);
    }

    #[test]
    fn ignores_unknown_and_bad_fields() {
        let u = parse_userinfo("foo=1; upload=10; bad=x; total=100").unwrap();
        assert_eq!(u.upload, 10);
        assert_eq!(u.total, 100);
        assert_eq!(u.download, 0);
    }

    #[test]
    fn empty_or_garbage_returns_none() {
        assert_eq!(parse_userinfo(""), None);
        assert_eq!(parse_userinfo("nothing=here"), None);
    }

    #[test]
    fn unlimited_total_has_no_ratio() {
        let u = SubscriptionUsage { upload: 1, download: 1, total: 0, expire: 0 };
        assert_eq!(u.used_ratio(), None);
    }

    #[test]
    fn summary_line_has_used_total_and_remaining() {
        let u = SubscriptionUsage { upload: 0, download: 512 * 1024 * 1024, total: 1024 * 1024 * 1024, expire: 0 };
        let s = u.summary_line();
        assert!(s.contains("512.0 MB"), "{s}");
        assert!(s.contains("1.0 GB"), "{s}");
        assert!(s.contains("50.0%"), "{s}");
        assert!(s.contains("512.0 MB"), "{s}");
    }

    #[test]
    fn unlimited_summary_mentions_unlimited() {
        let u = SubscriptionUsage { upload: 1, download: 2, total: 0, expire: 0 };
        assert!(u.summary_line().contains("不限量"));
    }

    #[test]
    fn formats_bytes() {
        assert_eq!(format_bytes(0), "0 B");
        assert_eq!(format_bytes(1023), "1023 B");
        assert_eq!(format_bytes(1536), "1.5 KB");
        assert_eq!(format_bytes(1_073_741_824), "1.0 GB");
    }
}