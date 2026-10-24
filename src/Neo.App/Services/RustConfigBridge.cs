using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

/// <summary>
/// M2 阶段 2「CLI 旁路」：设置开启后，sing-box 会话的配置改由 Rust 版 neo-cli 生成。
///
/// 安全边界（缺一不可）：
///   1. 只处理 sing-box 且非 naive/自定义/mihomo 的会话（由调用方判断）；
///   2. neo-cli 缺失 / 超时 12 s / 退出码非 0 / 输出为空 / sing-box check 不通过 → 返回 null，
///      调用方回退到 C# 原配置；
///   3. 生成读的是当前 AppState 与节点，不写任何用户数据（临时文件用完即删）。
/// </summary>
public static class RustConfigBridge
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>成功返回 Rust 生成的 sing-box 配置 JSON；任何失败返回 null（并写一条 Warning 说明原因）。</summary>
    public static async Task<string?> TryBuildAsync(NodeItem node, bool tun)
    {
        string inputPath = "";
        string outputPath = "";
        try
        {
            var cli = Path.Combine(NeoPaths.RuntimeDir, "neo-cli.exe");
            if (!File.Exists(cli))
            {
                LogBus.App("Rust 配置生成：未找到 app\\runtime\\neo-cli.exe，回退 C# 配置", "Warning");
                return null;
            }

            var state = AppState.State;
            var dir = Path.Combine(NeoPaths.DataDir, "rustconfig");
            Directory.CreateDirectory(dir);
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}";
            inputPath = Path.Combine(dir, $"input-{stamp}.json");
            outputPath = Path.Combine(dir, $"config-{stamp}.json");

            var options = new JsonObject
            {
                ["tun"] = tun,
                ["mixedPort"] = LocalPorts.CoreSocks,
                ["httpPort"] = LocalPorts.CoreHttp,
                ["inboundsMode"] = "socksHttp",
                ["allowLan"] = state.AllowLan,
                ["logLevel"] = state.KernelLogLevel,
                ["proxyMode"] = state.ProxyMode,
                ["bypassPrivate"] = state.BypassPrivate,
                ["bypassCn"] = state.BypassCn,
                ["blockAds"] = state.BlockAds,
                ["clashApiPort"] = SingBoxConfigBuilder.ClashApiPortOverride,
                ["clashApiSecret"] = SingBoxConfigBuilder.ClashApiSecret,
                ["dnsServer"] = state.DnsServer,
                ["ruleSetDir"] = NeoPaths.RuntimeDir,
                ["emitGroups"] = false,
                ["gameDirectProcesses"] = state.GameDirectEnabled
                    ? new JsonArray(AppState.GameDirectProcessList().Select(p => (JsonNode?)p).ToArray())
                    : new JsonArray(),
            };
            var root = new JsonObject { ["node"] = ConfigShadow.NodeToJson(node), ["options"] = options };
            await File.WriteAllTextAsync(inputPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("shadow");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);

            using var process = Process.Start(psi);
            if (process == null)
            {
                LogBus.App("Rust 配置生成：neo-cli 启动失败，回退 C# 配置", "Warning");
                return null;
            }
            var stderrTask = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                LogBus.App($"Rust 配置生成超时（{Timeout.TotalSeconds:0} s），回退 C# 配置", "Warning");
                return null;
            }
            if (process.ExitCode != 0)
            {
                var stderr = (await stderrTask).Trim();
                LogBus.App($"Rust 配置生成失败（exit {process.ExitCode}）：{Shorten(stderr, 200)}，回退 C# 配置", "Warning");
                return null;
            }
            if (!File.Exists(outputPath))
            {
                LogBus.App("Rust 配置生成失败：没有输出文件，回退 C# 配置", "Warning");
                return null;
            }
            var json = await File.ReadAllTextAsync(outputPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                LogBus.App("Rust 配置生成失败：输出为空，回退 C# 配置", "Warning");
                return null;
            }

            // 必须先用本机内核自检通过才允许使用，避免把坏配置交给内核。
            var checkExe = Path.Combine(NeoPaths.RuntimeDir, "sing-box.exe");
            var check = KernelCompat.RunCheck(checkExe, KernelKind.SingBox, outputPath, 8000);
            if (!check.Ok)
            {
                LogBus.App($"Rust 配置未通过 sing-box check：{KernelCompat.FirstLines(check.Output, 2)}，回退 C# 配置", "Warning");
                return null;
            }

            LogBus.App($"Rust 配置生成成功（{node.Name} · {(tun ? "TUN" : "系统代理")} · {json.Length:N0} B · check 通过）");
            return json;
        }
        catch (Exception ex)
        {
            LogBus.App($"Rust 配置生成异常（{ex.GetType().Name}: {ex.Message}），回退 C# 配置", "Warning");
            return null;
        }
        finally
        {
            try { if (inputPath.Length > 0 && File.Exists(inputPath)) File.Delete(inputPath); } catch { }
            try { if (outputPath.Length > 0 && File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static string Shorten(string value, int max)
        => string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value[..max] + "…");
}
