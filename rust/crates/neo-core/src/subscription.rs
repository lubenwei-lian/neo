//! 订阅占位节点识别（与Android 端 SubGuard.check 对齐的纯逻辑）。
//!
//! 机场常用「提示节点」表达账号超限/到期：文本里带提示语，或 outbounds 全部指向本机。
//! 本模块只做判定，不做网络请求（抓取由上层负责）。

use serde_json::Value;

const HINTS: &[&str] = &[
    "使用的设备过多",
    "请去官网获取新订阅链接",
    "设备过多",
    "请去官网",
    "官网地址",
    "套餐已到期",
    "流量已用尽",
    // 并集Windows 端 AppState.IsProviderPlaceholder 的两条关键词
    "获取新订阅",
    "使用说明",
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlaceholderReason {
    /// 文本命中提示语
    Hint(String),
    /// outbounds 里的真实节点全部指向本机
    AllLocal,
    /// 节点 tag 命中提示语
    TagHint(String),
}

fn is_local(server: &str) -> bool {
    let s = server.trim().to_ascii_lowercase();
    s.is_empty()
        || s.starts_with("127.")
        || s == "localhost"
        || s == "::1"
        || s == "[::1]"
        || s.starts_with("0.0.0.0")
}

fn hit(text: &str) -> Option<&'static str> {
    HINTS.iter().copied().find(|h| text.contains(h))
}

/// 返回 Some(reason) 表示看起来是占位/异常订阅；None 表示正常。
pub fn detect_placeholder(content: &str) -> Option<PlaceholderReason> {
    if content.trim().is_empty() {
        return None;
    }
    if let Some(h) = hit(content) {
        return Some(PlaceholderReason::Hint(h.to_string()));
    }
    let idx = content.find('{')?;
    let json: Value = serde_json::from_str(&content[idx..]).ok()?;
    let outbounds = json.get("outbounds")?.as_array()?;
    if outbounds.is_empty() {
        return None;
    }
    let mut real = 0usize;
    let mut local = 0usize;
    let mut tag_hint: Option<String> = None;
    for ob in outbounds {
        let ty = ob.get("type").and_then(|v| v.as_str()).unwrap_or("");
        let tag = ob.get("tag").and_then(|v| v.as_str()).unwrap_or("");
        if let Some(h) = hit(tag) {
            tag_hint = Some(h.to_string());
        }
        if matches!(ty, "direct" | "block" | "dns" | "selector" | "urltest") {
            continue;
        }
        real += 1;
        let server = ob.get("server").and_then(|v| v.as_str()).unwrap_or("");
        if is_local(server) {
            local += 1;
        }
    }
    if let Some(t) = tag_hint {
        return Some(PlaceholderReason::TagHint(t));
    }
    if real > 0 && real == local {
        return Some(PlaceholderReason::AllLocal);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_hint_in_text() {
        let s = "官网地址:https://example.com 使用的设备过多";
        assert!(matches!(detect_placeholder(s), Some(PlaceholderReason::Hint(_))));
    }

    #[test]
    fn detects_all_local_outbounds() {
        let s = r#"{"outbounds":[{"type":"vless","tag":"n1","server":"127.0.0.1"},{"type":"direct","tag":"direct"}]}"#;
        assert_eq!(detect_placeholder(s), Some(PlaceholderReason::AllLocal));
    }

    #[test]
    fn detects_hint_inside_json() {
        // 与 SubGuard 一致：文本层先命中（JSON 里的中文同样是纯文本），所以是 Hint 而不是 TagHint。
        let s = r#"{"outbounds":[{"type":"vless","tag":"请去官网获取新订阅链接","server":"1.2.3.4"}]}"#;
        assert!(matches!(detect_placeholder(s), Some(PlaceholderReason::Hint(_))));
    }

    #[test]
    fn tag_hint_path_is_reachable_for_non_text_cases() {
        // TagHint 只在文本层未命中时可达（例如 JSON 用 \uXXXX 转义写提示语）。
        let s = r#"{"outbounds":[{"type":"vless","tag":"\u8bf7\u53bb\u5b98\u7f51","server":"1.2.3.4"}]}"#;
        assert!(matches!(detect_placeholder(s), Some(PlaceholderReason::TagHint(_))));
    }

    #[test]
    fn normal_config_passes() {
        let s = r#"{"outbounds":[{"type":"vless","tag":"日本","server":"jp.example.com"},{"type":"direct","tag":"direct"}]}"#;
        assert_eq!(detect_placeholder(s), None);
        assert_eq!(detect_placeholder(""), None);
    }
}