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
    /// 内核实际监听的端口（2026-10-23 无感切换）：用户端口交给 PortRelay，
    /// 内核只监听 +100 的内部端口，切换节点时只改 Relay 的后端指针。
    /// </summary>
    public static int CoreSocks => Socks + 100;
    public static int CoreHttp => Http + 100;

    /// <summary>热备内核（无感切换的备用实例）的端口。</summary>
    public static int StandbySocks => Socks + 110;
    public static int StandbyHttp => Http + 110;

    public static int StatsApi => FixedStatsApi;
    public static int ClashApi => FixedClashApi;

    /// <summary>活跃内核的统计 API 端口（与热备实例区分，避免冲突）。</summary>
    public static int CoreStatsApi => FixedStatsApi + 100;

    /// <summary>热备内核的统计 API 端口。</summary>
    public static int StandbyStatsApi => FixedStatsApi + 101;

    /// <summary>校验并应用；返回错误文案（null = 成功）。</summary>
    public static string? TryApply(int socks, int http)
    {
        if (socks < 1024 || socks > 65535) return "SOCKS 端口需在 1024–65535";
        if (http < 1024 || http > 65535) return "HTTP 端口需在 1024–65535";
        if (socks == http) return "SOCKS 与 HTTP 端口不能相同";
        if (socks == FixedStatsApi || http == FixedStatsApi || socks == FixedClashApi || http == FixedClashApi)
            return $"不能使用固定端口 {FixedStatsApi}（统计）/ {FixedClashApi}（Clash API）";
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
