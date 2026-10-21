param([string]$ClickId = "")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class ProbeW {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$p = Get-Process neo -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { Write-Output "no neo process"; exit }
$h = $p.MainWindowHandle

[ProbeW]::ShowWindow($h, 9) | Out-Null
Start-Sleep -Milliseconds 250

[ProbeW]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 350

if ($ClickId -ne "") {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ClickId)
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -ne $btn) {
        $r0 = $btn.Current.BoundingRectangle
        $cx = [int]($r0.X + $r0.Width / 2)
        $cy = [int]($r0.Y + $r0.Height / 2)
        [ProbeW]::SetCursorPos($cx, $cy) | Out-Null
        Start-Sleep -Milliseconds 250
        [ProbeW]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [ProbeW]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Write-Output ("clicked " + $ClickId + " at " + $cx + "," + $cy)
    } else {
        Write-Output ($ClickId + " not found")
    }
    Start-Sleep -Milliseconds 500
}

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g1 = [System.Drawing.Graphics]::FromImage($bmp)
$g1.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$g1.Dispose()

$r = New-Object ProbeW+RECT
[ProbeW]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
$x = $r.Left; $y = $r.Top
if ($x -lt 0) { $x = 0 }
if ($y -lt 0) { $y = 0 }
if ($x + $w -gt $bmp.Width) { $w = $bmp.Width - $x }
if ($y + $hh -gt $bmp.Height) { $hh = $bmp.Height - $y }
$c = New-Object System.Drawing.Bitmap $w, $hh
$g2 = [System.Drawing.Graphics]::FromImage($c)
$g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0,0,$w,$hh)), (New-Object System.Drawing.Rectangle($x,$y,$w,$hh)), [System.Drawing.GraphicsUnit]::Pixel)
$c.Save("C:\Users\somew\AppData\Local\Temp\neo_win_now.png")
$g2.Dispose(); $c.Dispose(); $bmp.Dispose()
Write-Output ("shot: window " + $x + "," + $y + " size " + $w + "x" + $hh)

[ProbeW]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null
Write-Output "done"
