namespace Neo.Core;

/// <summary>
/// 本地入站端口的运行期真源（2026-10-23 可配置化）：
/// 之前全仓直接引用 XrayConfigBuilder.DefaultSocksPort 等常量，用户无法改；现在 UI 可改，
/// 运行期统一读这里，state.json 持久化后由 AppState.Load 写入。
/// 约束：socks / http 不能相同，也不能占用统计端口（21810）与 Clash API（21811）。
/// 内核配置生成仍在调用处显式传端口，因此改端口 = 下次连接生效。
/// </summary>
public static class LocalPorts
{
    public const int DefaultSocks = 21808;
    public const int DefaultHttp = 21809;
    public const int FixedStatsApi = 21810;
    public const int FixedClashApi = 21811;
    /// <summary>naive.exe 辅助进程的 socks 监听端口（前置内核通过 socks 出站接它；固定、不对用户开放）。</summary>
    public const int NaiveListen = 21812;

    public static int Socks { get; private set; } = DefaultSocks;
    public static int Http { get; private set; } = DefaultHttp;

    /// <summary>
    /// 当前活跃内核所在槽位（0/1）。无感切换时两个槽位交替使用：
    /// 新内核先在另一个槽位启动并验证就绪，再把中继后端指过去，最后延迟停旧内核。
    /// </summary>
    public static int ActiveSlot { get; set; }

    /// <summary>槽位对应的内核监听端口（0 → +100，1 → +110）。</summary>
    public static int SlotSocks(int slot) => Socks + (slot == 0 ? 100 : 110);
    public static int SlotHttp(int slot) => Http + (slot == 0 ? 100 : 110);
    public static int SlotStats(int slot) => slot == 0 ? FixedStatsApi + 100 : FixedStatsApi + 101;

    /// <summary>当前活跃内核的监听端口（用户端口交给 PortRelay）。</summary>
    public static int CoreSocks => SlotSocks(ActiveSlot);
    public static int CoreHttp => SlotHttp(ActiveSlot);

    public static int StatsApi => FixedStatsApi;
    public static int ClashApi => FixedClashApi;

    /// <summary>活跃内核的统计 API 端口。</summary>
    public static int CoreStatsApi => SlotStats(ActiveSlot);

    /// <summary>sing-box / mihomo 的 clash_api 端口（slot 0 = 21811，slot 1 = 21821）。
    /// 双内核并存时必须分开，否则两个实例抢控制器端口，统计和连接列表都会乱。</summary>
    public static int SlotClashApi(int slot) => FixedClashApi + (slot == 0 ? 0 : 10);

    /// <summary>活跃内核的 clash_api 端口。</summary>
    public static int CoreClashApi => SlotClashApi(ActiveSlot);

    /// <summary>校验并应用；返回错误文案（null = 成功）。</summary>
    public static string? TryApply(int socks, int http)
    {
        if (socks < 1024 || socks > 65535) return "SOCKS 端口需在 1024–65535";
        if (http < 1024 || http > 65535) return "HTTP 端口需在 1024–65535";
        if (socks == http) return "SOCKS 与 HTTP 端口不能相同";
        // 2026-10-23 审计 P1-7：槽位端口（21910/21911/21821）与 naive 端口也不能被用户端口占用，
        // 否则连接时 EnsureRelays 会抛 SocketException，留下「内核在跑但未连接」的半状态。
        var reserved = new[]
        {
            FixedStatsApi, FixedStatsApi + 100, FixedStatsApi + 101,
            FixedClashApi, FixedClashApi + 10, NaiveListen,
        };
        if (reserved.Contains(socks) || reserved.Contains(http))
            return $"不能占用保留端口（统计 {FixedStatsApi}+ / Clash API {FixedClashApi}+ / 槽位 / naive {NaiveListen}）";
        // 2026-10-24 审查 P1-2：还要校验槽位派生端口（socks/http ±100/110）。
        //   否则用户把 HTTP 设成 21908 这类值时，内核/中继会抢同一端口，连接时抛 SocketException，
        //   留下「内核在跑但未连接」的半启动态。
        if (socks + 110 > 65535 || http + 110 > 65535)
            return "端口过大：槽位派生端口（+110）超出 65535";
        var derived = new[] { socks + 100, socks + 110, http + 100, http + 110 };
        if (derived.Contains(socks) || derived.Contains(http))
            return "用户端口不能与槽位派生端口（本身 +100/+110）重叠";
        if (derived.Any(d => reserved.Contains(d)))
            return "端口会与槽位统计/Clash 保留端口冲突（socks/http +100/+110）";
        if (derived.Distinct().Count() != derived.Length)
            return "SOCKS/HTTP 端口的槽位派生值互相冲突（两个端口相差 10/100/110 会被拒绝）";
        Socks = socks;
        Http = http;
        return null;
    }

    public static void Reset()
    {
        Socks = DefaultSocks;
        Http = DefaultHttp;
    }
}
