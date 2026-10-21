param([string]$Id = "SwitchSysProxy")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process neo -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { Write-Output "no neo process"; exit }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if ($null -eq $win) { Write-Output "no window element"; exit }

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $el) { Write-Output "$Id not found"; exit }

try {
    $tp = [System.Windows.Automation.TogglePattern]$el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Write-Output ("before: " + $tp.Current.ToggleState)
    $tp.Toggle()
    Start-Sleep -Milliseconds 600
    Write-Output ("after: " + $tp.Current.ToggleState)
} catch {
    Write-Output ("toggle failed: " + $_.Exception.Message)
}
