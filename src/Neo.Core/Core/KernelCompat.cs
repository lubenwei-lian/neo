using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Neo.Core.Core;

/// <summary>内核种类。</summary>
public enum KernelKind
{
    Xray,
    SingBox,
}

/// <summary>配置预检结果。InfraFailed=true 表示预检本身没跑起来（缺 exe/超时），不代表配置有错。</summary>
public readonly record struct KernelCheckResult(bool Ok, bool InfraFailed, string Output);

/// <summary>
/// 内核兼容层：版本解析 + 能力表 + 配置自适应。
///
/// 目的：内核版本变化（字段被弃用/传输被移除）时，让 neo 自己把配置迁移/降级到新内核能接受的形式，
/// 而不是生成一份被内核拒绝的配置、表现为「内核启动后立即退出」等人来修。
///
/// 设计：除了 <see cref="RunCheck"/> 会起一次内核子进程做 -test/check，其余全是纯函数，便于单独验证。
/// 自检：调用 <see cref="SelfTest"/>（不依赖单元测试框架），返回空串=通过；失败返回失败项。
/// 临时验证命令（无需测试工程）：把 KernelCompat.SelfTest() 的返回值打进日志/控制台即可，
/// 或在调试器「即时窗口」执行 System.Console.WriteLine(KernelCompat.SelfTest())。
/// </summary>
public static class KernelCompat
{
    /// <summary>sing-box 从该版本起移除 DNS 规则旧 server 字段与旧地址过滤字段（evaluate+match_response 取代）。</summary>
    public static readonly Version SingBoxLegacyDnsRemovedIn = new(1, 16, 0);

    /// <summary>xray 从该主版本起移除 http/h2/quic 传输（ws 仍可用但会告警）。</summary>
    public static readonly Version XrayLegacyTransportRemovedIn = new(26, 0, 0);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---- 版本解析 ----

