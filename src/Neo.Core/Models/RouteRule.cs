namespace Neo.Core.Models;

public sealed class RouteRule
{
    public string Kind { get; set; } = "domain";

    public string Value { get; set; } = "";

    public string Action { get; set; } = "direct";
}
