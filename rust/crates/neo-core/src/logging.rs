//! 日志保留策略（与Windows 端 LogBus 对齐的纯逻辑，可单测、不依赖文件系统）。
//!
//! 规则：
//! - 保留最近 N 天（默认 7），当天文件永不删；
//! - 目录总量超上限（默认 512 MB）时按文件名（日期）从最旧开始删；
//! - 支持 `neo-20261024.log` 与 `neo-20261024.1.log` 两种名字。

/// 极简日期（离 1970-01-01 的天数），避免为一行逻辑引入日期库。
pub fn days_from_civil(y: i32, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y } as i64;
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = ((m as i64) + 9) % 12;
    let doy = (153 * mp + 2) / 5 + (d as i64) - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

/// 从日志文件名解析日期（返回距 1970-01-01 的天数）。
pub fn parse_log_day(file_name: &str) -> Option<i64> {
    let name = file_name.strip_suffix(".log")?;
    let rest = name.strip_prefix("neo-")?;
    if rest.len() < 8 {
        return None;
    }
    let y: i32 = rest[0..4].parse().ok()?;
    let m: u32 = rest[4..6].parse().ok()?;
    let d: u32 = rest[6..8].parse().ok()?;
    if !(1..=12).contains(&m) || !(1..=31).contains(&d) {
        return None;
    }
    Some(days_from_civil(y, m, d))
}

/// 需要删除的文件名列表（纯函数，调用方按返回结果执行删除）。
pub fn plan_deletions(
    files: &[(String, u64)],
    today_day: i64,
    keep_days: i64,
    max_bytes: u64,
) -> Vec<String> {
    let mut list: Vec<(String, i64, u64)> = files
        .iter()
        .filter_map(|(name, size)| parse_log_day(name).map(|d| (name.clone(), d, *size)))
        .collect();
    // 保留策略按文件名排序（同日期时 .1 在 .log 前，天然更旧）
    list.sort_by(|a, b| a.0.cmp(&b.0));
    let mut out: Vec<String> = Vec::new();
    let mut remaining = list.clone();
    // 1) 超期删除（当天文件永不删）
    remaining.retain(|(name, day, _)| {
        let stale = *day < today_day - keep_days + 1;
        if stale && *day != today_day {
            out.push(name.clone());
            false
        } else {
            true
        }
    });
    // 2) 配额删除：从最旧开始
    let mut total: u64 = remaining.iter().map(|(_, _, s)| *s).sum();
    for (name, _, size) in remaining.iter() {
        if total <= max_bytes {
            break;
        }
        out.push(name.clone());
        total = total.saturating_sub(*size);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn f(name: &str, size: u64) -> (String, u64) {
        (name.to_string(), size)
    }

    #[test]
    fn parses_both_log_name_forms() {
        assert_eq!(parse_log_day("neo-20261024.log"), Some(days_from_civil(2026, 10, 24)));
        assert_eq!(parse_log_day("neo-20261024.1.log"), Some(days_from_civil(2026, 10, 24)));
        assert_eq!(parse_log_day("neo-2026.log"), None);
        assert_eq!(parse_log_day("other-20261024.log"), None);
        assert_eq!(parse_log_day("neo-20261324.log"), None);
    }

    #[test]
    fn deletes_older_than_keep_but_not_today() {
        let today = days_from_civil(2026, 10, 24);
        let files = vec![
            f("neo-20261024.log", 100),
            f("neo-20261018.log", 100), // 第 7 天（保留）
            f("neo-20261017.log", 100), // 第 8 天（删除）
            f("neo-20261016.log", 100),
            f("neo-20261030.log", 100), // 未来日期不删
        ];
        let del = plan_deletions(&files, today, 7, u64::MAX);
        assert!(del.contains(&"neo-20261016.log".to_string()));
        assert!(del.contains(&"neo-20261017.log".to_string()));
        assert!(!del.contains(&"neo-20261018.log".to_string()));
        assert!(!del.contains(&"neo-20261024.log".to_string()));
        assert!(!del.contains(&"neo-20261030.log".to_string()));
    }

    #[test]
    fn quota_deletes_oldest_first() {
        let today = days_from_civil(2026, 10, 24);
        let files = vec![
            f("neo-20261020.log", 400),
            f("neo-20261021.log", 400),
            f("neo-20261024.log", 400),
        ];
        let del = plan_deletions(&files, today, 7, 800);
        assert_eq!(del, vec!["neo-20261020.log".to_string()]);
    }

    #[test]
    fn unknown_names_are_ignored() {
        let today = days_from_civil(2026, 10, 24);
        let files = vec![f("notes.txt", 10), f("neo-20261001.log", 10)];
        let del = plan_deletions(&files, today, 7, u64::MAX);
        assert_eq!(del, vec!["neo-20261001.log".to_string()]);
    }
}