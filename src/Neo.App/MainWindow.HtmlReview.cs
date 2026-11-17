using Microsoft.UI.Xaml;

namespace Neo.App;

public sealed partial class MainWindow
{
    private Ui.NativeHtmlWindow? _nativeHtmlReview;
    private Services.HtmlDesktopBridge? _htmlBridgeReview;
    private void InitializeHtmlReview()
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        Ui.NativeUiReview.MakeNonActivating(this);
        Root.Loaded += async (_, _) =>
        {
            AppWindow.Hide();
            try
            {
                _nativeHtmlReview = new Ui.NativeHtmlWindow(true);
                _nativeHtmlReview.CloseRequested += () => { _htmlBridgeReview?.Dispose(); _nativeHtmlReview.Dispose(); Application.Current.Exit(); };
                await _nativeHtmlReview.InitializeAsync(Path.Combine(Ui.NativeUiReview.DirectoryPath,"webview"),
                    new Uri("https://neo.ui/index.html"),true,async core=>
                    {
                        _htmlBridgeReview=new Services.HtmlDesktopBridge(_nativeHtmlReview,DispatcherQueue,()=>{_htmlBridgeReview?.Dispose();_nativeHtmlReview.Dispose();Application.Current.Exit();});
                        await _htmlBridgeReview.ConfigureAsync();
                        core.NavigationCompleted += (_, e) => File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath,"html-ready.json"),
                            System.Text.Json.JsonSerializer.Serialize(new { e.IsSuccess, error=e.WebErrorStatus.ToString(), pid=Environment.ProcessId }));
                    });
                _nativeHtmlReview.Show(false);
#if NEO_UI_REVIEW
                if (Environment.GetCommandLineArgs().Contains("--verify-visibility"))
                {
                    for (var attempt=0;attempt<100;attempt++)
                    {
                        if(await _nativeHtmlReview.Core!.ExecuteScriptAsync("!!window.neoTraffic&&!!document.querySelector('.page[data-page=conn].on')")=="true")break;
                        await Task.Delay(100);
                    }
                    _htmlBridgeReview?.Dispose();
                    try { await _nativeHtmlReview.VerifyVisibilityAsync(DispatcherQueue); }
                    finally { _nativeHtmlReview.Dispose(); Application.Current.Exit(); }
                    return;
                }
#endif
                _reviewTimer = DispatcherQueue.CreateTimer();
                _reviewTimer.Interval = TimeSpan.FromMilliseconds(150);
                _reviewTimer.Tick += async (_, _) =>
                {
                    var command=Path.Combine(Ui.NativeUiReview.DirectoryPath,"capture.request");
                    if(!File.Exists(command)||_reviewBusy)return;_reviewBusy=true;
                    try
                    {
                        var name=File.ReadAllText(command).Trim();File.Delete(command);
                        await Ui.NativeCaptureReview.CaptureAsync(_nativeHtmlReview.Handle,
                            Path.Combine(Ui.NativeUiReview.DirectoryPath,name+".png"));
                    }
                    catch(Exception ex){Ui.NativeUiReview.WriteError(ex.ToString());}
                    finally{_reviewBusy=false;}
                };
                _reviewTimer.Start();
            }
            catch(Exception ex){Ui.NativeUiReview.WriteError(ex.ToString());}
        };
        AppWindow.Show(false);Ui.NativeUiReview.KeepBehind(this);
        File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath,"ready.json"),"{}");
    }
}
