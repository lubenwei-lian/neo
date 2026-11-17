using Neo.Core.Models;
namespace Neo.App.Tests;
public static class SubscriptionFetcher
{
    public sealed class Result
    {
        public bool Success;
        public string? Error, RawText, TrafficInfo;
        public List<NodeItem> Nodes = [];
        public long SubUpload, SubDownload, SubTotal, SubExpireUnix;
    }
    public static Func<string, Task<Result>> FetchHook = url => Task.FromResult(new Result());
    public static Task<Result> FetchAsync(string url) => FetchHook(url);
}
public static class SubscriptionLogService
{
    public static string Begin(string name, string host, string kind) => "test";
    public static string HostOf(string url) => "synthetic.invalid";
    public static void Finish(string id, string status, string http, int count, string? error, string? parseError, string detail, double ms) { }
}
