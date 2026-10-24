//! 内核重启退避策略（与Windows 端 ConnectionService 的自动重启逻辑对齐）。
//!
//! 规则：
//! - 第 1/2/3 次分别等 3s / 10s / 30s；超过 max_attempts 不再自动重启；
//! - 运行超过 stable_after 秒后再崩溃，视为「稳定期崩溃」，计数清零重新开始。

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RestartPolicy {
    /// 每次重启前的等待毫秒数
    pub delays_ms: Vec<u64>,
    /// 最大连续重启次数
    pub max_attempts: u32,
    /// 运行多久算「稳定」（毫秒）
    pub stable_after_ms: i64,
}

impl Default for RestartPolicy {
    fn default() -> Self {
        Self { delays_ms: vec![3_000, 10_000, 30_000], max_attempts: 3, stable_after_ms: 60_000 }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RestartDecision {
    /// 等指定毫秒后重启（attempt 从 1 开始）
    Restart { delay_ms: u64, attempt: u32 },
    /// 连续崩溃次数用尽，放弃自动重启
    GiveUp,
}

#[derive(Debug, Default)]
pub struct RestartState {
    attempts: u32,
}

impl RestartState {
    pub fn attempts(&self) -> u32 {
        self.attempts
    }

    /// 内核退出时调用。`uptime_ms` = 本次会话运行时长。
    pub fn on_core_exited(&mut self, uptime_ms: i64, policy: &RestartPolicy) -> RestartDecision {
        if uptime_ms >= policy.stable_after_ms {
            self.attempts = 0;
        }
        self.attempts += 1;
        if self.attempts > policy.max_attempts {
            return RestartDecision::GiveUp;
        }
        let idx = (self.attempts as usize - 1).min(policy.delays_ms.len().saturating_sub(1));
        RestartDecision::Restart { delay_ms: policy.delays_ms[idx], attempt: self.attempts }
    }

    /// 用户主动断开 / 手动重连成功后清零。
    pub fn reset(&mut self) {
        self.attempts = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backoff_sequence_then_give_up() {
        let p = RestartPolicy::default();
        let mut s = RestartState::default();
        assert_eq!(s.on_core_exited(1_000, &p), RestartDecision::Restart { delay_ms: 3_000, attempt: 1 });
        assert_eq!(s.on_core_exited(1_000, &p), RestartDecision::Restart { delay_ms: 10_000, attempt: 2 });
        assert_eq!(s.on_core_exited(1_000, &p), RestartDecision::Restart { delay_ms: 30_000, attempt: 3 });
        assert_eq!(s.on_core_exited(1_000, &p), RestartDecision::GiveUp);
    }

    #[test]
    fn stable_run_resets_counter() {
        let p = RestartPolicy::default();
        let mut s = RestartState::default();
        s.on_core_exited(1_000, &p);
        s.on_core_exited(1_000, &p);
        // 稳定运行 90 秒后崩溃：计数清零，重新从 3s 开始
        assert_eq!(s.on_core_exited(90_000, &p), RestartDecision::Restart { delay_ms: 3_000, attempt: 1 });
    }

    #[test]
    fn manual_reset_clears() {
        let p = RestartPolicy::default();
        let mut s = RestartState::default();
        s.on_core_exited(1_000, &p);
        s.reset();
        assert_eq!(s.attempts(), 0);
    }
}