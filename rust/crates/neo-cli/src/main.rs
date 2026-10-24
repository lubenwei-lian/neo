//! neo-cli：解析分享链接、测试 TCP 延迟、生成 sing-box 配置、查询订阅额度、内置自检。

use std::env;
use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::time::Duration;

use neo_core::failover::{Decision, FailoverPolicy, FailoverState};
use neo_core::{
    build_config, build_config_json, fetch_subscription, parse_share_link, parse_subscription_body,
    parse_userinfo, DelayConfig, DelayTarget, FetchOptions, Node, Protocol, ProxyMode,
    SingBoxOptions, TcpDelayTester,
};
use serde_json::Value;

const VMESS_SAMPLE: &str = "vmess://eyJ2IjoiMiIsInBzIjoic2VsZnRlc3Qtdm1lc3MiLCJhZGQiOiJ2bS5leGFtcGxlLmNvbSIsInBvcnQiOiI0NDMiLCJpZCI6IjExMTExMTExLTExMTEtNDExMS04MTExLTExMTExMTExMTExMSIsImFpZCI6IjAiLCJzY3kiOiJhdXRvIiwibmV0Ijoid3MiLCJ0eXBlIjoibm9uZSIsImhvc3QiOiJ2bS5leGFtcGxlLmNvbSIsInBhdGgiOiIvd3MiLCJ0bHMiOiJ0bHMiLCJzbmkiOiJ2bS5leGFtcGxlLmNvbSJ9";

fn usage() {
    println!(
        r#"neo-cli 0.1.0 - neo Rust 命令行工具

用法:
  neo-cli parse <share-link>                 解析分享链接并输出节点 JSON
  neo-cli delay <host:port> [--count N]      测试 TCP 握手延迟（默认 3 次取样）
                 [--timeout-ms N] [--json]   单次超时默认 3000 ms；可选 JSON 输出
  neo-cli genconfig <分享链接文件|订阅缓存JSON> [--tun|--system] [-o 文件]
                  [--mode rule|global|direct] [--ruleset-dir DIR] [--no-groups]
                  [--mixed-port N] [--allow-lan] [--dns SERVER] [--log-level LEVEL]
                  [--game-process NAME]... [--key 缓存键]
                                              生成 sing-box 1.14.x 配置（默认非 TUN + 策略组）
  neo-cli shadow <输入JSON> [-o 文件]         按「节点 + 选项」生成一份影子配置（
                  {{"node": <Node JSON>, "options": {{tun, mixedPort, httpPort, inboundsMode,
                   allowLan, logLevel, proxyMode, bypassPrivate, bypassCn, blockAds,
                   gameDirectProcesses, clashApiPort, clashApiSecret, dnsServer, ruleSetDir, emitGroups}}}}
                  默认 --no-groups 同构 ；inboundsMode=socksHttp 时与 入站逐字段一致；
                  供 Windows / Android 端按 JSON 调用）
  neo-cli usage <url|分享链接文件|订阅缓存JSON> [--proxy URL] [--no-direct]
                  [--userinfo "upload=..;total=.."] [--key 缓存键] [--json]
                                              拉取订阅并解析 subscription-userinfo 额度
  neo-cli selftest                           运行内置断言（不访问外部网络）
  neo-cli help                               显示本帮助

退出码:
  0 成功 / 自检通过    1 解析/生成/抓取失败 / 延迟全部失败 / 自检失败    2 参数错误

支持的分享链接:
  vmess、vless、trojan、ss、hysteria2/hy2、tuic、
  anytls、socks/socks5/socks5h、naive+https、wireguard/wg

genconfig 说明:
  · 默认不引用规则集（配置可在任意机器 sing-box check 通过）；
    要复刻Windows 端分流（geosite-cn/geoip-cn/geosite-ads），加
    --ruleset-dir <runtime 目录>。
  · 默认生成 🚀 节点选择 / ♻️ 自动选择 / 🐟 漏网之鱼 三个策略组；
    --no-groups 退回 Windows 端同构（单节点 final=proxy）。
  · 订阅抓取默认 UA 为 v2rayN/7.24.8；额度探测 UA 链为
    ClashforWindows/0.20.39 → sing-box/1.14.2。
"#
    );
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = env::args_os()
        .skip(1)
        .map(|arg| arg.to_string_lossy().into_owned())
        .collect();
    let code = run(args).await;
    std::process::exit(code);
}

