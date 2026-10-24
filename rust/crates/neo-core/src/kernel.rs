//! 内核选择逻辑（与Windows 端 KernelRouter 对齐的纯规则，可单测）。
//!
//! 背景：neo 同时带 xray 与 sing-box 两套内核，协议/传输能力不同；选错会表现为
//! 「连上了但所有域名超时」或直接启动失败。这里把判定规则抽成纯函数。

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Kernel {
    Xray,
    SingBox,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct KernelHint {
    /// 协议名（小写：vless/vmess/trojan/ss/hysteria2/tuic/anytls/wireguard/socks...）
    pub protocol: String,
    /// 传输（小写：tcp/ws/grpc/httpupgrade/xhttp/kcp...）
    pub network: String,
    /// 是否用到只有 xray 支持的特性（VLESS 加密、REALITY pqv、vcn 等）
    pub xray_only_feature: bool,
}

/// sing-box 才能承载的协议（xray 没有对应出站）。
pub fn is_singbox_only_protocol(protocol: &str) -> bool {
    matches!(protocol, "hysteria2" | "tuic" | "anytls" | "wireguard" | "naive")
}

/// 只有 xray 支持的传输。
pub fn is_xray_only_network(network: &str) -> bool {
    matches!(network, "xhttp" | "kcp" | "mkcp")
}

/// 返回应使用的内核。规则优先级（）：
///   xray 专有能力 > 传输限制 > sing-box 专有协议 > 默认 xray。
pub fn pick(hint: &KernelHint) -> Kernel {
    if hint.xray_only_feature {
        return Kernel::Xray;
    }
    let protocol = hint.protocol.to_ascii_lowercase();
    let network = hint.network.to_ascii_lowercase();
    if is_xray_only_network(&network) {
        return Kernel::Xray;
    }
    if is_singbox_only_protocol(&protocol) {
        return Kernel::SingBox;
    }
    Kernel::Xray
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn udp_protocols_go_singbox() {
        for p in ["hysteria2", "tuic", "anytls", "wireguard"] {
            assert_eq!(pick(&KernelHint { protocol: p.into(), ..Default::default() }), Kernel::SingBox, "{p}");
        }
    }

    #[test]
    fn xray_only_transports_go_xray() {
        for n in ["xhttp", "kcp", "mkcp"] {
            assert_eq!(pick(&KernelHint { protocol: "vless".into(), network: n.into(), ..Default::default() }), Kernel::Xray, "{n}");
        }
    }

    #[test]
    fn xray_only_feature_beats_everything() {
        let h = KernelHint { protocol: "vless".into(), network: "ws".into(), xray_only_feature: true };
        assert_eq!(pick(&h), Kernel::Xray);
    }

    #[test]
    fn common_tcp_protocols_default_xray() {
        for p in ["vless", "vmess", "trojan", "ss", "socks"] {
            assert_eq!(pick(&KernelHint { protocol: p.into(), network: "tcp".into(), ..Default::default() }), Kernel::Xray, "{p}");
        }
    }
}