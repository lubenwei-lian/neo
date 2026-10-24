//! 故障转移策略模型（与Windows 端 FailoverService / Android 端 FailoverGuard 对齐的纯状态机）。
//!
//! 规则：
//! - 连续失败 N 次触发切换（有真实流量时阈值更高，避免误切）；成功后清零；
//! - 切换后进入冷却期，冷却内不再切换；
//! - 冷却结束、再次连续失败才允许下一次切换。

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FailoverPolicy {
    /// 无流量时的连续失败阈值（默认 3）
    pub threshold_idle: u32,
    /// 有流量时的阈值（默认 4）
    pub threshold_active: u32,
    /// 切换冷却（毫秒，默认 90_000）
    pub cooldown_ms: i64,
}

impl Default for FailoverPolicy {
    fn default() -> Self {
        Self { threshold_idle: 3, threshold_active: 4, cooldown_ms: 90_000 }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    /// 继续观察
    Keep,
    /// 触发切换
    Switch,
    /// 冷却中/已有切换进行中，本轮不动作
    Cooldown,
}

#[derive(Debug, Default)]
pub struct FailoverState {
    fails: u32,
    last_switch_ms: Option<i64>,
}

impl FailoverState {
    pub fn fails(&self) -> u32 {
        self.fails
    }

    /// 从外部保存的状态恢复（桌面/手机桥接用：调用方只保存 fails/lastSwitch 两个数）。
    pub fn from_parts(fails: u32, last_switch_ms: Option<i64>) -> Self {
        Self { fails, last_switch_ms }
    }

    pub fn last_switch_ms(&self) -> Option<i64> {
        self.last_switch_ms
    }

    /// 一次探活结果。`had_traffic` = 最近窗口有真实流量（更保守）。
    pub fn on_probe(&mut self, now_ms: i64, ok: bool, had_traffic: bool, policy: &FailoverPolicy) -> Decision {
        if ok {
            self.fails = 0;
            return Decision::Keep;
        }
        if let Some(last) = self.last_switch_ms {
            if now_ms - last < policy.cooldown_ms {
                // 冷却期：失败计数清零，避免冷却结束后“一次性立即切”。
                self.fails = 0;
                return Decision::Cooldown;
            }
        }
        self.fails += 1;
        let threshold = if had_traffic { policy.threshold_active } else { policy.threshold_idle };
        if self.fails >= threshold {
            self.fails = 0;
            self.last_switch_ms = Some(now_ms);
            Decision::Switch
        } else {
            Decision::Keep
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn three_idle_failures_trigger_switch() {
        let p = FailoverPolicy::default();
        let mut s = FailoverState::default();
        assert_eq!(s.on_probe(0, false, false, &p), Decision::Keep);
        assert_eq!(s.on_probe(1_000, false, false, &p), Decision::Keep);
        assert_eq!(s.on_probe(2_000, false, false, &p), Decision::Switch);
    }

    #[test]
    fn active_traffic_raises_threshold() {
        let p = FailoverPolicy::default();
        let mut s = FailoverState::default();
        assert_eq!(s.on_probe(0, false, true, &p), Decision::Keep);
        assert_eq!(s.on_probe(1_000, false, true, &p), Decision::Keep);
        assert_eq!(s.on_probe(2_000, false, true, &p), Decision::Keep);
        assert_eq!(s.on_probe(3_000, false, true, &p), Decision::Switch);
    }

    #[test]
    fn success_resets_counter() {
        let p = FailoverPolicy::default();
        let mut s = FailoverState::default();
        s.on_probe(0, false, false, &p);
        s.on_probe(1_000, false, false, &p);
        assert_eq!(s.on_probe(1_500, true, false, &p), Decision::Keep);
        assert_eq!(s.fails(), 0);
        assert_eq!(s.on_probe(2_000, false, false, &p), Decision::Keep);
    }

    #[test]
    fn cooldown_blocks_and_clears_counter() {
        let p = FailoverPolicy::default();
        let mut s = FailoverState::default();
        s.on_probe(0, false, false, &p);
        s.on_probe(100, false, false, &p);
        assert_eq!(s.on_probe(200, false, false, &p), Decision::Switch);
        // 冷却内继续失败：不切、计数清零
        assert_eq!(s.on_probe(10_000, false, false, &p), Decision::Cooldown);
        assert_eq!(s.fails(), 0);
        // 冷却结束后重新累计 3 次才切
        let t = 200 + p.cooldown_ms;
        assert_eq!(s.on_probe(t, false, false, &p), Decision::Keep);
        assert_eq!(s.on_probe(t + 1, false, false, &p), Decision::Keep);
        assert_eq!(s.on_probe(t + 2, false, false, &p), Decision::Switch);
    }
}