using System.IO;

namespace Neo.Core.Core;

/// CN 分流清单的完整性护栏。
///   xray 路径吃 v2fly `geosite.dat` 的 CN 分类（111,415 条），
///   而 sing-box(TUN) 路径吃上游精简 `geosite-cn.srs`（仅 9,302 条）——
///   两者差 12 倍。后果：TUN 下大量**国内域名**被判为"非国内"而走代理，
///   DNS 也会跟着走经代理的 dns-remote，表现为"什么都慢"。
/// neo 自 0.2.9 起改为**随包只带全量清单**（由 `tools/gen-rulesets.py` 从
/// 同目录的 `.dat` 生成，与 xray 路径同源）。但升级安装、手工替换、
/// 或把官方精简版拷回来，都可能让清单**静默退化**回旧规模 ——
/// 而退化后程序一切"正常"，只是国内域名悄悄绕道，用户很难察觉。
/// 因此启动时做一次体量核对，退化就在日志里显式告警。
/// 注意：这是**体量启发式**护栏，不是对清单内容的语义校验。
/// 它拦得住"换成上游精简版"这类退化，拦不住"被替换成另一份同等大小的错误清单"。
/// 内容层面的保证由 `tools/gen-rulesets.py --check` 在打包时把守。
public static class RuleSetIntegrity
{
    public const string GeoSiteCnFile = "geosite-cn.srs";
    public const string GeoIpCnFile = "geoip-cn.srs";
    public const string RuleSetsSubDir = "rule-sets";

    //   geosite-cn.srs : 全量 525,969 B（domain_suffix 110,853） / 上游精简 56,191 B（8,702）
    //   geoip-cn.srs   : 全量  36,370 B（ip_cidr   9,612）      / 上游精简 34,185 B（8,045）
    // 下限取在两者之间，留出重新生成时的正常浮动余量。
    private const long MinGeoBytes = 300_000;
    private const long MinGeoIpBytes = 35_000;

    public sealed record Finding(string File, long ActualBytes, long MinimumBytes, string Note);

    /// <summary>返回所有不达标项；为空表示通过。</summary>
    public static IReadOnlyList<Finding> Check()
    {
        var dir = Path.Combine(NeoPaths.RuntimeDir, RuleSetsSubDir);
        var list = new List<Finding>();
        Probe(list, dir, GeoSiteCnFile, MinGeoBytes,
            "国内域名清单体量过小 —— TUN 下大量国内域名会被判为境外而走代理");
        Probe(list, dir, GeoIpCnFile, MinGeoIpBytes,
            "国内 IP 清单体量过小 —— 未收录域名的解析结果可能落不进 CN 网段");
        return list;
    }

    private static void Probe(List<Finding> list, string dir, string name, long min, string note)
    {
        try
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                list.Add(new Finding(name, -1, min, "文件缺失"));
                return;
            }
            var len = new FileInfo(path).Length;
            if (len < min) list.Add(new Finding(name, len, min, note));
        }
        catch
        {
            // 读取失败不阻断启动，交由上游的 missing-file 报错处理
        }
    }
}
