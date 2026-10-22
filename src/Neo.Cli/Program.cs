using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Neo.Core.Parsing;

namespace Neo.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "parse":
                    if (args.Length < 2) { Console.WriteLine("用法: neo-cli parse <分享链接>"); return 1; }
                    return CmdParse(args[1]);

                case "fetch":
                    if (args.Length < 2) { Console.WriteLine("用法: neo-cli fetch <订阅URL|文件> [--delay]"); return 1; }
                    return await CmdFetch(args[1], args.Contains("--delay"));

                case "config":
                    if (args.Length < 3) { Console.WriteLine("用法: neo-cli config <订阅URL|文件> <节点序号> [--socks 端口] [--http 端口]"); return 1; }
                    return await CmdConfig(args[1], int.Parse(args[2]));

                case "test":
                    if (args.Length < 2) { Console.WriteLine("用法: neo-cli test <订阅URL|文件> [--index N]"); return 1; }
                    return await CmdTest(args[1], GetOption(args, "--index", 0));

                case "sbconfig":
                    if (args.Length < 3) { Console.WriteLine("用法: neo-cli sbconfig <订阅URL|文件> <节点序号> [--tun]"); return 1; }
                    return await CmdSbConfig(args[1], int.Parse(args[2]), args.Contains("--tun"));

                default:
                    Console.WriteLine("未知命令: " + cmd);
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[异常] " + ex.Message);
            return 9;
        }
    }

    private static int CmdParse(string link)
    {
        var node = ShareLinkParser.Parse(link);
        if (node == null)
        {
            Console.WriteLine("解析失败：无法识别的链接（或格式不支持）");
            return 2;
        }
        PrintNode(node, 0);
        return 0;
    }

    private static async Task<int> CmdSbConfig(string source, int index, bool tun)
    {
        var r = await SubscriptionFetcher.FetchAsync(source);
        if (!r.Success) { Console.WriteLine("[失败] " + r.Error); return 2; }
        if (index < 0 || index >= r.Nodes.Count) { Console.WriteLine($"序号越界（0~{r.Nodes.Count - 1}）"); return 2; }

        var node = r.Nodes[index];
        Console.Error.WriteLine($"节点: {node}");
        var json = tun
            ? SingBoxConfigBuilder.BuildTun(node, LocalPorts.Socks, LocalPorts.Http,
                "rule", true, true, true, false, null, "warning", "223.5.5.5", true,
                new System.Collections.Generic.List<string> { "TslGame.exe", "cs2.exe" })
            : SingBoxConfigBuilder.Build(node, LocalPorts.Socks, LocalPorts.Http,
                "rule", true, true, true, false, null, "warning", "223.5.5.5");
        Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> CmdFetch(string source, bool withDelay)
    {
        Console.WriteLine($"[1/2] 拉取订阅: {source}");
        var r = await SubscriptionFetcher.FetchAsync(source);
        if (!r.Success)
        {
            Console.WriteLine("[失败] " + r.Error);
            return 3;
        }
        Console.WriteLine($"      成功: {r.Nodes.Count} 个节点" + (r.TrafficInfo != null ? $"  |  {r.TrafficInfo}" : ""));

        if (withDelay)
        {
            Console.WriteLine("[2/2] 真连延迟测试（每节点临时内核，较慢）...");
            var sw = Stopwatch.StartNew();
            foreach (var n in r.Nodes)
                n.RealDelay = await RealDelayTester.RealPingAsync(n, NeoPaths.RuntimeDir);
            sw.Stop();
            Console.WriteLine($"      完成，用时 {sw.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            Console.WriteLine("[2/2] 跳过测速（--delay 启用）");
        }

        Console.WriteLine();
        Console.WriteLine("序号   延迟      节点");
        Console.WriteLine("----- --------- ----------------------------------------");
        int i = 0;
        foreach (var n in r.Nodes)
        {
            var delay = n.RealDelay > 0 ? $"{n.RealDelay,5} ms" : "     --";
            Console.WriteLine($"[{i,3}] {delay}  {n.Name}   ({n.Protocol} · {n.Server}:{n.Port})");
            i++;
        }
        return 0;
    }

    private static async Task<int> TcpProbeAsync(string host, int port, int timeoutMs = 3000)
    {
        if (string.IsNullOrEmpty(host) || port <= 0) return -1;
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new System.Net.Sockets.TcpClient { NoDelay = true };
            using var cts = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<int> CmdConfig(string source, int index)
    {
        var r = await SubscriptionFetcher.FetchAsync(source);
        if (!r.Success) { Console.WriteLine("[失败] " + r.Error); return 3; }
        if (index < 0 || index >= r.Nodes.Count) { Console.WriteLine($"[失败] 序号越界（共 {r.Nodes.Count} 个节点）"); return 4; }

        var node = r.Nodes[index];
        Console.WriteLine($"节点: {node}");
        var json = XrayConfigBuilder.Build(node);
        Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> CmdTest(string source, int index)
    {
        Console.WriteLine($"[1/5] 拉取订阅: {source}");
        var r = await SubscriptionFetcher.FetchAsync(source);
        if (!r.Success) { Console.WriteLine("[失败] " + r.Error); return 3; }
        if (index < 0 || index >= r.Nodes.Count) { Console.WriteLine($"[失败] 序号越界（共 {r.Nodes.Count} 个节点）"); return 4; }

        var node = r.Nodes[index];
        Console.WriteLine($"[2/5] 节点: [{index}] {node}");
        var tcpDelay = await TcpProbeAsync(node.Server, node.Port);
        Console.WriteLine($"      TCP 预检: {(tcpDelay >= 0 ? tcpDelay + " ms" : "超时/不达（继续尝试）")}");

        var socksPort = LocalPorts.Socks;
        var httpPort = LocalPorts.Http;
        bool useSingBox = node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC;
        var cfgPath = System.IO.Path.Combine(NeoPaths.DataDir, useSingBox ? "singbox-test-config.json" : "xray-test-config.json");
        var json = useSingBox
            ? SingBoxConfigBuilder.Build(node, socksPort, httpPort, "rule", true, true, true, false, null, "warning", "223.5.5.5")
            : XrayConfigBuilder.Build(node, socksPort, httpPort);
        await System.IO.File.WriteAllTextAsync(cfgPath, json);
        Console.WriteLine($"[3/5] 配置已生成: {cfgPath}");

        var xrayExe = System.IO.Path.Combine(NeoPaths.RuntimeDir, useSingBox ? "sing-box.exe" : "xray.exe");
        Console.WriteLine($"[4/5] 启动内核: {xrayExe}");
        using var core = new CoreProcess(xrayExe, NeoPaths.RuntimeDir);
        core.LogLine += line => Console.WriteLine("      [xray] " + line);
        core.Start(cfgPath);
        Console.WriteLine($"      xray 已启动 PID={core.Pid}，等待入站就绪...");

        bool ready = false;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(400);
            if (!core.IsRunning)
            {
                Console.WriteLine("[失败] xray 启动后立即退出（见上方日志）");
                return 5;
            }
            if (await TcpProbeAsync("127.0.0.1", httpPort, 400) >= 0)
            {
                ready = true;
                break;
            }
        }
        if (!ready) { Console.WriteLine("[失败] 入站端口未就绪"); core.Stop(); return 6; }
        Console.WriteLine("      入站已就绪");

        Console.WriteLine($"[5/5] 连通性测试: http://127.0.0.1:{httpPort} → https://www.gstatic.com/generate_204");
        int exit;
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{httpPort}"),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var sw = Stopwatch.StartNew();
            using var resp = await client.GetAsync("https://www.gstatic.com/generate_204");
            sw.Stop();
            int code = (int)resp.StatusCode;
            bool ok = code is 204 or 200;
            Console.WriteLine(ok
                ? $"      [OK] HTTP {code} · {sw.ElapsedMilliseconds} ms"
                : $"      [警告] HTTP {code}（非预期状态码）· {sw.ElapsedMilliseconds} ms");
            exit = ok ? 0 : 7;
        }
        catch (Exception ex)
        {
            Console.WriteLine("      [失败] 代理链路不通: " + ex.Message);
            exit = 8;
        }

        core.Stop();
        Console.WriteLine("      内核已停止");
        Console.WriteLine(exit == 0 ? "== 端到端测试通过 ==" : "== 端到端测试未通过 ==");
        return exit;
    }

    private static int GetOption(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                return v;
        }
        return fallback;
    }

    private static void PrintNode(NodeItem node, int index)
    {
        Console.WriteLine("名称    : " + node.Name);
        Console.WriteLine("协议    : " + node.Protocol);
        Console.WriteLine("地址    : " + node.EndPoint);
        Console.WriteLine("传输    : " + node.Network + " / 安全层: " + node.Security);
        if (node.Uuid != null) Console.WriteLine("UUID    : " + node.Uuid);
        if (node.Password != null) Console.WriteLine("密码    : " + node.Password);
        if (node.Method != null) Console.WriteLine("加密方式: " + node.Method);
        if (node.Cipher != null) Console.WriteLine("Cipher  : " + node.Cipher);
        if (node.Flow != null) Console.WriteLine("Flow    : " + node.Flow);
        if (node.Sni != null) Console.WriteLine("SNI     : " + node.Sni);
        if (node.Fingerprint != null) Console.WriteLine("指纹    : " + node.Fingerprint);
        if (node.PublicKey != null) Console.WriteLine("Reality公钥: " + node.PublicKey);
        if (node.ShortId != null) Console.WriteLine("Reality短ID: " + node.ShortId);
        if (node.Path != null) Console.WriteLine("Path    : " + node.Path);
        if (node.HostHeader != null) Console.WriteLine("Host    : " + node.HostHeader);
        if (node.ServiceName != null) Console.WriteLine("ServiceName: " + node.ServiceName);
        foreach (var kv in node.Extras)
            Console.WriteLine("扩展    : " + kv.Key + " = " + kv.Value);
        Console.WriteLine("有效    : " + (node.IsValid ? "是" : "否（缺地址/端口）"));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("neo-cli — M1 内核层测试工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  neo-cli parse <分享链接>              解析单条节点分享链接");
        Console.WriteLine("  neo-cli fetch <订阅URL|文件> [--delay]  拉取订阅并列出全部节点");
        Console.WriteLine("  neo-cli config <订阅URL|文件> <序号>   生成 xray 配置并输出");
        Console.WriteLine("  neo-cli test <订阅URL|文件> [--index N] 端到端：拉订阅→启动内核→连通性测试");
    }
}
