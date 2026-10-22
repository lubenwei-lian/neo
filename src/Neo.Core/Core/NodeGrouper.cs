using System.Text;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// 地区判定工具：把节点名归到一个地区代码（hk/jp/sg/…），供 UI 的地区筛选、
///   「实际出口与标记是否一致」提示等展示使用。
///   历史上这里还负责「多节点加速」的同协议 + 同地区组队；该功能已移除
///   （内核 balancer 按连接分流，无法真正并行，实测比单节点更慢），组队逻辑随之删除。
public static class NodeGrouper
{
    /// 地区标签，用于判定「同地区」。与 UI 旗帜判定保持同一套关键词。
    ///   Finland→nl；而 "Hong Kong" 因带空格反而识别不出 → other）。后果是不同国家的节点
    ///   地区判断失效，出口 IP 跨国乱跳的提示也会错。改为：
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
