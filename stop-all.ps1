<#
.SYNOPSIS
    Stops the world, her brain, and any client.

.DESCRIPTION
    Nothing is lost by stopping. The world saves on every tick and on shutdown, and a restart
    inside two minutes is not even recorded as time away.

    Note it kills companion-api.exe rather than the dotnet process that launched it: `dotnet run`
    spawns the real server as a child, and killing only the wrapper leaves the child holding port
    5266 — which then makes the next start fail with "address already in use" and look like a
    crash. That has caused genuine confusion more than once.
#>
[CmdletBinding()]
param([switch]$KeepWorld, [switch]$KeepCompanion)

$ErrorActionPreference = 'SilentlyContinue'

function Stop-Named($pattern, $label) {
    $procs = @(Get-CimInstance Win32_Process -Filter $pattern)
    if ($procs.Count -eq 0) {
        Write-Host "$label  not running" -ForegroundColor DarkGray
        return
    }
    foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Write-Host "$label  stopped ($($procs.Count))" -ForegroundColor Green
}

if (-not $KeepCompanion) {
    Stop-Named "Name='companion-api.exe'" 'companion '
}
if (-not $KeepWorld) {
    # Covers the server and any client - both are Godot running this project.
    Stop-Named "Name LIKE 'Godot%'" 'world     '
}

Start-Sleep -Seconds 2

foreach ($port in 5266, 8738) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        Write-Host "warning: something is still listening on $port" -ForegroundColor Yellow
    }
}
