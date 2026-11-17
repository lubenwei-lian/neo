namespace Neo.Core.Parsing;

/// <summary>Only HTTP(S) subscriptions and explicitly chosen absolute local files are accepted.</summary>
public static class SubscriptionSource
{
    public const long MaxImportBytes = 16 * 1024 * 1024;
    public static string Normalize(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Host.Length == 0) throw new ArgumentException("订阅地址缺少服务器名");
            return uri.AbsoluteUri;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile) value = uri.LocalPath;
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("请输入 HTTP/HTTPS 订阅地址或完整文件路径");
        var path = Path.GetFullPath(value);
        if (!File.Exists(path)) throw new FileNotFoundException("订阅文件不存在", path);
        if (new FileInfo(path).Length > MaxImportBytes) throw new ArgumentException("订阅文件不能超过16 MB");
        return path;
    }

    public static async Task<string> ReadLocalAsync(string source, CancellationToken ct = default)
    {
        var path = Normalize(source);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择本地订阅文件");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxImportBytes) throw new ArgumentException("订阅文件不能超过16 MB");
        using var bytes = new MemoryStream((int)Math.Min(file.Length, MaxImportBytes));
        var buffer = new byte[65536];
        int read;
        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + read > MaxImportBytes) throw new ArgumentException("订阅文件不能超过16 MB");
            bytes.Write(buffer, 0, read);
        }
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
