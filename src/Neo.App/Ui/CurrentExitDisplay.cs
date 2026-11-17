using System.Globalization;
using Neo.Core.Core;

namespace Neo.App.Ui;

internal static class CurrentExitDisplay
{
    internal static string Region(string country)
    {
        var known = NodeGrouper.RegionDisplay(country.ToLowerInvariant());
        if (known != "其他地区") return known;
        if (country.Equals("cn", StringComparison.OrdinalIgnoreCase)) return "中国";
        try { return new RegionInfo(country.ToUpperInvariant()).DisplayName; }
        catch (ArgumentException) { return country.ToUpperInvariant(); }
    }
}
