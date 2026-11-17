namespace Neo.App.Ui;

/// <summary>Presentation only; never participates in recovery or node selection.</summary>
internal static class ConnectivityDisplay
{
    internal readonly record struct Summary(bool Complete, int Finished, int Passed, int Total)
    {
        internal string Percentage => Complete ? $"{100d * Passed / Total:0.#}%" : "—";
    }

    internal static Summary Summarize(IReadOnlyList<string> states, bool validSession, bool running, bool finished)
    {
        static bool Final(string state) => state is "可达" or "失败" or "站点限制" or "响应异常" or "站点异常" or "检测异常";
        var done = states.Count(Final);
        var passed = states.Count(s => s == "可达");
        return new(validSession && !running && finished && states.Count > 0 && done == states.Count,
            done, passed, states.Count);
    }

    internal static string Site(string? state) => state switch
    {
        "可达" => "通过", "失败" => "失败", "站点限制" => "受限", "检测中" => "检测中",
        "已取消" => "已停止", "检测异常" => "异常", "响应异常" => "响应异常", "站点异常" => "站点异常", _ => "—"
    };
}
