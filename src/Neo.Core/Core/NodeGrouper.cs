using System.Text;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// 多节点加速的「组队」逻辑：决定哪些节点可以放在同一个 balancer 里一起用。
///   1. **同协议** —— balancer 的 selector 只是把出站分组，内核按延迟挑一个用。
///      混入不同协议（如 vless 与 hysteria2）会导致部分节点走 sing-box、部分走 xray，
///      而本项目单次连接只用一种内核，混协议必然有一半用不了。
///   2. **同地区** —— 用户预期是「同地区多节点提速」，跨地区混用会让出口 IP 在
///      国家之间乱跳，登录态、风控、流媒体解锁都会出问题。
public static class NodeGrouper
{
    /// 地区标签，用于判定「同地区」。与 UI 旗帜判定保持同一套关键词。
    ///   Finland→nl；而 "Hong Kong" 因带空格反而识别不出 → other）。后果是不同国家的节点
    ///   被编进同一 balancer，出口 IP 跨国乱跳。改为：
    ///     ① 中文全名 / 含空格的多词短语按包含匹配（中文没有分隔符，这是唯一可行方式）；
    ///     ② 拉丁名（australia/russia/...）与两字母代码按**分隔符切词后整词匹配**；
    ///     ③ 长名优先于两字母代码，避免 "in"/"us" 这类代码抢走其它国家的长名。
    public static string RegionOf(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Length == 0) return "other";

        // ① 中文全名 / 多词短语。短语两侧必须是词边界，避免 "hong kong" 命中 "xhongkongx"。
        var spaced = new string(n.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        spaced = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var padded = " " + spaced + " ";
        foreach (var (region, phrases, _, _) in RegionDefs)
        {
            foreach (var phrase in phrases)
            {
                if (phrase[0] < 128 && char.IsLetter(phrase[0]))
                {
                    if (padded.Contains(" " + phrase + " ", StringComparison.Ordinal)) return region;
                }
                else if (n.Contains(phrase, StringComparison.Ordinal)) return region;
            }
        }

        // ② 拉丁词整词匹配：先长名，后两字母代码（长词优先）。
        var tokens = Tokenize(n);
        foreach (var (region, _, words, _) in RegionDefs)
            foreach (var word in words)
                if (tokens.Contains(word)) return region;
        foreach (var (region, _, _, codes) in RegionDefs)
            foreach (var code in codes)
                if (tokens.Contains(code)) return region;

        return "other";
    }

    /// 地区关键词表：Phrases=包含匹配（中文/多词），Words=拉丁长名整词，Codes=两字母代码整词。
    /// 顺序即优先级（先查到的先返回）：长名与代码分两轮扫描，保证「长词优先」。
    private static readonly (string Region, string[] Phrases, string[] Words, string[] Codes)[] RegionDefs =
    {
        ("hk", new[] { "香港", "hong kong", "hongkong" }, Array.Empty<string>(), new[] { "hk" }),
        ("jp", new[] { "日本", "东京", "大阪" }, new[] { "japan", "tokyo", "osaka" }, new[] { "jp" }),
        ("sg", new[] { "新加坡", "狮城" }, new[] { "singapore" }, new[] { "sg" }),
        ("us", new[] { "美国", "圣何塞", "洛杉矶", "西雅图", "芝加哥", "纽约", "united states", "los angeles", "san jose", "new york" }, new[] { "usa", "america" }, new[] { "us" }),
        ("kr", new[] { "韩国", "首尔" }, new[] { "korea", "seoul" }, new[] { "kr" }),
        ("gb", new[] { "英国", "伦敦", "united kingdom", "great britain" }, new[] { "britain", "england" }, new[] { "uk", "gb" }),
        ("de", new[] { "德国", "法兰克福" }, new[] { "germany", "frankfurt" }, new[] { "de" }),
        ("nl", new[] { "荷兰" }, new[] { "netherlands", "amsterdam" }, new[] { "nl" }),
        ("au", new[] { "澳大利亚", "悉尼" }, new[] { "australia", "sydney" }, new[] { "au" }),
        ("tw", new[] { "台湾", "台北" }, new[] { "taiwan", "taipei" }, new[] { "tw" }),
        ("ca", new[] { "加拿大", "多伦多" }, new[] { "canada", "toronto" }, new[] { "ca" }),
        ("fr", new[] { "法国", "巴黎" }, new[] { "france", "paris" }, new[] { "fr" }),
        ("ru", new[] { "俄罗斯", "莫斯科" }, new[] { "russia", "moscow" }, new[] { "ru" }),
        ("in", new[] { "印度" }, new[] { "india", "mumbai", "delhi" }, new[] { "in" }),
        ("tr", new[] { "土耳其" }, new[] { "turkey", "istanbul" }, new[] { "tr" }),
    };

    /// <summary>把名字切成 ASCII 字母/数字词；中文等非 ASCII 字符一律当分隔符。</summary>
    private static List<string> Tokenize(string s)
    {
        var list = new List<string>(4);
        var sb = new StringBuilder(16);
        foreach (var c in s)
        {
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// 为当前节点挑选同组节点：同地区 + 同协议，且只用 xray 能承载的协议。
    /// 返回列表第一项固定是「当前节点」本身，保证用户选的那个一定在组里。
    /// <param name="all">全部候选节点</param>
    /// <param name="current">用户当前选中的节点（组的锚点）</param>
    /// <param name="maxNodes">上限，避免出站过多拖慢 observatory 探测</param>
    public static List<NodeItem> PickGroup(IReadOnlyList<NodeItem> all, NodeItem current, int maxNodes = 8)
    {
        var region = RegionOf(current.Name);
        var result = new List<NodeItem> { current };

        // hy2/tuic 走 sing-box，与 xray 出站不兼容，不能混组
        bool XrayCapable(NodeItem n) => n.Protocol is NodeProtocol.VLESS or NodeProtocol.VMess
                                                  or NodeProtocol.Trojan or NodeProtocol.Shadowsocks;

        if (!XrayCapable(current)) return result;

        foreach (var n in all)
        {
            if (result.Count >= maxNodes) break;
            if (!n.IsValid) continue;
            if (SameNode(n, current)) continue;
            if (n.Protocol != current.Protocol) continue;          // 硬约束 1：同协议
            if (RegionOf(n.Name) != region) continue;              // 硬约束 2：同地区
            if (result.Any(r => SameNode(r, n))) continue;
            result.Add(n);
        }

        return result;
    }

    public static bool SameNode(NodeItem a, NodeItem b) =>
        a.EndPoint == b.EndPoint && a.Protocol == b.Protocol && a.Name == b.Name;

    /// <summary>人类可读的地区名，用于日志与界面提示。</summary>
    public static string RegionDisplay(string region) => region switch
    {
        "hk" => "香港",
        "jp" => "日本",
        "sg" => "新加坡",
        "us" => "美国",
        "kr" => "韩国",
        "gb" => "英国",
        "de" => "德国",
        "nl" => "荷兰",
        "au" => "澳大利亚",
        "tw" => "台湾",
        "ca" => "加拿大",
        "fr" => "法国",
        "ru" => "俄罗斯",
        "in" => "印度",
        "tr" => "土耳其",
        _ => "其他地区",
    };
}
