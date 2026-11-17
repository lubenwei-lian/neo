using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Neo.Core.Core;

/// <summary>只读 TestRoute；PublishResult=false，不订阅流量、不添加/删除任何路由。
/// Wire contract: XTLS/Xray-core app/router/command/command.proto (RoutingContext 1/2/5/6/7/8/12).
/// </summary>
public static class XrayRouteClient
{
    public sealed record Result(string? Outbound, bool UsesDefault, string Detail);
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
    { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 64 * 1024 };

    public static async Task<Result> QueryAsync(int port, string inbound, Uri target, int sourcePort,
        CancellationToken ct = default, string protocol = "tls")
    {
        var payload = Encode(inbound, target.DnsSafeHost, target.Port, sourcePort, protocol);
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame, 5);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}/xray.app.router.command.RoutingService/TestRoute")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(frame) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-timeout", "2S");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("路由 API 不是 gRPC 响应");
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string Header(string key)
        {
            if (response.TrailingHeaders.TryGetValues(key, out var values) || response.Headers.TryGetValues(key, out values))
                return string.Join("", values);
            return "";
        }
        var status = Header("grpc-status");
        var message = Uri.UnescapeDataString(Header("grpc-message"));
        // PickRoute explicitly returning ErrNoClue is the only proof that dispatch will use its configured default.
        var usesDefault = status == "2" && (message.Equals("not enough information for making a decision", StringComparison.Ordinal)
            || message.EndsWith(": not enough information for making a decision", StringComparison.Ordinal));
        if (status != "0") return new(null, usesDefault,
            usesDefault ? "当前规则无匹配，使用运行配置默认出口" : $"路由 API 状态 {status}（未确认出口）：{message[..Math.Min(message.Length, 160)]}");
        if (body.Length < 5 || body[0] != 0 || BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4)) != body.Length - 5)
            throw new InvalidDataException("路由 API 帧不完整");
        return new(DecodeOutbound(body.AsSpan(5)), false,
            $"当前运行内核 TestRoute 规则判定（域名 / TCP / {target.Port} / {(protocol == "tls" ? "TLS 嗅探" : "未启用 TLS 嗅探")} / 入站 {inbound} / 源端口 {sourcePort}）");
    }

    public static byte[] Encode(string inbound, string host, int port, int sourcePort, string protocol = "tls")
    {
        if (port is < 1 or > 65535 || sourcePort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var context = new List<byte>();
        void Number(int key, ulong value) { Varint(context, (ulong)key); Varint(context, value); }
        void Text(int key, string value) { var bytes = Encoding.UTF8.GetBytes(value); Number(key, (ulong)bytes.Length); context.AddRange(bytes); }
        Text(10, inbound); Number(16, 2); // Network.TCP = 2
        Number(26, 4); context.AddRange(new byte[] { 127, 0, 0, 1 }); // SourceIPs
        Number(40, (ulong)sourcePort); Number(48, (ulong)port); Text(58, host);
        if (!string.IsNullOrEmpty(protocol)) Text(66, protocol);
        var result = new List<byte> { 10 }; Varint(result, (ulong)context.Count); result.AddRange(context);
        return result.ToArray(); // FieldSelectors omitted; PublishResult defaults false.
    }

    public static string? DecodeOutbound(ReadOnlySpan<byte> data)
    {
        if (data.Length > 64 * 1024) throw new InvalidDataException("路由响应超限");
        var offset = 0; string? outbound = null;
        while (offset < data.Length)
        {
            var key = ReadVarint(data, ref offset);
            if (key >> 3 == 0) throw new InvalidDataException("无效 protobuf 字段");
            switch (key & 7)
            {
                case 0: ReadVarint(data, ref offset); break;
                case 1: Advance(data, ref offset, 8); break;
                case 2:
                    var size = ReadVarint(data, ref offset);
                    if (size > (ulong)(data.Length - offset)) throw new InvalidDataException("截断的路由字段");
                    if (key == 98) outbound = new UTF8Encoding(false, true).GetString(data.Slice(offset, (int)size));
                    offset += (int)size; break;
                case 5: Advance(data, ref offset, 4); break;
                default: throw new InvalidDataException("不支持的 protobuf wire type");
            }
        }
        return outbound;
    }
    private static void Varint(List<byte> output, ulong value)
    { while (value >= 128) { output.Add((byte)(value | 128)); value >>= 7; } output.Add((byte)value); }
    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("截断的 protobuf varint");
            var b = data[offset++];
            if (shift == 63 && b > 1) throw new InvalidDataException("varint 溢出");
            value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException("无效 varint");
    }
    private static void Advance(ReadOnlySpan<byte> data, ref int offset, int size)
    { if (size > data.Length - offset) throw new InvalidDataException("截断的固定字段"); offset += size; }
}