async fn run(args: Vec<String>) -> i32 {
    let Some(command) = args.first().map(String::as_str) else {
        usage();
        return 2;
    };
    match command {
        "parse" => {
            if args.len() != 2 {
                eprintln!("用法: neo-cli parse <share-link>");
                return 2;
            }
            match parse_share_link(&args[1]) {
                Ok(node) => match node.to_json_pretty() {
                    Ok(json) => {
                        println!("{json}");
                        0
                    }
                    Err(error) => {
                        eprintln!("JSON 序列化失败: {error}");
                        1
                    }
                },
                Err(error) => {
                    eprintln!("解析失败: {error}");
                    1
                }
            }
        }
        "delay" => {
            if args.len() < 2 {
                eprintln!("用法: neo-cli delay <host:port> [--count N] [--timeout-ms N] [--json]");
                return 2;
            }
            let target = match DelayTarget::parse(&args[1]) {
                Ok(target) => target,
                Err(error) => {
                    eprintln!("目标地址无效: {error}");
                    return 2;
                }
            };
            let mut count = 3usize;
            let mut timeout_ms = 3000u64;
            let mut json = false;
            let mut index = 2;
            while index < args.len() {
                match args[index].as_str() {
                    "--count" => {
                        index += 1;
                        let Some(raw) = args.get(index) else {
                            eprintln!("--count 缺少数值");
                            return 2;
                        };
                        match raw.parse::<usize>() {
                            Ok(value) if (1..=20).contains(&value) => count = value,
                            _ => {
                                eprintln!("--count 必须是 1..=20 的整数");
                                return 2;
                            }
                        }
                    }
                    "--timeout-ms" => {
                        index += 1;
                        let Some(raw) = args.get(index) else {
                            eprintln!("--timeout-ms 缺少数值");
                            return 2;
                        };
                        match raw.parse::<u64>() {
                            Ok(value) if (50..=60_000).contains(&value) => timeout_ms = value,
                            _ => {
                                eprintln!("--timeout-ms 必须是 50..=60000 的整数");
                                return 2;
                            }
                        }
                    }
                    "--json" => json = true,
                    "-h" | "--help" => {
                        usage();
                        return 0;
                    }
                    other => {
                        eprintln!("未知参数: {other}");
                        return 2;
                    }
                }
                index += 1;
            }

            let total_budget =
                Duration::from_millis((count as u64).saturating_mul(timeout_ms) + 500);
            let tester = match TcpDelayTester::new(DelayConfig {
                concurrency: 8,
                timeout: Duration::from_millis(timeout_ms),
                samples: count,
                total_timeout: Some(total_budget.min(Duration::from_secs(60))),
            }) {
                Ok(tester) => tester,
                Err(error) => {
                    eprintln!("测速参数无效: {error}");
                    return 2;
                }
            };
            let outcome = tester.test_target(target).await;
            if json {
                match serde_json::to_string_pretty(&outcome) {
                    Ok(text) => println!("{text}"),
                    Err(error) => {
                        eprintln!("JSON 序列化失败: {error}");
                        return 1;
                    }
                }
            } else if outcome.ok {
                println!("目标: {}", outcome.target.endpoint());
                println!(
                    "取样: {} 次，成功 {}，失败 {}",
                    count,
                    outcome.samples_ms.len(),
                    outcome.failures
                );
                println!(
                    "样本: {}",
                    outcome
                        .samples_ms
                        .iter()
                        .map(|ms| format!("{ms} ms"))
                        .collect::<Vec<_>>()
                        .join(", ")
                );
                println!("最小: {} ms", outcome.min_ms.unwrap_or(0));
                println!("中位: {} ms", outcome.median_ms.unwrap_or(0));
            } else {
                println!("目标: {}", outcome.target.endpoint());
                println!("结果: 失败（{} 次失败）", outcome.failures);
                if let Some(error) = &outcome.error {
                    println!("原因: {error}");
                }
            }
            if outcome.ok {
                0
            } else {
                1
            }
        }
        "detect-placeholder" => detect_placeholder_cli(&args[1..]),
        "parse-subscription" => parse_subscription_cli(&args[1..]),
        "failover-decide" => failover_decide(&args[1..]),
        "genconfig" => genconfig(&args[1..]),
        "shadow" => shadow(&args[1..]),
        "usage" => usage_command(&args[1..]),
        "selftest" => selftest().await,
        "help" | "-h" | "--help" => {
            usage();
            0
        }
        other => {
            eprintln!("未知命令: {other}");
            usage();
            2
        }
    }
}


// ---------------------------------------------------------------------------
// Windows 端纯逻辑 CLI 协议（通过临时 JSON 文件调用，失败回退 ）
// ---------------------------------------------------------------------------

const CORE_HINTS: &[&str] = &[
    "使用的设备过多",
    "请去官网获取新订阅链接",
    "设备过多",
    "请去官网",
    "官网地址",
    "套餐已到期",
    "流量已用尽",
    "获取新订阅",
    "使用说明",
];

fn read_text_file(path: &str) -> Result<String, String> {
    fs::read_to_string(path).map_err(|error| error.to_string())
}

fn print_json(value: &Value) -> i32 {
    match serde_json::to_string(value) {
        Ok(text) => {
            println!("{text}");
            0
        }
        Err(error) => {
            eprintln!("JSON 序列化失败: {error}");
            1
        }
    }
}