    private static readonly Regex XrayVersionRe = new(@"Xray\s+(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SingBoxVersionRe = new(@"(?:sing-box\s+)?version\s+(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DottedVersionRe = new(@"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    /// <summary>解析 `xray version` 输出（也兼容只传版本串，如 "25.9.11"）。失败返回 null。</summary>
    public static Version? ParseXrayVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = XrayVersionRe.Match(output);
        if (m.Success) return MakeVersion(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        return ParseDotted(output.Trim());
    }

    /// <summary>解析 `sing-box version` 输出（也兼容只传版本串，如 "1.14.3"；预发布后缀忽略）。失败返回 null。</summary>
    public static Version? ParseSingBoxVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = SingBoxVersionRe.Match(output);
        if (m.Success) return ParseDotted(m.Groups[1].Value);
        return ParseDotted(output.Trim());
    }

    /// <summary>按内核种类解析版本。</summary>
    public static Version? ParseVersion(string? output, KernelKind kind)
        => kind == KernelKind.Xray ? ParseXrayVersion(output) : ParseSingBoxVersion(output);

    private static Version? ParseDotted(string text)
    {
        var m = DottedVersionRe.Match(text);
        if (!m.Success) return null;
        return MakeVersion(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }

    private static Version MakeVersion(string major, string minor, string patch)
    {
        int.TryParse(major, out var ma);
        int.TryParse(minor, out var mi);
        int.TryParse(patch, out var pa);
        return new Version(ma, mi, pa);
    }

    // ---- 能力表 ----

    /// <summary>sing-box 该版本是否仍允许 DNS 规则旧字段（server/ip_is_private 等）。</summary>
    public static bool SingBoxAllowsLegacyDnsRuleFields(Version? version)
        => version == null || version < SingBoxLegacyDnsRemovedIn;

    /// <summary>xray 该版本是否仍支持该传输方式（http/h2/quic 自 26 起移除；ws 仍支持但弃用）。</summary>
    public static bool XraySupportsTransport(Version? version, string? network)
    {
        if (version == null) return true;
        var n = (network ?? "").ToLowerInvariant();
        if (n is "http" or "h2" or "quic") return version < XrayLegacyTransportRemovedIn;
        return true;
    }

    /// <summary>xray 该版本下该传输是否「仍可用但已弃用/会告警」（目前只有 ws）。</summary>
    public static bool XrayTransportIsDeprecated(Version? version, string? network)
        => version != null && version >= XrayLegacyTransportRemovedIn
           && string.Equals(network, "ws", StringComparison.OrdinalIgnoreCase);

    // ---- 配置自适应（纯函数） ----

    /// <summary>
    /// 按内核种类+版本适配配置。返回适配后的 JSON（无需改动时原样返回）。
    /// notes：需要留痕的说明。凡「已自适应」的改动都写在 note 里，调用方应写 LogBus + IncidentLog；
    /// 以「版本提示：」开头的是能力注记（不修改配置）。
    /// </summary>
    public static string Adapt(string json, KernelKind kind, Version? version, out List<string> notes)
    {
        notes = new List<string>();
        if (string.IsNullOrWhiteSpace(json) || version == null) return json;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (Exception ex)
        {
            notes.Add($"版本提示：配置 JSON 解析失败，保留原配置（{ex.GetType().Name}）");
            return json;
        }
        if (root is not JsonObject obj) return json;

        bool changed = kind == KernelKind.SingBox
            ? AdaptSingBox(obj, version, notes)
            : AnnotateXray(obj, version, notes);
        if (!changed) return json;

        try { return obj.ToJsonString(Indented); }
        catch (Exception ex)
        {
            notes.Add($"版本提示：适配结果序列化失败，保留原配置（{ex.GetType().Name}）");
            return json;
        }
    }

    private static readonly string[] LegacyAddressFilterKeys = { "ip_is_private", "ip_cidr", "geoip" };

    /// <summary>可作为 evaluate 作用域的查询匹配字段（有这些条件时，evaluate 只对匹配的查询发探测）。</summary>
    private static readonly string[] QueryScopeKeys =
    {
        "domain", "domain_suffix", "domain_keyword", "domain_regex", "rule_set",
        "query_type", "ip_version", "network", "protocol", "inbound",
        "source_ip_cidr", "source_ip_is_private", "source_port", "source_port_range",
        "port", "port_range", "process_name", "process_path", "package_name", "user", "auth_user",
    };

    private static readonly string[] LocalDomainSuffixes =
    {
        ".local", ".lan", ".home.arpa", ".internal", ".intranet", ".localdomain",
    };

    /// <summary>sing-box >= 1.16：DNS 规则旧字段迁移。返回是否改动了配置。</summary>
    private static bool AdaptSingBox(JsonObject root, Version version, List<string> notes)
    {
        if (version < SingBoxLegacyDnsRemovedIn) return false;
        if (root["dns"] is not JsonObject dns || dns["rules"] is not JsonArray rules) return false;

        bool changed = false;
        var rebuilt = new JsonArray();
        foreach (var item in rules)
        {
            if (item is not JsonObject)
            {
                if (item != null) rebuilt.Add(item.DeepClone());
                continue;
            }

            // 深拷贝后再改：原节点仍挂在旧 rules 数组上，直接 Add 到新数组会抛「node already has a parent」。
            var rule = (JsonObject)item.DeepClone();

            // ① 旧 server 字段：1.16 移除，改为显式 action=route（server 仍是 route 动作的字段）。
            if (rule.ContainsKey("server") && !rule.ContainsKey("action"))
            {
                rule["action"] = "route";
                changed = true;
                notes.Add("DNS 规则旧 server 字段已迁移为 action=route（sing-box 1.16）");
            }

            // ② 1.14 弃用、1.16 移除的旧字段：strategy / rule_set_ip_cidr_accept_empty / outbound。
            foreach (var removedKey in new[] { "strategy", "rule_set_ip_cidr_accept_empty", "outbound" })
            {
                if (!rule.ContainsKey(removedKey)) continue;
                rule.Remove(removedKey);
                changed = true;
                notes.Add($"已移除 DNS 规则不兼容旧字段 {removedKey}（sing-box 1.16）");
            }

            // ③ 旧地址过滤字段（未配 match_response）：先尝试等价迁移（evaluate+match_response），
            //    无法等价（规则没有任何查询匹配条件，evaluate 会对所有查询多发一次探测）时退化为本地域名直连规则。
            var legacyKeys = LegacyAddressFilterKeys.Where(rule.ContainsKey).ToList();
            if (legacyKeys.Count > 0 && !rule.ContainsKey("match_response"))
            {
                var evalRule = BuildEvaluateRule(rule);
                if (evalRule != null)
                {
                    rebuilt.Add(evalRule);
                    rule["match_response"] = true;
                    if (rule["server"] is JsonValue sv && sv.TryGetValue<string>(out var serverTag) && !string.IsNullOrWhiteSpace(serverTag))
                        rule["action"] = "route";
                    changed = true;
                    notes.Add($"DNS 规则旧地址过滤字段（{string.Join("/", legacyKeys)}）已迁移为 evaluate+match_response（sing-box 1.16）");
                }
                else
                {
                    foreach (var k in legacyKeys) rule.Remove(k);
                    AddFallbackLocalDomainRule(rule);
                    changed = true;
                    notes.Add($"DNS 规则旧地址过滤字段（{string.Join("/", legacyKeys)}）无法等价迁移，已退化为本地域名直连规则（语义可能不完备，sing-box 1.16）");
                }
            }

            // ④ 兜底：仍残留的旧地址过滤字段一律删掉 —— 绝不把含旧字段的配置交给 1.16 内核。
            if (!rule.ContainsKey("match_response"))
            {
                foreach (var k in LegacyAddressFilterKeys)
                {
                    if (!rule.ContainsKey(k)) continue;
                    rule.Remove(k);
                    changed = true;
                    notes.Add($"已移除不兼容字段 {k}（sing-box 1.16）");
                }
            }

            rebuilt.Add(rule);
        }

        if (changed) dns["rules"] = rebuilt;
        return changed;
    }

    /// <summary>
    /// 判定一条含旧地址过滤字段的 DNS 规则能否等价迁移为 evaluate+match_response：
    /// 必须带 server，且至少有一个可限定范围的查询匹配字段（否则 evaluate 会对所有查询多发一次探测）。
    /// </summary>
    private static JsonObject? BuildEvaluateRule(JsonObject rule)
    {
        if (rule["server"] is not JsonValue sv || !sv.TryGetValue<string>(out var server) || string.IsNullOrWhiteSpace(server))
            return null;
        var scoped = QueryScopeKeys.Where(rule.ContainsKey).ToList();
        if (scoped.Count == 0) return null;

        var eval = new JsonObject { ["action"] = "evaluate", ["server"] = server };
        foreach (var key in scoped) eval[key] = rule[key]?.DeepClone();
        return eval;
    }

    /// <summary>无法等价迁移时的退化规则：本地域名后缀走直连（原规则保留 server/action）。</summary>
    private static void AddFallbackLocalDomainRule(JsonObject rule)
    {
        if (!rule.ContainsKey("action") && rule.ContainsKey("server")) rule["action"] = "route";

        if (rule["domain_suffix"] is JsonArray arr)
        {
            foreach (var suffix in LocalDomainSuffixes)
            {
                var exists = arr.Any(x => x is JsonValue v && v.TryGetValue<string>(out var t)
                    && string.Equals(t, suffix, StringComparison.OrdinalIgnoreCase));
                if (!exists) arr.Add(suffix);
            }
            return;
        }

        var list = new JsonArray();
        foreach (var suffix in LocalDomainSuffixes) list.Add(suffix);
        rule["domain_suffix"] = list;
    }

    /// <summary>xray：只做版本注记（h2/quic/http 在生成阶段已抛异常；ws 仍可用但弃用）。返回 false（不修改配置）。</summary>
    private static bool AnnotateXray(JsonObject root, Version version, List<string> notes)
    {
        if (root["outbounds"] is not JsonArray outs) return false;
        foreach (var item in outs)
        {
            if (item is not JsonObject outbound) continue;
            var network = (outbound["streamSettings"] as JsonObject)?["network"]?.GetValue<string>() ?? "";
            if (XrayTransportIsDeprecated(version, network))
                notes.Add($"版本提示：xray {version} 仍支持 {network} 传输，但已弃用并会告警，建议后续换 grpc/xhttp/tcp");
            else if (!XraySupportsTransport(version, network))
                notes.Add($"版本提示：xray {version} 已移除 {network} 传输（生成阶段本应已拦截），该配置会被内核拒绝");
        }
        return false;
    }

    private static readonly string[] OptionalPerfKeys =
    {
        "sockopt", "observatory", "burstObservatory", "mux", "udp_over_tcp",
    };

    /// <summary>预检失败的第 1 级降级：去掉性能向可选字段（sockopt / observatory / mux 等）。</summary>
    public static string StripOptionalFields(string json, KernelKind kind, out List<string> notes)
    {
        notes = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var root = JsonNode.Parse(json);
            if (root == null) return json;
            var removed = new List<string>();
            StripKeys(root, removed);
            if (removed.Count == 0) return json;
            notes.Add($"已去掉性能向可选字段（{string.Join("、", removed.Distinct())}）");
            return root.ToJsonString(Indented);
        }
        catch (Exception ex)
        {
            notes.Add($"去可选字段失败、保留原配置（{ex.GetType().Name}）");
            return json;
        }
    }

    private static void StripKeys(JsonNode? node, List<string> removed)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in OptionalPerfKeys)
            {
                if (!obj.ContainsKey(key)) continue;
                obj.Remove(key);
                removed.Add(key);
            }
            foreach (var kv in obj.ToList()) StripKeys(kv.Value, removed);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) StripKeys(item, removed);
        }
    }

    // ---- 预检执行 ----

    /// <summary>
    /// 用内核自己的校验模式预检一份配置：xray = `xray run -test -c`，sing-box = `check -c`。
    /// 同步阻塞 timeoutMs（调用方应放到 Task.Run 里）；退出码 0 = 通过。
    /// </summary>
    public static KernelCheckResult RunCheck(string exePath, KernelKind kind, string configPath, int timeoutMs = 8000)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return new KernelCheckResult(false, true, $"找不到内核可执行文件：{exePath}");
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return new KernelCheckResult(false, true, $"找不到待预检配置：{configPath}");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (kind == KernelKind.Xray)
            {
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("-test");
                psi.ArgumentList.Add("-c");
            }
            else
            {
                psi.ArgumentList.Add("check");
                psi.ArgumentList.Add("-c");
            }
            psi.ArgumentList.Add(configPath);
            // 与真实启动保持一致：内核资源（rule-sets 等）按工作目录解析。
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (!string.IsNullOrEmpty(exeDir)) psi.WorkingDirectory = exeDir;

            using var p = Process.Start(psi);
            if (p == null) return new KernelCheckResult(false, true, "预检进程启动失败");

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!Task.WaitAll(new Task[] { stdout, stderr }, timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                // 杀掉后再等读流结束：否则这两个 ReadToEndAsync 可能以 IOException 结束而无人观察，
                // 变成 TaskScheduler.UnobservedTaskException（本项目踩过同类坑）。
                try { Task.WaitAll(new Task[] { stdout, stderr }, 1000); } catch { }
                return new KernelCheckResult(false, true, $"预检超时（{timeoutMs} ms）");
            }
            try { p.WaitForExit(2000); } catch { }

            var output = ((stderr.Result ?? "") + Environment.NewLine + (stdout.Result ?? "")).Trim();
            return new KernelCheckResult(p.ExitCode == 0, false, output);
        }
        catch (Exception ex)
        {
            return new KernelCheckResult(false, true, $"预检无法执行：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>粗判一份 JSON 是否是某内核的配置（用于「用当前配置预检新内核」前先确认口径一致）。</summary>
    public static bool LooksLikeConfig(string json, KernelKind kind)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            if (JsonNode.Parse(json) is not JsonObject root) return false;
            if (root["outbounds"] is not JsonArray outs || outs.Count == 0) return false;
            if (outs[0] is not JsonObject first) return false;
            return kind == KernelKind.SingBox ? first.ContainsKey("type") : first.ContainsKey("protocol");
        }
        catch { return false; }
    }

    /// <summary>取文本前 N 行（压缩成一行，最长 500 字），用于日志/IncidentLog 的 stderr 摘要。</summary>
    public static string FirstLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(Math.Max(1, count));
        var s = string.Join(" | ", lines).Trim();
        return s.Length > 500 ? s[..500] + "…" : s;
    }

    /// <summary>把内核启动失败的已知特征（unknown field / unknown transport / removed / unsupported）映射成中文可读原因。</summary>
    public static string DescribeStartupFailure(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";
        var s = stderr.ToLowerInvariant();
        var causes = new List<string>();
        if (s.Contains("unknown field", StringComparison.Ordinal))
            causes.Add("配置含当前内核不认识的字段（版本升级后字段可能已移除/改名）");
        if (s.Contains("unknown transport", StringComparison.Ordinal))
            causes.Add("节点传输方式当前内核不支持");
        if (s.Contains("unknown outbound type", StringComparison.Ordinal) || s.Contains("unknown outbound", StringComparison.Ordinal))
            causes.Add("出站类型当前内核不支持");
        if (s.Contains("has been removed", StringComparison.Ordinal) || s.Contains("is removed", StringComparison.Ordinal))
            causes.Add("配置使用了已被内核移除的功能");
        if (s.Contains("unsupported", StringComparison.Ordinal))
            causes.Add("内核不支持该配置项");
        return causes.Count == 0 ? "" : string.Join("；", causes);
    }

    // ---- 自检（无测试框架） ----

    /// <summary>
    /// 单测式自检：解析/适配几条内置样例。返回空串=全部通过；否则返回失败项列表。
    /// 不依赖外部文件、不启动内核进程。
    /// </summary>
    public static string SelfTest()
    {
        var failures = new List<string>();
        void Check(string name, bool ok) { if (!ok) failures.Add(name); }

        var xver = ParseVersion("Xray 26.3.1 (Xray, Penetrates Everything.)", KernelKind.Xray);
        Check("解析 xray 版本", xver != null && xver.Major == 26 && xver.Minor == 3);

        var sver = ParseVersion("sing-box version 1.16.0\nEnvironment: go1.25", KernelKind.SingBox);
        Check("解析 sing-box 版本", sver != null && sver >= SingBoxLegacyDnsRemovedIn);

        const string oldDns = "{\"dns\":{\"rules\":[{\"ip_is_private\":true,\"server\":\"dns-direct\"},{\"rule_set\":[\"geosite-cn\"],\"server\":\"dns-direct\"}],\"final\":\"dns-remote\"}}";
        var adapted = Adapt(oldDns, KernelKind.SingBox, SingBoxLegacyDnsRemovedIn, out var notes);
        Check("1.16 适配：旧 ip_is_private 已被移除", !adapted.Contains("ip_is_private", StringComparison.Ordinal));
        Check("1.16 适配：旧 server 已补 action=route", adapted.Contains("\"action\": \"route\"", StringComparison.Ordinal));
        Check("1.16 适配：产生留痕 note", notes.Count > 0);

        var oldAdapted = Adapt(oldDns, KernelKind.SingBox, new Version(1, 15, 0), out var oldNotes);
        Check("1.15 不迁移", oldAdapted == oldDns && oldNotes.Count == 0);

        const string wsXray = "{\"outbounds\":[{\"protocol\":\"vless\",\"streamSettings\":{\"network\":\"ws\"}}]}";
        Adapt(wsXray, KernelKind.Xray, new Version(26, 0, 0), out var xnotes);
        Check("xray 26 ws 版本注记", xnotes.Count > 0);

        const string sockoptJson = "{\"outbounds\":[{\"streamSettings\":{\"sockopt\":{\"tcpFastOpen\":true}}}]}";
        var stripped = StripOptionalFields(sockoptJson, KernelKind.Xray, out _);
        Check("去性能可选字段", !stripped.Contains("sockopt", StringComparison.Ordinal));

        Check("失败特征映射", DescribeStartupFailure("FATAL unknown field \"foo\"").Length > 0);
        const string sbConfig = "{\"outbounds\":[{\"type\":\"vless\",\"tag\":\"proxy\"}]}";
        Check("配置口径识别", LooksLikeConfig(sbConfig, KernelKind.SingBox));

        return failures.Count == 0 ? "" : "KernelCompat.SelfTest 失败：" + string.Join("；", failures);
    }
}