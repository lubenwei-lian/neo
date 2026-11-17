using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Neo.Core.Core;

/// <summary>
/// 本机 Xray StatsService 的只读 unary gRPC 客户端（空 pattern，reset=false）。
/// 协议来源：XTLS/Xray-core app/stats/command/command.proto 与 gRPC HTTP/2 framing。
/// 仅实现这两个稳定的 protobuf 消息，不引入代码生成或第三方网络依赖。
/// </summary>
public static class XrayStatsClient
{
    public readonly record struct Counter(string Name, long Value);
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    public static async Task<IReadOnlyList<Counter>> QueryAsync(int port, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port}/xray.app.stats.command.StatsService/QueryStats")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            // 五字节 gRPC 头 + 空 protobuf 请求：pattern=""、reset=false；绝不重置计数器。
            Content = new ByteArrayContent(new byte[5]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-timeout", "3S");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("Stats API did not return gRPC content");
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!response.TrailingHeaders.TryGetValues("grpc-status", out var status)
            && !response.Headers.TryGetValues("grpc-status", out status))
            throw new InvalidDataException("Stats API omitted grpc-status");
        if (status.Any(s => s != "0")) throw new InvalidDataException("Stats API returned a nonzero grpc-status");
        if (body.Length < 5 || body.Length > MaxResponseBytes || body[0] != 0)
            throw new InvalidDataException("Unsupported gRPC frame");
        var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4));
        if (length != body.Length - 5) throw new InvalidDataException("Invalid gRPC frame length");
        return Decode(body.AsSpan(5));
    }

    /// <summary>QueryStatsResponse.stat=1；Stat.name=1、Stat.value=2，未知字段安全跳过。</summary>
    public static IReadOnlyList<Counter> Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxResponseBytes) throw new InvalidDataException("Stats payload exceeds limit");
        var counters = new List<Counter>(); var offset = 0;
        while (offset < payload.Length)
        {
            var key = Varint(payload, ref offset);
            if (key == 10)
            {
                var stat = Bytes(payload, ref offset); var pos = 0; var name = ""; long value = 0;
                while (pos < stat.Length)
                {
                    var field = Varint(stat, ref pos);
                    if (field == 10) name = Utf8.GetString(Bytes(stat, ref pos));
                    else if (field == 16) value = unchecked((long)Varint(stat, ref pos));
                    else Skip(stat, ref pos, field);
                }
                if (counters.Count >= 100_000) throw new InvalidDataException("Too many stats counters");
                counters.Add(new Counter(name, value));
            }
            else Skip(payload, ref offset, key);
        }
        return counters;
    }

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = Varint(data, ref offset);
        if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("Truncated protobuf field");
        var bytes = data.Slice(offset, (int)length); offset += (int)length; return bytes;
    }
    private static ulong Varint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Truncated protobuf varint");
            var b = data[offset++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Overflowing protobuf varint");
            result |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return result;
        }
        throw new InvalidDataException("Invalid protobuf varint");
    }
    private static void Skip(ReadOnlySpan<byte> data, ref int offset, ulong key)
    {
        if (key >> 3 == 0) throw new InvalidDataException("Invalid protobuf field number");
        switch (key & 7)
        {
            case 0: Varint(data, ref offset); break;
            case 1: Advance(data, ref offset, 8); break;
            case 2: Bytes(data, ref offset); break;
            case 5: Advance(data, ref offset, 4); break;
            default: throw new InvalidDataException("Unsupported protobuf wire type");
        }
    }
    private static void Advance(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count > data.Length - offset) throw new InvalidDataException("Truncated protobuf fixed field");
        offset += count;
    }
}
