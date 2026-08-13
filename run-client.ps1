<#
.SYNOPSIS
    Opens a window onto a running world.

.DESCRIPTION
    The client is a viewer. It does not host anything — the world must already be running, either
    from run-server.ps1 on this machine or on another one — and closing this window changes nothing
    about the world except who is present in it.

    WASD walks, the mouse looks, Escape releases the mouse, clicking takes it back. Ava is the
    capsule that moves between rooms on her own.

.PARAMETER Host
    Which world to connect to. Defaults to this machine.

.PARAMETER Token
    The world's access token. Defaults to $env:AVAWORLD_TOKEN, then the .avaworld-token file the
    server wrote — which is why a client on the same machine needs no configuration at all.
#>
[CmdletBinding()]
param(
    [string]$Godot = $env:GODOT,
    [Alias('Host')][string]$WorldHost = '127.0.0.1',
    [int]$Port = 8737,
    [string]$Token = $env:AVAWORLD_TOKEN
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\AvaWorld.Server'

function Find-Godot {
    if ($Godot -and (Test-Path $Godot)) { return $Godot }
    $onPath = Get-Command godot -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $roots = @(
        "$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop",
        "$env:LOCALAPPDATA\Godot", "C:\Godot", "C:\Tools\Godot", "C:\Program Files\Godot"
    ) | Where-Object { Test-Path $_ }

    $found = foreach ($root in $roots) {
        Get-ChildItem -Path $root -Recurse -Filter 'Godot*.exe' -ErrorAction SilentlyContinue -Depth 3
    }
    if (-not $found) { return $null }

    # Prefer a .NET build. Prefer the windowed executable here - unlike the server, this one wants
    # a window, and the console variant would open a spare terminal alongside it.
    ($found |
        Sort-Object @{ Expression = { $_.Name -like '*mono*' }; Descending = $true },
                    @{ Expression = { $_.Name -notlike '*console*' }; Descending = $true } |
        Select-Object -First 1).FullName
}

$exe = Find-Godot
if (-not $exe) {
    Write-Host 'Could not find Godot.' -ForegroundColor Red
    Write-Host 'Set $env:GODOT to the .NET/Mono build, or pass -Godot with its path.'
    exit 1
}

$banner = (& $exe --version 2>&1 | Select-Object -First 1)
$hasGodotSharp = Test-Path (Join-Path (Split-Path $exe -Parent) 'GodotSharp')
if (-not [string]::IsNullOrWhiteSpace($banner) -and $banner -notmatch 'mono' -and -not $hasGodotSharp) {
    Write-Host ''
    Write-Host 'This Godot cannot run C#.' -ForegroundColor Red
    Write-Host "  version : $banner"
    Write-Host 'You need the ".NET" download from https://godotengine.org/download.'
    exit 1
}

# Same token resolution the client itself uses, checked here so the failure is legible rather than
# a window that opens and immediately closes.
if (-not $Token) {
    $tokenFile = Join-Path $projectPath '.avaworld-token'
    if (Test-Path $tokenFile) { $Token = (Get-Content $tokenFile -Raw).Trim() }
}
if (-not $Token) {
    Write-Host 'No token.' -ForegroundColor Red
    Write-Host 'Start the world here first, or set $env:AVAWORLD_TOKEN to match the world you are joining.'
    exit 1
}
$env:AVAWORLD_TOKEN = $Token

Write-Host "Joining the world at ${WorldHost}:${Port}" -ForegroundColor DarkGray
Write-Host 'WASD to walk, mouse to look, Escape to free the mouse, click to take it back.' -ForegroundColor DarkGray
Write-Host ''

& $exe --path $projectPath --client "--host=$WorldHost" "--port=$Port"
exit $LASTEXITCODE