/// 输入: {"text": 原始正文, "nodes": [{"name","server"}]}
/// 输出: {"parsed":bool,"placeholder":bool,"reason":string|null}
fn detect_placeholder_cli(args: &[String]) -> i32 {
    let Some(path) = args.first() else {
        eprintln!("用法: neo-cli detect-placeholder <input.json>");
        return 2;
    };
    let raw = match read_text_file(path) {
        Ok(text) => text,
        Err(error) => {
            eprintln!("读取失败: {error}");
            return 1;
        }
    };
    let payload: Value = serde_json::from_str(&raw).unwrap_or(Value::Null);
    let body = payload.get("text").and_then(Value::as_str).unwrap_or("");
    let nodes: Vec<&Value> = payload
        .get("nodes")
        .and_then(Value::as_array)
        .map(|items| items.iter().collect())
        .unwrap_or_default();

    let mut placeholder = false;
    let mut reason: Option<String> = None;
    if let Some(hit) = CORE_HINTS.iter().find(|hint| body.contains(**hint)) {
        placeholder = true;
        reason = Some(format!("hint:{hit}"));
    }
    if !placeholder && !nodes.is_empty() {
        let mut local = 0usize;
        for node in &nodes {
            let name = node.get("name").and_then(Value::as_str).unwrap_or("");
            let server = node.get("server").and_then(Value::as_str).unwrap_or("");
            if let Some(hit) = CORE_HINTS.iter().find(|hint| name.contains(**hint)) {
                placeholder = true;
                reason = Some(format!("tag_hint:{hit}"));
                break;
            }
            if matches!(server, "127.0.0.1" | "::1" | "localhost") {
                local += 1;
            }
        }
        if !placeholder && local == nodes.len() {
            placeholder = true;
            reason = Some("all_local".to_string());
        }
    }
    let parsed = !nodes.is_empty()
        || payload
            .get("text")
            .and_then(Value::as_str)
            .and_then(|text| text.find('{'))
            .map(|brace| serde_json::from_str::<Value>(&body[brace..]).is_ok())
            .unwrap_or(false);
    print_json(&serde_json::json!({
        "placeholder": placeholder,
        "reason": reason,
        "parsed": parsed,
    }))
}

/// 输入: 订阅正文文件；输出: 解析结果（节点 JSON）。失败返回 1（回退）。
fn parse_subscription_cli(args: &[String]) -> i32 {
    let Some(path) = args.first() else {
        eprintln!("用法: neo-cli parse-subscription <body-file>");
        return 2;
    };
    let body = match read_text_file(path) {
        Ok(text) => text,
        Err(error) => {
            eprintln!("读取失败: {error}");
            return 1;
        }
    };
    match parse_subscription_body(&body) {
        Ok(parsed) => {
            let nodes = serde_json::to_value(&parsed.nodes).unwrap_or_else(|_| Value::Array(vec![]));
            print_json(&serde_json::json!({
                "nodes": nodes,
                "trafficInfo": parsed.traffic_info,
                "wasBase64": parsed.was_base64,
                "skipped": parsed.skipped,
                "placeholder": parsed.placeholder.as_ref().map(|reason| format!("{reason:?}")),
            }))
        }
        Err(error) => {
            eprintln!("解析失败: {error}");
            1
        }
    }
}

/// 输入: {"nowMs","ok","hadTraffic","fails","lastSwitchMs","thresholdIdle","thresholdActive","cooldownMs"}
/// 输出: {"decision":"keep|switch|cooldown","fails":n,"lastSwitchMs":n|null}
fn failover_decide(args: &[String]) -> i32 {
    let Some(path) = args.first() else {
        eprintln!("用法: neo-cli failover-decide <input.json>");
        return 2;
    };
    let raw = match read_text_file(path) {
        Ok(text) => text,
        Err(error) => {
            eprintln!("读取失败: {error}");
            return 1;
        }
    };
    let payload: Value = match serde_json::from_str(&raw) {
        Ok(value) => value,
        Err(error) => {
            eprintln!("输入 JSON 无效: {error}");
            return 2;
        }
    };
    let now_ms = payload.get("nowMs").and_then(Value::as_i64).unwrap_or(0);
    let ok = payload.get("ok").and_then(Value::as_bool).unwrap_or(false);
    let had_traffic = payload.get("hadTraffic").and_then(Value::as_bool).unwrap_or(false);
    let fails = payload.get("fails").and_then(Value::as_u64).unwrap_or(0) as u32;
    let last_switch_ms = payload.get("lastSwitchMs").and_then(Value::as_i64);
    let policy = FailoverPolicy {
        threshold_idle: payload
            .get("thresholdIdle")
            .and_then(Value::as_u64)
            .unwrap_or(3) as u32,
        threshold_active: payload
            .get("thresholdActive")
            .and_then(Value::as_u64)
            .unwrap_or(4) as u32,
        cooldown_ms: payload
            .get("cooldownMs")
            .and_then(Value::as_i64)
            .unwrap_or(60_000),
    };
    let mut state = FailoverState::from_parts(fails, last_switch_ms);
    let decision = state.on_probe(now_ms, ok, had_traffic, &policy);
    let name = match decision {
        Decision::Keep => "keep",
        Decision::Switch => "switch",
        Decision::Cooldown => "cooldown",
    };
    print_json(&serde_json::json!({
        "decision": name,
        "fails": state.fails(),
        "lastSwitchMs": state.last_switch_ms(),
    }))
}

// ---------------------------------------------------------------------------
// genconfig / usage
// ---------------------------------------------------------------------------

