param([string]$Id = "NavLog", [string]$Out = "C:\Users\somew\AppData\Local\hermes\cache\scratch\neo_shots\x.png")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class NavShotW {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$p = Get-Process neo -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { Write-Output "no neo process"; exit }
$h = $p.MainWindowHandle

$win = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $el) { Write-Output "$Id not found"; exit }

try {
    $inv = [System.Windows.Automation.InvokePattern]$el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Write-Output "invoked $Id"
} catch {
    Write-Output ("invoke failed: " + $_.Exception.Message)
    exit
}

Start-Sleep -Milliseconds 700

[NavShotW]::SetWindowPos($h, [IntPtr](-1), 120, 80, 0, 0, 0x0011) | Out-Null
Start-Sleep -Milliseconds 400

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g1 = [System.Drawing.Graphics]::FromImage($bmp)
$g1.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$g1.Dispose()

$r = New-Object NavShotW+RECT
[NavShotW]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
$x = $r.Left; $y = $r.Top
if ($x -lt 0) { $x = 0 }
if ($y -lt 0) { $y = 0 }
if ($x + $w -gt $bmp.Width) { $w = $bmp.Width - $x }
if ($y + $hh -gt $bmp.Height) { $hh = $bmp.Height - $y }
$c = New-Object System.Drawing.Bitmap $w, $hh
$g2 = [System.Drawing.Graphics]::FromImage($c)
$g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0,0,$w,$hh)), (New-Object System.Drawing.Rectangle($x,$y,$w,$hh)), [System.Drawing.GraphicsUnit]::Pixel)
$c.Save($Out)
$g2.Dispose(); $c.Dispose(); $bmp.Dispose()

[NavShotW]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null
Write-Output ("saved " + $Out + " (window " + $r.Left + "," + $r.Top + " " + $w + "x" + $hh + ")")
