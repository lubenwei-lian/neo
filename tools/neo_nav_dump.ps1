Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process neo -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $p) { Write-Output "no neo process"; exit }
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if ($null -eq $win) { Write-Output "no window element"; exit }

Write-Output ("window rect: " + $win.Current.BoundingRectangle)

foreach ($id in @("NavConn","NavNodes","NavSubs","NavSettings","NavRule","NavLog","SwitchSysProxy")) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -ne $el) {
        $r = $el.Current.BoundingRectangle
        Write-Output ("$id => rect $($r.X),$($r.Y) $($r.Width)x$($r.Height) enabled=$($el.Current.IsEnabled) offscreen=$($el.Current.IsOffscreen) name=$($el.Current.Name)")
    } else {
        Write-Output "$id => NOT FOUND"
    }
}