/// 影子配置生成（输入 = 序列化的「节点 + 选项」，输出 = 与 同构的 sing-box 配置）。
/// 只做只读对照，不参与实际连接。
fn shadow(args: &[String]) -> i32 {
    let Some(input) = args.first() else {
        eprintln!("用法: neo-cli shadow <输入JSON> [-o 输出文件]");
        return 2;
    };
    let mut output: Option<PathBuf> = None;
    let mut index = 1usize;
    while index < args.len() {
        match args[index].as_str() {
            "-o" | "--output" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("-o 缺少输出路径");
                    return 2;
                };
                output = Some(PathBuf::from(raw));
            }
            "-h" | "--help" => {
                usage();
                return 0;
            }
            other => {
                eprintln!("未知参数: {other}");
                return 2;
            }
        }
        index += 1;
    }
    let raw = if input == "-" {
        let mut buffer = String::new();
        if let Err(error) = std::io::stdin().read_to_string(&mut buffer) {
            eprintln!("读取标准输入失败: {error}");
            return 1;
        }
        buffer
    } else {
        match fs::read_to_string(input) {
            Ok(text) => text,
            Err(error) => {
                eprintln!("读取 {input} 失败: {error}");
                return 1;
            }
        }
    };
    let value: serde_json::Value = match serde_json::from_str(&raw) {
        Ok(value) => value,
        Err(error) => {
            eprintln!("输入 JSON 解析失败: {error}");
            return 1;
        }
    };
    let Some(node_value) = value.get("node") else {
        eprintln!("输入 JSON 缺少 node 字段");
        return 2;
    };
    let node: Node = match serde_json::from_value(node_value.clone()) {
        Ok(node) => node,
        Err(error) => {
            eprintln!("node 解析失败: {error}");
            return 1;
        }
    };
    // 影子模式默认与 同构（单个 proxy 出站、无策略组）。
    let mut opts = SingBoxOptions { emit_groups: false, ..SingBoxOptions::default() };
    if let Some(options) = value.get("options") {
        if let Some(v) = options.get("tun").and_then(|v| v.as_bool()) {
            opts.tun = v;
        }
        if let Some(v) = options.get("mixedPort").and_then(|v| v.as_u64()) {
            if (1..=65535).contains(&v) {
                opts.mixed_port = v as u16;
            }
        }
        if let Some(v) = options.get("httpPort").and_then(|v| v.as_u64()) {
            if (1..=65535).contains(&v) {
                opts.http_port = v as u16;
            }
        }
        if let Some(v) = options.get("inboundsMode").and_then(|v| v.as_str()) {
            opts.socks_http_inbounds = matches!(v, "socksHttp" | "socks+http" | "socks-http");
        }
        if let Some(v) = options.get("allowLan").and_then(|v| v.as_bool()) {
            opts.allow_lan = v;
        }
        if let Some(v) = options.get("logLevel").and_then(|v| v.as_str()) {
            opts.log_level = v.to_string();
        }
        if let Some(v) = options.get("proxyMode").and_then(|v| v.as_str()) {
            if let Some(mode) = ProxyMode::parse(v) {
                opts.proxy_mode = mode;
            }
        }
        if let Some(v) = options.get("bypassPrivate").and_then(|v| v.as_bool()) {
            opts.bypass_private = v;
        }
        if let Some(v) = options.get("bypassCn").and_then(|v| v.as_bool()) {
            opts.bypass_cn = v;
        }
        if let Some(v) = options.get("blockAds").and_then(|v| v.as_bool()) {
            opts.block_ads = v;
        }
        if let Some(list) = options.get("gameDirectProcesses").and_then(|v| v.as_array()) {
            opts.game_direct_processes = list
                .iter()
                .filter_map(|item| item.as_str().map(str::to_string))
                .collect();
        }
        if let Some(v) = options.get("clashApiPort").and_then(|v| v.as_u64()) {
            if (1..=65535).contains(&v) {
                opts.clash_api_port = v as u16;
            }
        }
        if let Some(v) = options.get("clashApiSecret").and_then(|v| v.as_str()) {
            opts.clash_api_secret = v.to_string();
        }
        if let Some(v) = options.get("dnsServer").and_then(|v| v.as_str()) {
            opts.dns_server = v.to_string();
        }
        if let Some(v) = options.get("ruleSetDir").and_then(|v| v.as_str()) {
            if !v.is_empty() {
                opts.rule_set_dir = Some(PathBuf::from(v));
            }
        }
        if let Some(v) = options.get("emitGroups").and_then(|v| v.as_bool()) {
            opts.emit_groups = v;
        }
    }
    let json = match build_config_json(&[node], &opts) {
        Ok(text) => text,
        Err(error) => {
            eprintln!("生成影子配置失败: {error}");
            return 1;
        }
    };
    match output {
        Some(path) => {
            if let Err(error) = fs::write(&path, &json) {
                eprintln!("写入 {} 失败: {error}", path.display());
                return 1;
            }
            eprintln!("已生成影子配置: {}", path.display());
            0
        }
        None => {
            print!("{json}");
            0
        }
    }
}

