using System.Text.Json;
using Neo.App.Ui;
using Neo.Core.Parsing;
using Windows.Storage.Pickers;

namespace Neo.App.Services;

internal sealed partial class HtmlDesktopBridge
{
    private async Task<object> ImportNodeContentAsync(JsonElement payload)
    {
        if (NativeUiReview.Enabled) throw new InvalidOperationException("隔离审核不导入真实订阅");
        var content = Text(payload, "content");
        var name = payload.TryGetProperty("name", out var supplied) ? supplied.GetString()?.Trim() : null;
        var result = await AppState.ImportLocalContentAsync(content, string.IsNullOrWhiteSpace(name) ? "本地导入" : name[..Math.Min(name.Length, 80)], ct: _lifetime.Token);
        _catalogDirty = true;
        return new { added = result.Count, enabled = result.Enabled, subscriptionId = result.Id };
    }

    private async Task<object> ImportNodeFileAsync()
    {
        if (NativeUiReview.Enabled) throw new InvalidOperationException("隔离审核不打开文件选择器");
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".txt", ".yaml", ".yml", ".json", ".conf" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return new { cancelled = true };
        var path = SubscriptionSource.Normalize(file.Path);
        var content = await SubscriptionSource.ReadLocalAsync(path, _lifetime.Token);
        var result = await AppState.ImportLocalContentAsync(content, Path.GetFileNameWithoutExtension(path), path, _lifetime.Token);
        _catalogDirty = true;
        return new { added = result.Count, enabled = result.Enabled, subscriptionId = result.Id };
    }
}
