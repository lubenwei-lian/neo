using Neo.Core.Models;

namespace Neo.Core.Parsing;

public static class ClashYamlParser
{
    public static List<NodeItem> Parse(string yaml)
    {
        var nodes = new List<NodeItem>();
        if (string.IsNullOrWhiteSpace(yaml)) return nodes;

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0 || l[0] == ' ' || l[0] == '\t' || l[0] == '#') continue;
            if (l.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase)) { start = i + 1; break; }
        }
        if (start < 0) return nodes;

        var block = new List<string>();
        for (int i = start; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length > 0 && l[0] != ' ' && l[0] != '\t' && l[0] != '-') break;
            block.Add(l);
        }

        var items = new List<Dictionary<string, object>>();
        Dictionary<string, object>? cur = null;
        int itemIndent = -1;
        var stack = new List<(int Indent, Dictionary<string, object> Dict)>();

        foreach (var raw in block)
        {
            //   "pw2 # 这是注释"（认证必失败）、name 也会被污染。
            var line = StripComment(raw).TrimEnd();
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;
            int indent = line.Length - t.Length;
            bool isItem = t.StartsWith("- ") || t == "-";

            if (isItem && (itemIndent < 0 || indent <= itemIndent))
            {
                itemIndent = indent;
                cur = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                items.Add(cur);
                stack.Clear();
                stack.Add((indent + 2, cur));
                var rest = t.Length > 1 ? t[2..].Trim() : "";
                //   原先被当成 "={name" 这样的怪键，整条节点解析失败 → 订阅「未解析出可用节点」。
                if (rest.Length > 0 && !TrySetFlowMapping(rest, cur))
                    SetKeyVal(rest, indent + 2, stack);
                continue;
            }

            if (cur == null) continue;
            SetKeyVal(t, indent, stack);
        }

        foreach (var item in items)
        {
            var n = ToNode(item);
            if (n != null && n.IsValid) nodes.Add(n);
        }
        return nodes;
    }

    private static void SetKeyVal(string content, int indent, List<(int Indent, Dictionary<string, object> Dict)> stack)
    {
        int colon = -1;
        for (int i = 0; i < content.Length; i++)
            if (content[i] == ':' && (i == content.Length - 1 || content[i + 1] == ' ')) { colon = i; break; }
        if (colon <= 0) return;

        var key = Unquote(content[..colon].Trim());
        var val = content[(colon + 1)..].Trim();
        if (key.Length == 0) return;

        while (stack.Count > 1 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
        var parent = stack[^1].Dict;

        if (val.Length == 0)
        {
            var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            parent[key] = child;
            stack.Add((indent, child));
        }
        else if (val.Length >= 2 && val[0] == '{' && val[^1] == '}')
        {
            // ★ 2026-10-23 第二批：嵌套 flow mapping（xhttp-opts: {path: /x, host: h, mode: stream-one}）
            //   旧实现把整段当字符串存，Map() 永远取不到 ⇒ xhttp-opts / h2-opts / http-opts / ech-opts 全丢。
            var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            FillFlowMapping(val, child);
            parent[key] = child;
        }
        else
        {
            parent[key] = Unquote(val);
        }
    }

    private static string StripComment(string line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inDouble)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inDouble = false;
                continue;
            }
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }
            if (c == '#' && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t'))
                return line[..i];
            if (c == '"' && StartsQuotedValue(line, i)) inDouble = true;
            else if (c == '\'' && StartsQuotedValue(line, i)) inSingle = true;
        }
        return line;
    }

    /// 只有出现在「值起始位置」（行首、": "、", "、"- "、"[", "{" 之后）的引号才当字符串定界符，
    /// 避免把普通文本里的撇号（例如 Bob's）误判成引号起始。
    private static bool StartsQuotedValue(string s, int i)
    {
        for (int k = i - 1; k >= 0; k--)
        {
            var c = s[k];
            if (c == ' ' || c == '\t') continue;
            return c == ':' || c == ',' || c == '-' || c == '[' || c == '{';
        }
        return true;
    }

    private static bool TrySetFlowMapping(string text, Dictionary<string, object> target)
    {
        var t = text.Trim();
        if (t.Length < 2 || t[0] != '{' || t[^1] != '}') return false;
        FillFlowMapping(t, target);
        return true;
    }

    private static void FillFlowMapping(string text, Dictionary<string, object> target)
    {
        var inner = text[1..^1];
        foreach (var seg in SplitFlow(inner, ','))
        {
            var s = seg.Trim();
            if (s.Length == 0) continue;
            var colon = FindFlowColon(s);
            if (colon <= 0) continue;
            var key = Unquote(s[..colon].Trim());
            if (key.Length == 0) continue;
            var valRaw = s[(colon + 1)..].Trim();
            if (valRaw.Length >= 2 && valRaw[0] == '{' && valRaw[^1] == '}')
            {
                var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                FillFlowMapping(valRaw, child);
                target[key] = child;
            }
            else
            {
                target[key] = Unquote(valRaw);
            }
        }
    }

    /// <summary>按分隔符切分 flow mapping，忽略引号内与嵌套 {} / [] 内的分隔符。</summary>
    private static IEnumerable<string> SplitFlow(string s, char sep)
    {
        int start = 0;
        bool inSingle = false, inDouble = false;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inDouble)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inDouble = false;
                continue;
            }
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }
            if (c == '"') { inDouble = true; continue; }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '{' || c == '[') { depth++; continue; }
            if (c == '}' || c == ']') { depth--; continue; }
            if (c == sep && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    /// <summary>找 flow mapping 片段里第一个顶层（引号外、嵌套外）冒号。</summary>
    private static int FindFlowColon(string s)
    {
        bool inSingle = false, inDouble = false;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inDouble)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inDouble = false;
                continue;
            }
            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }
            if (c == '"') { inDouble = true; continue; }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '{' || c == '[') { depth++; continue; }
            if (c == '}' || c == ']') { depth--; continue; }
            if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    private static NodeItem? ToNode(Dictionary<string, object> item)
    {
        var type = Str(item, "type").ToLowerInvariant();
        var node = new NodeItem
        {
            Name = Str(item, "name"),
            // ★ 2026-10-23 IPv6 方括号：Clash 的 server 也可能写成 "[2001:db8::1]"；
            //   解析阶段统一剥掉，xray 与 sing-box 两条路径都受益（Xray 构建器不在本次写入范围）。
            Server = StripBrackets(Str(item, "server")),
            Port = Int(item, "port"),
        };
        if (node.Port <= 0 || node.Server.Length == 0) return null;
        if (node.Name.Length == 0) node.Name = $"{node.Server}:{node.Port}";

        var network = Str(item, "network");
        bool tls = Bool(item, "tls");
        var sni = Str(item, "servername");
        if (sni.Length == 0) sni = Str(item, "sni");

        switch (type)
        {
            case "ss":
                node.Protocol = NodeProtocol.Shadowsocks;
                node.Method = Str(item, "cipher");
                node.Password = Str(item, "password");
                // ★ 2026-10-23 ss-plugin：Clash 的 ss 条目用 plugin / plugin-opts(map) 描述 SIP003；
                //   与分享链接一样落到 NodeItem.Plugin/PluginOpts，由 SingBoxConfigBuilder 写进 outbound。
                node.Plugin = StrOrNull(item, "plugin");
                node.PluginOpts = PluginOptsToString(item);
                break;

            case "vmess":
                node.Protocol = NodeProtocol.VMess;
                node.Uuid = Str(item, "uuid");
                node.AlterId = Int(item, "alterId");
                node.Cipher = Str(item, "cipher");
                ApplyTransport(node, item, network, tls, sni);
                break;

            case "vless":
                node.Protocol = NodeProtocol.VLESS;
                node.Uuid = Str(item, "uuid");
                node.Flow = Str(item, "flow");
                ApplyTransport(node, item, network, tls, sni);
                var ro = Map(item, "reality-opts");
                if (ro != null)
                {
                    node.Security = "reality";
                    node.PublicKey = Str(ro, "public-key");
                    node.ShortId = Str(ro, "short-id");
                    node.Fingerprint = Str(item, "client-fingerprint");
                }
                // ★ 2026-10-23 第二批：Clash Meta 的 vless 后量子加密（部分面板会写 encryption: mlkem... 或 mlkem768x25519plus...）
                var encC = Str(item, "encryption");
                if (encC.Length > 0 && !encC.Equals("none", StringComparison.OrdinalIgnoreCase) && !encC.Equals("false", StringComparison.OrdinalIgnoreCase))
                    node.VlessEncryption = encC;
                break;

            case "trojan":
                node.Protocol = NodeProtocol.Trojan;
                node.Password = Str(item, "password");
                ApplyTransport(node, item, network, tls, sni);
                break;

            case "hysteria2":
            case "hysteria":
                node.Protocol = NodeProtocol.Hysteria2;
                node.Password = Str(item, "password");
                node.Sni = sni;
                Put(item, node, "obfs");
                Put(item, node, "obfs-password");
                Put(item, node, "up");
                Put(item, node, "down");
                if (Bool(item, "skip-cert-verify")) node.AllowInsecure = true;
                break;

            case "socks5":
            case "socks":
                // ★ 2026-10-23 第二批：Clash 的 socks5 条目（整份 YAML 交给内核的原生做法不用，坚持拆成节点）。
                node.Protocol = NodeProtocol.Socks;
                node.Username = StrOrNull(item, "username");
                node.Password = StrOrNull(item, "password") ?? "";
                break;

            case "anytls":
                node.Protocol = NodeProtocol.AnyTls;
                node.Password = StrOrNull(item, "password") ?? "";
                node.Security = "tls";
                node.Sni = sni;
                if (Bool(item, "skip-cert-verify")) node.AllowInsecure = true;
                if (Str(item, "client-fingerprint") is { Length: > 0 } anyFp) node.Fingerprint = anyFp;
                break;

            case "wireguard":
                // Clash.Meta 的 WireGuard 条目 → 生成器的 endpoints 形态。
                node.Protocol = NodeProtocol.WireGuard;
                node.Password = StrOrNull(item, "private-key") ?? "";
                node.WgPublicKey = StrOrNull(item, "public-key");
                node.WgPresharedKey = StrOrNull(item, "pre-shared-key") ?? StrOrNull(item, "preshared-key");
                node.WgAddress = SplitList(Str(item, "ip"));
                node.WgReserved = StrOrNull(item, "reserved");
                if (int.TryParse(Str(item, "mtu"), out var wgMtu) && wgMtu > 0) node.WgMtu = wgMtu;
                node.Network = "udp";
                break;

            case "tuic":
                node.Protocol = NodeProtocol.TUIC;
                node.Uuid = Str(item, "uuid");
                node.Password = Str(item, "password");
                node.Sni = sni;
                if (Bool(item, "skip-cert-verify")) node.AllowInsecure = true;
                break;

            default:
                return null;
        }
        return node;
    }

    private static void ApplyTransport(NodeItem node, Dictionary<string, object> item, string network, bool tls, string sni)
    {
        node.Network = network.Length == 0 ? "tcp" : network;
        node.Sni = sni;
        if (tls) node.Security = "tls";

        var ws = Map(item, "ws-opts");
        if (ws != null)
        {
            node.Path = Str(ws, "path");
            var h = Map(ws, "headers");
            if (h != null) node.HostHeader = Str(h, "host");
        }

        var grpc = Map(item, "grpc-opts");
        if (grpc != null) node.ServiceName = Str(grpc, "grpc-service-name");

        // ★ 2026-10-23 第二批：更多 Clash 传输/安全扩展（此前只有 ws/grpc，其余项全丢）：
        //   · h2-opts（network: h2）→ network=h2 + host/path（xray 26 已移除 h2，自动改走 sing-box）
        //   · http-opts（network: http）→ host/path
        //   · xhttp-opts（network: xhttp）→ path/host/mode（mihomo 的新传输，xray 支持）
        //   · ech-opts（enable/config）→ TLS ECH 配置列表
        var h2 = Map(item, "h2-opts");
        if (h2 != null)
        {
            node.HostHeader = SplitList(Str(h2, "host"));
            node.Path = Str(h2, "path");
        }

        var http = Map(item, "http-opts");
        if (http != null)
        {
            node.Path = Str(http, "path");
            var hh = Map(http, "headers");
            if (hh != null)
            {
                var hv = SplitList(Str(hh, "Host"));
                if (hv.Length > 0) node.HostHeader = hv;
            }
        }

        var xhttp = Map(item, "xhttp-opts");
        if (xhttp != null)
        {
            node.Path = string.IsNullOrEmpty(Str(xhttp, "path")) ? node.Path : Str(xhttp, "path");
            var xh = Str(xhttp, "host");
            if (xh.Length > 0) node.HostHeader = xh;
            var xm = Str(xhttp, "mode");
            if (xm.Length > 0) node.XhttpMode = xm;
        }

        var ech = Map(item, "ech-opts");
        if (ech != null && (Str(ech, "enable").Equals("true", StringComparison.OrdinalIgnoreCase) || Bool(item, "ech")))
        {
            var cfg = Str(ech, "config");
            if (cfg.Length > 0) node.EchConfigList = cfg;
        }

        var cf = Str(item, "client-fingerprint");
        if (cf.Length > 0 && string.IsNullOrEmpty(node.Fingerprint)) node.Fingerprint = cf;
        if (Bool(item, "skip-cert-verify")) node.AllowInsecure = true;

        // 传输名归一化：Clash 的 "tcp" 在 neo/xray 里叫 tcp（一致）；"h2" 保留，让生成器决定内核。
        node.Network = node.Network.ToLowerInvariant();
    }

    /// <summary>取列表式标量（"[a.com, b.com]" / "a.com"）的第一个值，并去掉方括号/引号。</summary>
    private static string SplitList(string s)
    {
        var t = s.Trim().Trim('[', ']').Trim();
        if (t.Length == 0) return "";
        var first = t.Split(',')[0].Trim().Trim('"', '\'');
        return first;
    }


    /// <summary>把 Clash 的 plugin-opts map 拼成 SIP003 的 "k=v;k=v" 串；也兼容字符串写法。</summary>
    private static string? PluginOptsToString(Dictionary<string, object> item)
    {
        var direct = StrOrNull(item, "plugin-opts") ?? StrOrNull(item, "plugin_opts");
        if (direct != null) return direct;
        var map = Map(item, "plugin-opts") ?? Map(item, "plugin_opts");
        if (map == null) return null;
        var parts = new List<string>();
        foreach (var kv in map)
        {
            if (kv.Value is string s && s.Length > 0) parts.Add($"{kv.Key}={s}");
        }
        return parts.Count > 0 ? string.Join(";", parts) : null;
    }

    /// <summary>去掉 IPv6 字面量两侧的方括号。</summary>
    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static string Str(Dictionary<string, object> m, string k) =>
        m.TryGetValue(k, out var v) && v is string s ? s : "";

    private static string? StrOrNull(Dictionary<string, object>? m, string k) =>
        m != null && m.TryGetValue(k, out var v) && v is string s && s.Length > 0 ? s : null;

    private static int Int(Dictionary<string, object> m, string k) =>
        int.TryParse(Str(m, k), out var i) ? i : 0;

    private static bool Bool(Dictionary<string, object> m, string k) =>
        Str(m, k).Equals("true", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object>? Map(Dictionary<string, object> m, string k) =>
        m.TryGetValue(k, out var v) && v is Dictionary<string, object> d ? d : null;

    private static void Put(Dictionary<string, object> m, NodeItem n, string k)
    {
        var s = Str(m, k);
        if (s.Length > 0) n.Extras[k] = s;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1].Replace("\\\"", "\"").Replace("\\'", "'");
        // YAML 单引号的转义写法是连写两个单引号（''→'）。
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s[1..^1].Replace("''", "'");
        return s;
    }
}
