param(
    [Parameter(Mandatory)]
    [string] $ReadyPath,
    [Parameter(Mandatory)]
    [string] $ArmPath,
    [Parameter(Mandatory)]
    [string] $ArmedPath,
    [Parameter(Mandatory)]
    [string] $MeasurePath,
    [Parameter(Mandatory)]
    [string] $ResultPath,
    [int] $DurationMilliseconds = 1000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temporaryResultPath = "$ResultPath.tmp"

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CursorMeasurement
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static Point Cursor()
    {
        Point point;
        if (!GetCursorPos(out point))
            throw new InvalidOperationException(
                string.Format("GetCursorPos failed: {0}",
                    Marshal.GetLastWin32Error()));
        return point;
    }

    public static void Center()
    {
        if (!SetCursorPos(GetSystemMetrics(0) / 2, GetSystemMetrics(1) / 2))
            throw new InvalidOperationException(
                string.Format("SetCursorPos failed: {0}",
                    Marshal.GetLastWin32Error()));
    }
}
'@

function Wait-Signal([string] $Path) {
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $Path) -and
        (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 20
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Signal not received: '$Path'."
    }
}

try {
    [CursorMeasurement]::Center()
    Set-Content -LiteralPath $ReadyPath -Value ready -Encoding ascii
    Wait-Signal $ArmPath
    $start = [CursorMeasurement]::Cursor()
    Set-Content -LiteralPath $ArmedPath -Value armed -Encoding ascii
    Wait-Signal $MeasurePath
    Start-Sleep -Milliseconds $DurationMilliseconds
    $end = [CursorMeasurement]::Cursor()
    $json = [pscustomobject]@{
        Start = $start
        End = $end
        DeltaX = $end.X - $start.X
        DeltaY = $end.Y - $start.Y
        Error = $null
    } | ConvertTo-Json -Depth 3
}
catch {
    $json = [pscustomobject]@{
        Error = $_.Exception.ToString()
    } | ConvertTo-Json -Depth 3
}

[IO.File]::WriteAllText(
    $temporaryResultPath,
    $json,
    [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporaryResultPath, $ResultPath)
