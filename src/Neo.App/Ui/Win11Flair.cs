using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Neo.App.Ui;

/// Windows 11 原生观感适配。集中处理四件系统级外观，避免散落在 MainWindow 各处：
/// ① **系统背景材质（Mica / Mica Alt）** —— Win11 的标志性特性：窗口背景取自桌面壁纸，
///    由 DWM 在合成层生成，不需要应用自己画像素。此前 neo 用自绘
///    `LinearGradientBrush` 铺满窗底，在 Win11 上反而「不像原生应用」。
///    注意 Mica 只能显示在**不透明窗口**上：它位于 XAML 内容之下、窗口之后，
///    所以必须让根 Grid 的背景透明，否则 Mica 被完全遮住（这是最常踩的坑）。
/// ② **DWM 圆角 / 边框色 / 深色标题栏** —— Win11 的窗口圆角与描边由 DWM 决定，
///    默认跟随系统；显式设置是为了让「暗/亮主题」切换时标题栏与边框一起变，
///    否则暗色界面配一个亮色标题栏会非常割裂。
/// ③ **标题栏按钮安全区（RightInset）** —— 自绘标题栏（ExtendsContentIntoTitleBar = true）
///    时，右上角必须有足够留白避免内容压到系统的最小化/最大化/关闭按钮。
///    旧实现把这 140px **硬编码**在 XAML Margin 里，在 125%/150% 缩放下系统按钮更宽，
///    于是内容被压住或留白过多。正确做法是读 TitleBar.RightInset 动态留白。
/// ④ **系统主题跟随** —— 用户在 Windows 设置里切亮/暗时，若 neo 处于「跟随系统」
///    状态（ElementTheme.Default），界面应立刻跟着变，而不是等下次启动。
/// 全部操作都做了 OS 版本与能力探测，并在失败时静默降级（调用方给的回调负责兜底），
/// 因为 DWM/Mica 在不同 Win11 小版本、以及 Win10 上行为并不一致，不能让它把启动搞崩。
public static class Win11Flair
{
    // ---- DWM 窗口属性 ----
    // 参考 Microsoft 文档 DwmSetWindowAttribute 的 DWMWINDOWATTRIBUTE 枚举。
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;      // 1809 起；旧值 19 需在 1903 前使用
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;     // Win11 起
    private const int DWMWA_BORDER_COLOR = 34;                 // Win11 起
    private const int DWMWA_CAPTION_COLOR = 35;                // Win11 起

    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    // 特殊值：DWMWA_COLOR_NONE = -2 表示「交给系统决定」。
    // 用 0xFFFFFFFE 传入即等价于 -2（该 API 接受 32 位 ARGB 或这些负值语义常量）。
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private static bool? _isWin11;

    /// <summary>是否 Windows 11（build 22000+）。Mica、圆角等只在 Win11 可用。</summary>
    public static bool IsWindows11
    {
        get
        {
            if (_isWin11 == null)
            {
                try
                {
                    var v = Environment.OSVersion.Version;
                    _isWin11 = v.Major >= 10 && v.Build >= 22000;
                }
                catch { _isWin11 = false; }
            }
            return _isWin11.Value;
        }
    }