fn genconfig(args: &[String]) -> i32 {
    let Some(input) = args.first() else {
        eprintln!(
            "用法: neo-cli genconfig <分享链接文件|订阅缓存JSON> [--tun|--system] [-o 文件] \
             [--mode rule|global|direct] [--ruleset-dir DIR] [--no-groups] [--mixed-port N] \
             [--allow-lan] [--dns SERVER] [--log-level LEVEL] [--game-process NAME]... [--key 缓存键]"
        );
        return 2;
    };
    let mut opts = SingBoxOptions::default();
    let mut output: Option<PathBuf> = None;
    let mut cache_key: Option<String> = None;
    let mut index = 1usize;
    while index < args.len() {
        match args[index].as_str() {
            "--tun" => opts.tun = true,
            "--system" => opts.tun = false,
            "-o" | "--output" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("-o 缺少输出路径");
                    return 2;
                };
                output = Some(PathBuf::from(raw));
            }
            "--mode" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--mode 缺少取值（rule/global/direct）");
                    return 2;
                };
                match ProxyMode::parse(raw) {
                    Some(mode) => opts.proxy_mode = mode,
                    None => {
                        eprintln!("--mode 只支持 rule/global/direct（收到 {raw}）");
                        return 2;
                    }
                }
            }
            "--ruleset-dir" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--ruleset-dir 缺少目录");
                    return 2;
                };
                opts.rule_set_dir = Some(PathBuf::from(raw));
            }
            "--no-groups" => opts.emit_groups = false,
            "--groups" => opts.emit_groups = true,
            "--mixed-port" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--mixed-port 缺少数值");
                    return 2;
                };
                match raw.parse::<u16>() {
                    Ok(port) if port > 0 => opts.mixed_port = port,
                    _ => {
                        eprintln!("--mixed-port 必须是 1..=65535 的整数");
                        return 2;
                    }
                }
            }
            "--allow-lan" => opts.allow_lan = true,
            "--dns" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--dns 缺少地址");
                    return 2;
                };
                opts.dns_server = raw.clone();
            }
            "--log-level" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--log-level 缺少取值");
                    return 2;
                };
                opts.log_level = raw.clone();
            }
            "--game-process" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--game-process 缺少进程名");
                    return 2;
                };
                opts.game_direct_processes.push(raw.clone());
            }
            "--key" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--key 缺少缓存键");
                    return 2;
                };
                cache_key = Some(raw.clone());
            }
            "-h" | "--help" => {
                usage();
                return 0;
            }
            other => {
                eprintln!("未知参数: {other}");
                return 2;
            }
        }
        index += 1;
    }

    let (nodes, traffic_info, placeholder) = match load_nodes(input, cache_key.as_deref()) {
        Ok(value) => value,
        Err(error) => {
            eprintln!("读取节点失败: {error}");
            return 1;
        }
    };
    if let Some(info) = &traffic_info {
        eprintln!("订阅信息: {info}");
    }
    if let Some(placeholder) = &placeholder {
        eprintln!("警告：订阅疑似占位/异常（{placeholder}）");
    }
    let json = match build_config_json(&nodes, &opts) {
        Ok(text) => text,
        Err(error) => {
            eprintln!("生成 sing-box 配置失败: {error}");
            return 1;
        }
    };
    let groups_active = opts.emit_groups
        && nodes.iter().all(|node| node.protocol != Protocol::WireGuard);
    match output {
        Some(path) => {
            if let Err(error) = fs::write(&path, &json) {
                eprintln!("写入 {} 失败: {error}", path.display());
                return 1;
            }
            eprintln!(
                "已生成 sing-box 配置: {}（{} 个节点 · {} · {}）",
                path.display(),
                nodes.len(),
                if opts.tun { "TUN + IPv4 fakeip" } else { "mixed（非 TUN）" },
                if groups_active {
                    "策略组"
                } else if opts.emit_groups {
                    "WireGuard endpoint（无策略组）"
                } else {
                    "同构"
                }
            );
            0
        }
        None => {
            print!("{json}");
            0
        }
    }
}

