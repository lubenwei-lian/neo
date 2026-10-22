using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Core;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;

namespace Neo.App.Services;

/// <summary>
/// Cloudflare WARP 一键注册（对标成熟客户端的「WARP 配置」功能）：
///   生成 X25519 密钥对 → 调 api.cloudflareclient.com 注册免费 WARP 账号 →
///   用注册返回的 peer 公钥 / endpoint / client_id(reserved) 拼成 wireguard:// 链接，
///   再交给现有的手动节点通道（AppState.AddManualLinks）。
/// 说明：WARP 是 Cloudflare 的免费公共出口，速度/可用性受地区与策略影响，
/// 仅作为「没有自建 WireGuard 服务端时」的补充，不替代自建节点。
/// </summary>
public static class WarpService
{
    private const string RegUrl = "https://api.cloudflareclient.com/v0a2158/reg";

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var priv = new byte[32];
        X25519.GeneratePrivateKey(new SecureRandom(), priv);
        var pub = new byte[32];
        X25519.ScalarMultBase(priv, 0, pub, 0);
        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
    }

    /// <summary>注册并返回可直接导入的 wireguard:// 分享链接。</summary>
    public static async Task<(bool Ok, string Message, string? Link)> RegisterAsync(CancellationToken ct = default)
    {
        try
        {
            var (priv, pub) = GenerateKeyPair();
            var body = JsonSerializer.Serialize(new
            {
                key = pub,
                install_id = "",
                fcm_token = "",
                tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                model = "PC",
                serial_number = "",
                locale = "en-US",
                type = "a",
            });

            var text = await PostViaBestRouteAsync(body, ct);
            if (string.IsNullOrWhiteSpace(text))
                return (false, "注册失败：无法连接 Cloudflare API（可先连接一个可用节点再试）", null);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("config", out var cfg))
                return (false, "注册失败：响应里没有 config（可能被限流/接口变更）", null);
            var clientId = cfg.TryGetProperty("client_id", out var cid) ? cid.GetString() ?? "" : "";
            var iface = cfg.GetProperty("interface");
            var v4 = iface.GetProperty("addresses").GetProperty("v4").GetString() ?? "172.16.0.2";
            var peer = cfg.GetProperty("peers")[0];
            var peerPub = peer.GetProperty("public_key").GetString() ?? "";
            var endpoint = peer.GetProperty("endpoint");
            var host = endpoint.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
            if (host.Length == 0 && endpoint.TryGetProperty("v4", out var v4e))
                host = v4e.GetString() ?? "";
            if (host.Length == 0 || peerPub.Length == 0 || clientId.Length == 0)
                return (false, "注册失败：响应缺少必要字段", null);

            // client_id 是 3 字节 base64 → reserved（WARP 握手需要）
            var reserved = clientId;
            var link =
                "wireguard://" + Uri.EscapeDataString(priv) + "@" + host +
                "?publickey=" + Uri.EscapeDataString(peerPub) +
                "&address=" + Uri.EscapeDataString(v4 + "/32") +
                "&mtu=1280&keepalive=25&dns=1.1.1.1" +
                "&reserved=" + Uri.EscapeDataString(reserved) +
                "#WARP（Cloudflare 免费出口）";
            LogBus.App($"WARP 注册成功：endpoint={host}，address={v4}/32，reserved={reserved}", "Info");
            return (true, $"WARP 节点已生成（{host} · {v4}/32）", link);
        }
        catch (Exception ex)
        {
            LogBus.App($"WARP 注册异常：{ex.GetType().Name} {ex.Message}", "Warning");
            return (false, "注册异常：" + ex.Message, null);
        }
    }

    /// <summary>优先走本地代理（注册接口在国内常被墙），失败再直连重试一次。</summary>
    private static async Task<string?> PostViaBestRouteAsync(string body, CancellationToken ct)
    {
        var payload = new StringContent(body, Encoding.UTF8, "application/json");
        var attempts = new List<IWebProxy?>();
        if (ConnectionService.IsConnected)
            attempts.Add(new WebProxy($"socks5://127.0.0.1:{LocalPorts.Socks}"));
        attempts.Add(null);   // 直连兜底
        foreach (var proxy in attempts)
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    UseProxy = proxy != null,
                    Proxy = proxy,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "okhttp/3.12.1");
                client.DefaultRequestHeaders.TryAddWithoutValidation("CF-Client-Version", "a-6.10-2158");
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(RegUrl, content, ct);
                if (!resp.IsSuccessStatusCode) continue;
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch { }
        }
        return null;
    }

    // ================= X25519（BouncyCastle，RFC 7748） =================
    // 自己用 BigInteger 写的 Montgomery ladder 对照 RFC 测试向量不通过（t1/t2 都不匹配），
    // 这类密码学实现不该手搓：改用 BouncyCastle 的 X25519（成熟实现，有测试向量覆盖）。

    /// <summary>测试入口：任意标量/点（对照 RFC 7748 测试向量）。</summary>
    public static byte[] X25519ForTest(byte[] scalar, byte[] uPoint)
    {
        var r = new byte[32];
        X25519.ScalarMult(scalar, 0, uPoint, 0, r, 0);
        return r;
    }

    /// <summary>X25519 基点乘法（对照 RFC 7748 §6.1 向量）。</summary>
    public static byte[] X25519Base(byte[] scalar)
    {
        var r = new byte[32];
        X25519.ScalarMultBase(scalar, 0, r, 0);
        return r;
    }
}