    /// 尝试给窗口套用系统背景材质。
    /// 用 MicaController（WindowsAppSDK 自带）而非 DesktopAcrylicController：
    ///   · Mica       —— 壁纸色调，最「原生」，Win11 系统应用默认用它；
    ///   · Mica Alt   —— 更强调壁纸对比，适合带标签页的窗口；
    ///   · Acrylic    —— 模糊其后的实时内容，Win11 上主要给飞出菜单/弹出层用，
    ///                    整窗用会明显拖累滚动性能，故不用于主窗。
    /// Mica 对**不透明**窗口才是「壁纸色调」；若窗口透明（如靠 XAML 画半透明底），
    /// 效果会变成模糊其后的桌面 —— 与设计预期不同。neo 的玻璃卡片是半透明白色叠加，
    /// 放在 Mica 上观感正常。
    /// 返回是否成功套用；失败时调用方保留原来的自绘背景。
    public static bool TryApplyMica(Window window)
    {
        if (!IsWindows11) return false;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return false;

            // WindowsAppSDK 的 SystemBackdrop 必须在 UI 线程设置。
            window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            return true;
        }
        catch
        {
            // 某些 Win11 小版本 / 远程桌面 / 虚拟机会抛，这里只是观感增强，失败无妨。
            return false;
        }
    }

    /// 设置 DWM 层面的窗口外观：圆角、边框色、标题栏深色模式。
    /// 逐项独立 try，任何一项不受支持都不影响其它项。
    /// <param name="dark">当前是否暗色主题，决定标题栏深色模式与边框色。</param>
    /// <param name="accentBorder">可选：边框颜色（ARGB）。null 表示交给系统决定。</param>
    public static void ApplyDwmChrome(Window window, bool dark, uint? accentBorder = null)
    {
        if (!IsWindows11) return;
        IntPtr hwnd;
        try { hwnd = WindowNative.GetWindowHandle(window); } catch { return; }
        if (hwnd == IntPtr.Zero) return;

        // ① 窗口圆角：Win11 系统应用统一圆角（DWMWCP_ROUND）。
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        // ② 标题栏深色模式：让系统按钮与标题文字在暗色主题下用浅色，反之亦然。
        //    注意这与 AppWindow.TitleBar.ButtonForegroundColor 是两层：
        //    这里管 DWM 画的非客户区，那边管 WinUI 自绘的部分，两者都要设才一致。
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);

        // ③ 边框色：null 时用 DWMWA_COLOR_NONE 交还系统，避免留下上一次的硬编码色。
        TrySet(hwnd, DWMWA_BORDER_COLOR, accentBorder.HasValue
            ? unchecked((int)accentBorder.Value)
            : DWMWA_COLOR_NONE);
    }

    private static void TrySet(IntPtr hwnd, int attr, int value)
    {
        try
        {
            var v = value;
            DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int));
        }
        catch { }
    }

    /// 按系统标题栏实际的按钮宽度，给右上角留出安全区。
    /// 旧实现把 `Margin="0,0,140,0"` 硬编码在 XAML 里 —— 那是在 100% 缩放下估的固定值。
    /// 但 Win11 的 caption 按钮宽度随 DPI 缩放变化（100% 约 138px，150% 约 200px+），
    /// 于是高缩放下内容会被系统按钮压住。这里改用 TitleBar.RightInset 的真实值。
    /// 额外留一点呼吸空间（+8），因为 RightInset 贴着按钮，内容紧贴会显得拥挤。
    public static void ApplyTitleBarInset(Window window, FrameworkElement container, double extra = 8)
    {
        try
        {
            var tb = window.AppWindow?.TitleBar;
            if (tb == null) return;
            var inset = tb.RightInset;
            if (inset <= 0) inset = 140;   // 读不到时退回旧值，至少不会压住按钮
            container.Margin = new Thickness(0, 0, inset + extra, 0);
        }
        catch { }
    }

    private static bool _subscribed;

    /// 订阅系统主题变化。仅当 neo 处于「跟随系统」(ElementTheme.Default) 时才需要响应，
    /// 否则用户显式选了亮/暗，不该被系统改动覆盖。
    /// 用 UISettings.ColorValuesChanged 而不是 Application.RequestedThemeChanged：
    /// 后者在 WinUI 3 桌面应用里对「系统主题变化」并不总是触发（已知差异），
    /// 而 UISettings 直接监听系统颜色值，是更可靠的信号源。
    /// 回调在**后台线程**触发，调用方必须自行 marshal 回 UI 线程。
    public static void SubscribeSystemThemeChanged(Action<bool> onChanged)
    {
        if (_subscribed) return;
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            settings.ColorValuesChanged += (_, _) =>
            {
                try
                {
                    var bg = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                    // 系统暗色时 Background 是深色：亮度低即暗色主题。
                    var dark = (bg.R + bg.G + bg.B) / 3 < 128;
                    onChanged(dark);
                }
                catch { }
            };
            _subscribed = true;
        }
        catch { }
    }

    /// <summary>读当前系统是否暗色，用于「跟随系统」时初始化。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            var bg = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return (bg.R + bg.G + bg.B) / 3 < 128;
        }
        catch { return false; }
    }
}