/// 读取输入：分享链接文本 / 订阅缓存 JSON（{id: 正文}）/ 标准输入 -。
fn load_nodes(
    input: &str,
    cache_key: Option<&str>,
) -> Result<(Vec<Node>, Option<String>, Option<String>), String> {
    let raw = if input == "-" {
        let mut buffer = String::new();
        std::io::stdin()
            .read_to_string(&mut buffer)
            .map_err(|error| format!("读取标准输入失败: {error}"))?;
        buffer
    } else {
        fs::read_to_string(input).map_err(|error| format!("读取 {input} 失败: {error}"))?
    };
    if raw.trim().is_empty() {
        return Err("输入内容为空".into());
    }

    if raw.trim_start().starts_with('{') {
        if let Ok(value) = serde_json::from_str::<serde_json::Value>(&raw) {
            if let Some(map) = value.as_object() {
                if !map.is_empty() && map.values().all(|value| value.is_string()) {
                    if let Some(want) = cache_key {
                        if !map.contains_key(want) {
                            return Err(format!(
                                "订阅缓存中没有键 {want}（现有键: {}）",
                                map.keys().cloned().collect::<Vec<_>>().join(", ")
                            ));
                        }
                    }
                    let mut nodes = Vec::new();
                    let mut infos = Vec::new();
                    let mut placeholders = Vec::new();
                    for (id, value) in map {
                        if cache_key.is_some_and(|want| want != id) {
                            continue;
                        }
                        let parsed = parse_subscription_body(value.as_str().unwrap_or(""))
                            .map_err(|error| format!("缓存条目 {id} 解析失败: {error}"))?;
                        nodes.extend(parsed.nodes);
                        if let Some(info) = parsed.traffic_info {
                            infos.push(info);
                        }
                        if let Some(reason) = parsed.placeholder {
                            placeholders.push(format!("{id}: {reason:?}"));
                        }
                    }
                    if nodes.is_empty() {
                        return Err("订阅缓存中没有解析出有效节点".into());
                    }
                    return Ok((
                        nodes,
                        (!infos.is_empty()).then(|| infos.join(" | ")),
                        (!placeholders.is_empty()).then(|| placeholders.join("; ")),
                    ));
                }
            }
        }
    }

    let parsed = parse_subscription_body(&raw).map_err(|error| error.to_string())?;
    if parsed.nodes.is_empty() {
        return Err("未解析出有效节点".into());
    }
    Ok((
        parsed.nodes,
        parsed.traffic_info,
        parsed.placeholder.map(|reason| format!("{reason:?}")),
    ))
}

fn usage_command(args: &[String]) -> i32 {
    let mut input: Option<String> = None;
    let mut userinfo_raw: Option<String> = None;
    let mut proxy: Option<String> = None;
    let mut allow_direct = true;
    let mut as_json = false;
    let mut cache_key: Option<String> = None;
    let mut index = 0usize;
    while index < args.len() {
        match args[index].as_str() {
            "--userinfo" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--userinfo 缺少响应头内容");
                    return 2;
                };
                userinfo_raw = Some(raw.clone());
            }
            "--proxy" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--proxy 缺少地址（如 http://127.0.0.1:21809）");
                    return 2;
                };
                proxy = Some(raw.clone());
            }
            "--no-direct" => allow_direct = false,
            "--json" => as_json = true,
            "--key" => {
                index += 1;
                let Some(raw) = args.get(index) else {
                    eprintln!("--key 缺少缓存键");
                    return 2;
                };
                cache_key = Some(raw.clone());
            }
            "-h" | "--help" => {
                usage();
                return 0;
            }
            other if other.starts_with('-') => {
                eprintln!("未知参数: {other}");
                return 2;
            }
            other => {
                if input.is_some() {
                    eprintln!("只能指定一个输入");
                    return 2;
                }
                input = Some(other.to_string());
            }
        }
        index += 1;
    }

    if let Some(raw) = &userinfo_raw {
        if input.is_none() {
            return print_userinfo(raw, as_json);
        }
    }

    let Some(input) = input else {
        eprintln!(
            "用法: neo-cli usage <url|分享链接文件|订阅缓存JSON> [--proxy URL] [--no-direct] \
             [--userinfo \"upload=..;total=..\"] [--key 缓存键] [--json]"
        );
        return 2;
    };

    let lower = input.to_ascii_lowercase();
    if lower.starts_with("http://") || lower.starts_with("https://") {
        let mut opts = FetchOptions::default();
        if let Some(proxy) = &proxy {
            opts.proxy_candidates = vec![proxy.clone()];
        }
        opts.allow_direct = allow_direct;
        let outcome = match fetch_subscription(&input, &opts) {
            Ok(outcome) => outcome,
            Err(error) => {
                eprintln!("抓取订阅失败: {error}");
                return 1;
            }
        };
        let parsed = parse_subscription_body(&outcome.body).ok();
        let node_count = parsed.as_ref().map(|p| p.nodes.len()).unwrap_or(0);
        let traffic_info = parsed.as_ref().and_then(|p| p.traffic_info.clone());
        let placeholder = parsed
            .as_ref()
            .and_then(|p| p.placeholder.clone())
            .map(|reason| format!("{reason:?}"));
        if as_json {
            let usage = outcome.usage.as_ref();
            let payload = serde_json::json!({
                "url": outcome.url,
                "status": outcome.status,
                "usedProxy": outcome.used_proxy,
                "contentUserAgent": outcome.content_user_agent,
                "usageUserAgent": outcome.usage_user_agent,
                "nodes": node_count,
                "trafficInfo": traffic_info,
                "placeholder": placeholder,
                "upload": usage.map(|u| u.upload),
                "download": usage.map(|u| u.download),
                "total": usage.map(|u| u.total),
                "expire": usage.map(|u| u.expire),
                "used": usage.map(|u| u.used()),
                "remaining": usage.map(|u| u.remaining()),
                "usedRatio": usage.and_then(|u| u.used_ratio()),
            });
            match serde_json::to_string_pretty(&payload) {
                Ok(text) => println!("{text}"),
                Err(error) => {
                    eprintln!("JSON 序列化失败: {error}");
                    return 1;
                }
            }
        } else {
            println!("订阅: {}", outcome.url);
            println!(
                "状态: HTTP {} · {}",
                outcome.status,
                outcome
                    .used_proxy
                    .as_deref()
                    .map(|proxy| format!("代理 {proxy}"))
                    .unwrap_or_else(|| "直连".to_string())
            );
            println!("节点: {node_count} 个");
            if let Some(info) = &traffic_info {
                println!("订阅信息: {info}");
            }
            if let Some(reason) = &placeholder {
                println!("疑似占位订阅: {reason}");
            }
            match outcome.usage.as_ref() {
                Some(usage) => {
                    println!("额度: {}", usage.summary_line());
                    if usage.expire > 0 {
                        println!("到期: {}（Unix 秒）", usage.expire);
                    }
                    if let Some(ua) = &outcome.usage_user_agent {
                        println!("额度探测 UA: {ua}");
                    }
                }
                None => println!(
                    "额度: 未返回 subscription-userinfo（可尝试 --proxy 或 --no-direct 重试）"
                ),
            }
            if let Some(raw) = &userinfo_raw {
                if let Some(usage) = parse_userinfo(raw) {
                    println!("--userinfo 覆盖: {}", usage.summary_line());
                }
            }
        }
        0
    } else {
        let (nodes, traffic_info, placeholder) = match load_nodes(&input, cache_key.as_deref()) {
            Ok(value) => value,
            Err(error) => {
                eprintln!("读取订阅失败: {error}");
                return 1;
            }
        };
        if as_json {
            let payload = serde_json::json!({
                "source": input,
                "nodes": nodes.len(),
                "trafficInfo": traffic_info,
                "placeholder": placeholder,
                "usage": serde_json::Value::Null,
            });
            match serde_json::to_string_pretty(&payload) {
                Ok(text) => println!("{text}"),
                Err(error) => {
                    eprintln!("JSON 序列化失败: {error}");
                    return 1;
                }
            }
        } else {
            println!("来源: {input}");
            println!("节点: {} 个", nodes.len());
            if let Some(info) = &traffic_info {
                println!("订阅信息: {info}");
            }
            if let Some(reason) = &placeholder {
                println!("疑似占位订阅: {reason}");
            }
            println!("额度: 文件/缓存没有 HTTP 响应头；可用 --userinfo 直接解析响应头文本");
        }
        if let Some(raw) = &userinfo_raw {
            if let Some(usage) = parse_userinfo(raw) {
                println!("--userinfo: {}", usage.summary_line());
            }
        }
        0
    }
}

