using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Neo.App.Services;

/// 为什么需要：
///  · 更新内核 = 往 Program Files 里覆盖一个可执行文件，下载源只是 GitHub/TLS 保护；
///    代码里原先没有任何哈希/签名校验（只有体积和版本号字符串）；
///  · 覆盖用的是 File.Copy 直接写目标 —— 中断/断电会留下半个 exe，下次启动才报「内核启动失败」；
///    xray.exe.bak-prev 停了一个月）。
/// 本类把这三件事收口：SHA-256 校验、先写 .new 再原子替换、启动时清理/恢复。
public static class KernelInstall
{
    /// <summary>从任意文本里解析 SHA-256（兼容 Xray .dgst、常见 .sha256 与裸 hex）。</summary>
    public static bool TryParseSha256(string? text, out string hex)
    {
        hex = "";
        var s = text ?? "";
        // Xray .dgst:  "SHA2-256= abcd..."   / 常见: "SHA256: abcd" / "abcd  file.zip"
        var m = Regex.Match(s, @"(?i)(?:sha2?-?256|sha-?256)\s*[=:]\s*([0-9a-f]{64})");
        if (!m.Success) m = Regex.Match(s, @"\b([0-9a-fA-F]{64})\b");
        if (!m.Success) return false;
        hex = m.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    public static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>校验字节流哈希；不匹配时返回 false 并带出实际值，供日志/弹窗使用。</summary>
    public static bool VerifySha256(byte[] data, string expectedHex, out string actualHex)
    {
        actualHex = Sha256Hex(data);
        return string.Equals(actualHex, (expectedHex ?? "").Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// 原子替换内核文件。
    /// 先在同目录写 target.new（写完再校验版本），再用 File.Replace 把旧文件挪到 backup、新文件顶上 ——
    /// 不再直接覆盖正在使用的目标，避免「写一半断电 = 内核损坏」。
    public static void ReplaceAtomically(string newFile, string target, string backup)
    {
        var tmp = target + ".new";
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            File.Copy(newFile, tmp, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, target, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// 启动自愈：
    ///  ① 清掉上次安装中断留下的 *.new（它永远不是合法内核）；
    ///  ② 若目标内核不存在但 .bak-prev 在（安装恰好死在替换前后），用备份恢复，避免「内核没了」。
    public static void RecoverIfNeeded()
    {
        foreach (var name in new[] { "xray.exe", "sing-box.exe" })
        {
            try
            {
                var target = Path.Combine(Neo.Core.NeoPaths.RuntimeDir, name);
                var tmp = target + ".new";
                var backup = target + ".bak-prev";

                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                    LogBus.App($"已清理上次内核安装未完成的临时文件：{name}.new", "Warning");
                }

                if (!File.Exists(target) && File.Exists(backup))
                {
                    File.Copy(backup, target, true);
                    LogBus.App($"检测到内核文件缺失，已用备份恢复：{name}", "Warning");
                }
            }
            catch (Exception ex)
            {
                LogBus.App($"内核文件自愈失败（{name}）：{ex.GetType().Name} {ex.Message}", "Warning");
            }
        }
    }
}
