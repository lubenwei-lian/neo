#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
校验 WinUI 页面：XAML 里定义的 x:Name 与 .cs 里引用的标识符是否一一对应。

为什么需要它：XAML 的 x:Name 会生成一个同名字段，.cs 里直接按名字用。
删掉/改名一个 x:Name 而 .cs 仍在引用 —— 编译期就会 CS0103；
反过来，XAML 定义了但 .cs 引用的名字拼错，同样炸。
这个脚本把两边都抽出来做差集，比人肉数快且不会漏。

用法: python tools/check-xaml-names.py <Page.xaml> <Page.xaml.cs>
退出码 0 = 一致；1 = 有缺口（缺谁就报谁）。
"""
import re
import sys
from pathlib import Path


def xaml_names(p: Path) -> set[str]:
    s = p.read_text(encoding="utf-8-sig")
    # 只认真正的 x:Name 属性，忽略注释里的内容
    s = re.sub(r"<!--.*?-->", "", s, flags=re.S)
    return set(re.findall(r'x:Name="([A-Za-z_][A-Za-z0-9_]*)"', s))


# 只有这些成员名才说明「左边那个标识符是个 XAML 控件」。
# 用它当锚点，才能把 XAML 字段与私有静态数组（DnPalette）、枚举
# （ContentDialogButton）、命名空间（Microsoft）区分开 —— 它们同样以点号链出现。
CONTROL_MEMBERS = {
    "Text", "Visibility", "IsOn", "IsEnabled", "Background", "Foreground",
    "Fill", "Stroke", "Opacity", "Children", "ColumnDefinitions",
    "RowDefinitions", "Width", "Height", "Content", "Items", "Style",
    "FontSize", "FontWeight", "Margin", "Padding", "Tag", "Source",
    "SelectedIndex", "Value", "IsChecked", "CornerRadius", "BorderThickness",
    "BorderBrush", "ActualWidth", "ActualHeight", "MaxWidth", "MinWidth",
    "RenderTransform", "XamlRoot", "Resources", "Measure", "UpdateLayout",
    "HorizontalAlignment", "VerticalAlignment", "SetLeft", "SetTop",
    "SetZIndex", "StrokeThickness", "TextTrimming", "Spacing",
}


def cs_identifiers(p: Path) -> set[str]:
    s = p.read_text(encoding="utf-8-sig")
    # 去掉注释，避免注释里提到的名字被算进来
    s = re.sub(r"//[^\n]*", "", s)
    s = re.sub(r"/\*.*?\*/", "", s, flags=re.S)
    # 只认「标识符.控件成员」这种组合；同时要求标识符前面不是点号，
    # 从而排除 Microsoft.UI.Xaml.Controls.Canvas 这类命名空间链的中间段。
    return {
        m.group(1)
        for m in re.finditer(r"(?<![.\w])([A-Z][A-Za-z0-9_]{1,})\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)", s)
        if m.group(2) in CONTROL_MEMBERS
    }


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    xaml, cs = Path(sys.argv[1]), Path(sys.argv[2])
    for f in (xaml, cs):
        if not f.is_file():
            print(f"找不到文件: {f}")
            return 2

    defined = xaml_names(xaml)
    used = cs_identifiers(cs)

    # .cs 里必然出现、但不是 XAML 字段的点号前缀（服务/静态类/类型等）
    KNOWN_NON_XAML = {
        "Math", "Console", "String", "Task", "File", "Directory", "Path", "Json",
        "DateTime", "TimeSpan", "Color", "Colors", "ElementTheme", "Visibility",
        "GridLength", "GridUnitType", "FontWeights", "RepeatBehavior", "Storyboard",
        "DoubleAnimation", "Canvas", "SolidColorBrush", "LinearGradientBrush",
        "DispatcherQueue", "Application", "Uri", "Enumerable", "MenuFlyout",
        "MenuFlyoutItem", "MenuFlyoutSeparator", "FontIcon", "ContentDialog",
        "ContentDialogResult", "DialogGuard", "Rectangle", "Ellipse", "WindowActivation",
        "WindowActivationState", "Windows", "Microsoft", "System", "Neo", "App",
        "AppState", "ConnectionService", "UsageTracker", "StatsSampler", "LogBus",
        "CoreStats", "NodeGrouper", "XrayConfigBuilder", "SingBoxConfigBuilder",
        "FlagPainter", "Ui", "ThemeUtil", "Size", "Point", "Nullable", "Assert",
        "Interlocked", "Expression", "Setting", "Settings", "SubItem", "NodeItem",
        "NodeProtocol", "TappedRoutedEventArgs", "PointerRoutedEventArgs",
        "RoutedEventArgs", "SizeChangedEventArgs", "EventArgs", "Binding",
        "PropertyPath", "UserControl", "DispatcherTimer", "CultureInfo",
        "StringComparison", "CharArray", "List", "Dictionary", "Guid", "Convert",
        "Environment", "Process", "Registry", "Tray", "Hook", "Native", "Marshal",
        "Span", "Range", "Exception", "Debug", "Trace", "Timer", "Thread",
        "CancellationToken", "Parallel", "TaskCompletionSource", "Func", "Action",
        "EqualityComparer", "GC", "Array", "MemoryExtensions", "Stopwatch",
    }

    missing = sorted(n for n in used - defined - KNOWN_NON_XAML)
    # 只报「看起来像本页字段」的：.cs 用了点号、XAML 却没有
    print(f"XAML 定义 x:Name : {len(defined)} 个")
    print(f"其中被 .cs 引用   : {len(defined & used)} 个")
    print(f"仅 XAML 定义未引用: {sorted(defined - used) or '无'}")

    if missing:
        print(f"\n[!] .cs 引用了但 XAML 里找不到的标识符（{len(missing)} 个）：")
        for n in missing:
            print(f"    - {n}")
        return 1
    print("\nOK：.cs 引用的标识符在 XAML 里都能找到对应 x:Name。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