fn print_userinfo(raw: &str, as_json: bool) -> i32 {
    match parse_userinfo(raw) {
        Some(usage) => {
            if as_json {
                let payload = serde_json::json!({
                    "upload": usage.upload,
                    "download": usage.download,
                    "total": usage.total,
                    "expire": usage.expire,
                    "used": usage.used(),
                    "remaining": usage.remaining(),
                    "usedRatio": usage.used_ratio(),
                    "summary": usage.summary_line(),
                });
                match serde_json::to_string_pretty(&payload) {
                    Ok(text) => println!("{text}"),
                    Err(error) => {
                        eprintln!("JSON 序列化失败: {error}");
                        return 1;
                    }
                }
            } else {
                println!("{}", usage.summary_line());
                if usage.expire > 0 {
                    println!("到期: {}（Unix 秒）", usage.expire);
                }
            }
            0
        }
        None => {
            eprintln!("无法解析 subscription-userinfo（期望 upload=/download=/total=/expire= 用 ; 分隔）");
            1
        }
    }
}

async fn selftest() -> i32 {
    println!("neo-cli selftest：内置断言（不访问外部网络）");
    let mut passed = 0u32;
    let mut failed = 0u32;

    macro_rules! check {
        ($name:expr, $cond:expr) => {
            if $cond {
                passed += 1;
                println!("[PASS] {}", $name);
            } else {
                failed += 1;
                println!("[FAIL] {}", $name);
            }
        };
    }

    let cases: [(&str, &str, Protocol); 8] = [
        ("vmess", VMESS_SAMPLE, Protocol::VMess),
        (
            "vless",
            "vless://11111111-1111-4111-8111-111111111111@[2001:db8::1]:443?security=reality&sni=example.com&type=grpc&serviceName=svc#selftest-vless",
            Protocol::VLESS,
        ),
        (
            "trojan",
            "trojan://p%40ss@trojan.example.com:443?sni=t.example.com#selftest-trojan",
            Protocol::Trojan,
        ),
        (
            "ss",
            "ss://YWVzLTI1Ni1nY206cEBzcw==@ss.example.com:8388#selftest-ss",
            Protocol::Shadowsocks,
        ),
        (
            "hysteria2",
            "hysteria2://secret@hy.example.com:443?sni=hy.example.com&insecure=1#selftest-hy2",
            Protocol::Hysteria2,
        ),
        (
            "tuic",
            "tuic://uuid:pass@tuic.example.com:443?sni=t.example.com#selftest-tuic",
            Protocol::TUIC,
        ),
        (
            "anytls",
            "anytls://secret@any.example.com:443#selftest-anytls",
            Protocol::AnyTLS,
        ),
        (
            "socks5",
            "socks5://user:pass@127.0.0.1:1080#selftest-socks",
            Protocol::Socks,
        ),
    ];
    for (name, link, protocol) in cases {
        match parse_share_link(link) {
            Ok(node) => check!(
                format!("parse {name}"),
                node.protocol == protocol && node.is_valid()
            ),
            Err(error) => {
                failed += 1;
                println!("[FAIL] parse {name}: {error}");
            }
        }
    }

    let vless = parse_share_link(cases[1].1);
    check!(
        "vless IPv6 去方括号 + grpc serviceName",
        vless
            .as_ref()
            .map(|n| n.server == "2001:db8::1" && n.service_name.as_deref() == Some("svc"))
            .unwrap_or(false)
    );
    let ss = parse_share_link(cases[3].1);
    check!(
        "ss SIP002 base64 userinfo",
        ss.as_ref()
            .map(|n| n.method.as_deref() == Some("aes-256-gcm")
                && n.password.as_deref() == Some("p@ss"))
            .unwrap_or(false)
    );
    check!(
        "bad vmess 返回 Err 而非 panic",
        std::panic::catch_unwind(|| parse_share_link("vmess://not-base64"))
            .map(|result| result.is_err())
            .unwrap_or(false)
    );
    check!(
        "Node JSON 往返",
        parse_share_link(cases[0].1)
            .ok()
            .and_then(|node| {
                let json = node.to_json().ok()?;
                let back = Node::from_json(&json).ok()?;
                (back == node).then_some(())
            })
            .is_some()
    );

    // 配置生成 / 订阅解析 / 额度解析
    let tun_config = parse_share_link(cases[0].1).ok().and_then(|node| {
        build_config(
            &[node],
            &SingBoxOptions { tun: true, ..SingBoxOptions::default() },
        )
        .ok()
    });
    check!(
        "config builder：VMess outbound + TUN 入口",
        tun_config
            .as_ref()
            .map(|v| {
                v["outbounds"][0]["type"] == serde_json::json!("vmess")
                    && v["outbounds"][0]["tag"] == serde_json::json!("proxy")
                    && v["inbounds"][0]["type"] == serde_json::json!("tun")
            })
            .unwrap_or(false)
    );
    check!(
        "config builder：TUN fakeip 仅 IPv4 + query_type A",
        tun_config
            .as_ref()
            .map(|v| {
                let servers = v["dns"]["servers"].as_array().cloned().unwrap_or_default();
                let fake_ok = servers
                    .iter()
                    .find(|s| s["type"] == serde_json::json!("fakeip"))
                    .map(|fake| fake.get("inet6_range").is_none())
                    .unwrap_or(false);
                let a_ok = v["dns"]["rules"]
                    .as_array()
                    .map(|rules| {
                        rules.iter().any(|rule| {
                            rule["server"] == serde_json::json!("dns-fakeip")
                                && rule["query_type"] == serde_json::json!(["A"])
                        })
                    })
                    .unwrap_or(false);
                fake_ok && a_ok
            })
            .unwrap_or(false)
    );
    check!(
        "config builder：xhttp 传输明确报错",
        parse_share_link(cases[1].1)
            .ok()
            .map(|mut node| {
                node.network = "xhttp".into();
                build_config(&[node], &SingBoxOptions::default()).is_err()
            })
            .unwrap_or(false)
    );
    check!(
        "subscription：多行分享链接解析",
        parse_subscription_body(&format!("{}\n{}", cases[0].1, cases[1].1))
            .map(|parsed| parsed.nodes.len() == 2)
            .unwrap_or(false)
    );
    check!(
        "usage：subscription-userinfo 解析",
        parse_userinfo("upload=5335693168; download=363024435813; total=1073741824000; expire=0")
            .map(|usage| usage.total == 1_073_741_824_000 && usage.used() == 368_360_128_981)
            .unwrap_or(false)
    );

    match selftest_delay().await {
        Ok((ok, detail)) => check!(format!("TCP 延迟本地环回取样（{detail}）"), ok),
        Err(error) => {
            failed += 1;
            println!("[FAIL] TCP 延迟本地环回取样: {error}");
        }
    }

    if failed == 0 {
        println!("\nSELFTEST PASS（{passed} 项断言）");
        0
    } else {
        println!("\nSELFTEST FAIL（{passed} 通过，{failed} 失败）");
        1
    }
}

async fn selftest_delay() -> Result<(bool, String), String> {
    let listener = std::net::TcpListener::bind("127.0.0.1:0")
        .map_err(|error| format!("绑定本地监听失败: {error}"))?;
    let addr = listener
        .local_addr()
        .map_err(|error| format!("读取本地端口失败: {error}"))?;
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            if stream.is_err() {
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
    .map_err(|error| error.to_string())?;
    let outcome = tester
        .test_target(DelayTarget::new("127.0.0.1", addr.port()))
        .await;
    let detail = format!(
        "成功 {}/3，最小 {} ms，中位 {} ms",
        outcome.samples_ms.len(),
        outcome.min_ms.unwrap_or(0),
        outcome.median_ms.unwrap_or(0)
    );
    Ok((outcome.ok && outcome.samples_ms.len() == 3, detail))
}
