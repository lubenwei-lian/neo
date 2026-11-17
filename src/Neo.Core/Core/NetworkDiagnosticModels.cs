namespace Neo.Core.Core;

public sealed record NetworkDiagnosticTarget(string Name, string Group, Uri Url, bool Requires204 = false);
public sealed record NetworkRouteEvidence(string Path, string Outbound, string Source, string Detail)
{
    public static NetworkRouteEvidence Unknown(string detail) => new("未确认", "—", "未取得内核证据", detail);
}
public sealed record WebsiteDiagnosticResult(NetworkDiagnosticTarget Target, string State, int? HttpStatus,
    long ElapsedMs, NetworkRouteEvidence Route, string Detail)
{
    public IReadOnlyList<WebsiteDiagnosticAttempt> Attempts { get; init; } = Array.Empty<WebsiteDiagnosticAttempt>();
    public bool RecoveredAfterRetry => State == "可达" && Attempts.Count > 1;
}
public sealed record WebsiteDiagnosticAttempt(int Number, string State, int? HttpStatus, long ElapsedMs,
    NetworkRouteEvidence Route, string Detail);
public sealed record NetworkDiagnosticSession(int Epoch, string Kind, string ConfigPath, bool Tun, string Mode);

public static class NetworkDiagnosticTargets
{
    public static IReadOnlyList<NetworkDiagnosticTarget> All { get; } = Array.AsReadOnly<NetworkDiagnosticTarget>(
    [
        new("Google", "国外", new("https://www.google.com/generate_204"), true),
        new("Cloudflare", "国外", new("https://cp.cloudflare.com/generate_204"), true),
        new("X", "国外", new("https://x.com/")),
        new("Steam 商店", "国外", new("https://store.steampowered.com/")),
        new("Steam 社区", "国外", new("https://steamcommunity.com/")),
        new("百度", "国内", new("https://www.baidu.com/")),
        new("哔哩哔哩", "国内", new("https://www.bilibili.com/")),
        new("腾讯", "国内", new("https://www.qq.com/")),
        // Test the website users visit; shared exits can exhaust the unrelated anonymous REST quota.
        new("GitHub", "国外", new("https://github.com/")),
        new("YouTube", "国外", new("https://www.youtube.com/")),
        new("Microsoft", "国外", new("https://www.microsoft.com/")),
        new("维基百科", "国外", new("https://www.wikipedia.org/")),
        new("Apple", "国外", new("https://www.apple.com/library/test/success.html")),
        new("Mozilla", "国外", new("https://detectportal.firefox.com/success.txt")),
        new("Debian", "国外", new("https://www.debian.org/")),
        new("Python", "国外", new("https://www.python.org/")),
        new("阿里云", "国内", new("https://www.aliyun.com/")),
        new("京东", "国内", new("https://www.jd.com/")),
        new("网易", "国内", new("https://www.163.com/")),
        new("必应", "国外", new("https://www.bing.com/")),
    ]);

}
