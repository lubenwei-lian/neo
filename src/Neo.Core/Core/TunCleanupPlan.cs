namespace Neo.Core.Core;

/// <summary>Targets one captured adapter identity, never all routes sharing a gateway.</summary>
public static class TunCleanupPlan
{
    public static string Build(Guid adapterId)
    {
        if (adapterId == Guid.Empty) throw new ArgumentException("An adapter identity is required", nameof(adapterId));
        return "$ErrorActionPreference = 'Stop'; "
            + "$owned = Get-CimInstance Win32_NetworkAdapter | Where-Object { $_.GUID -eq '"
            + adapterId.ToString("B") + "' }; "
            + "if (-not $owned) { 'ABSENT'; exit 0 }; "
            + "if (@($owned).Count -ne 1 -or -not $owned.PNPDeviceID) { throw 'Ambiguous adapter identity' }; "
            + "Get-NetRoute -InterfaceIndex $owned.InterfaceIndex -ErrorAction SilentlyContinue | "
            + "Remove-NetRoute -Confirm:$false -ErrorAction Stop; "
            + "& \"$env:SystemRoot\\System32\\pnputil.exe\" /remove-device $owned.PNPDeviceID; "
            + "if ($LASTEXITCODE -ne 0) { throw 'Adapter removal failed' }; 'REMOVED';";
    }
}
