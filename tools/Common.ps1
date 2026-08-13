<#
    Shared helpers for the launch scripts. Dot-source it:

        . "$PSScriptRoot\tools\Common.ps1"

    Finding Godot and checking it can run C# was copied into two scripts and about to be copied
    into a third, which is the point at which a copy becomes a bug waiting to be fixed once.
#>

function Find-GodotExe {
    <#
    .SYNOPSIS
        Locates a Godot that can run C#.
    .PARAMETER Preferred
        An explicit path to try first ($env:GODOT, or -Godot from a caller).
    .PARAMETER Windowed
        Prefer the executable that opens a window. The server wants the console build so its log is
        visible; the client wants the plain one so it does not open a spare terminal beside the game.
    #>
    param([string]$Preferred, [switch]$Windowed)

    if ($Preferred -and (Test-Path $Preferred)) { return $Preferred }
    if ($env:GODOT -and (Test-Path $env:GODOT)) { return $env:GODOT }

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

    # .NET build first; then console or windowed depending on the role.
    $found |
        Sort-Object `
            @{ Expression = { $_.Name -like '*mono*' }; Descending = $true },
            @{ Expression = { if ($Windowed) { $_.Name -notlike '*console*' } else { $_.Name -like '*console*' } }; Descending = $true } |
        Select-Object -First 1 -ExpandProperty FullName
}

function Test-GodotRunsCSharp {
    <#
    .SYNOPSIS
        True unless there is positive evidence this Godot lacks C# support.
    .DESCRIPTION
        Two signals, because neither is reliable alone: the version banner is definitive when it can
        be read, but the windowed executable is a Windows-subsystem app whose stdout is not attached
        to a pipe and reports nothing. The GodotSharp folder ships only with the .NET build and
        needs no process launch.

        Inconclusive is treated as fine. A false "wrong Godot" is worse than letting Godot speak.
    #>
    param([Parameter(Mandatory)][string]$Exe)

    $banner = (& $Exe --version 2>&1 | Select-Object -First 1)
    $hasGodotSharp = Test-Path (Join-Path (Split-Path $Exe -Parent) 'GodotSharp')

    if ([string]::IsNullOrWhiteSpace($banner)) { return $true }
    if ($banner -match 'mono') { return $true }
    return $hasGodotSharp
}

function Write-WrongGodot {
    param([string]$Exe)
    Write-Host ''
    Write-Host 'This Godot cannot run C#.' -ForegroundColor Red
    Write-Host "  executable : $Exe"
    Write-Host 'AvaWorld is a C# project, so it fails with'
    Write-Host "  'No loader found for resource: res://Main.cs'"
    Write-Host 'which does NOT mean the project is broken - Godot simply does not know what a .cs file is.'
    Write-Host 'You need the ".NET" download from https://godotengine.org/download.'
}

function Test-PortListening {
    <#
    .SYNOPSIS
        True when something is already listening on a TCP port.
    .DESCRIPTION
        Used to make the launcher idempotent: starting a piece that is already up should be a
        no-op with a clear message, not a second copy that fails on a bound port and looks like a
        crash. That exact confusion cost an evening.
    #>
    param([Parameter(Mandatory)][int]$Port)
    $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

function Get-WorldToken {
    param([Parameter(Mandatory)][string]$WorldProject)
    if ($env:AVAWORLD_TOKEN) { return $env:AVAWORLD_TOKEN }
    $file = Join-Path $WorldProject '.avaworld-token'
    if (Test-Path $file) { return (Get-Content $file -Raw).Trim() }
    return $null
}
