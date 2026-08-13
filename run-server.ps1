<#
.SYNOPSIS
    Runs the AvaWorld headless server, refusing to start under a Godot build that can't run C#.

.DESCRIPTION
    Godot ships two Windows builds. The standard one has no C# support, and launching this project
    with it produces an error that says nothing about the real cause:

        ERROR: No loader found for resource: res://Main.cs (expected type: Script)
        ERROR: res://Main.tscn:6 - Parse Error: [ext_resource] referenced non-existent resource

    That is not a broken project. It is Godot not knowing what a .cs file is, because the build
    was compiled without the .NET module. The two are told apart by the version banner: the one
    you need says "mono".

        4.7.1.stable.mono.official   <- correct
        4.7.1.stable.official        <- no C# support

    This script checks before launching and says so plainly, rather than letting Godot fail
    obscurely fifteen lines later.

.PARAMETER Godot
    Path to the Godot executable. Defaults to $env:GODOT, then a search of the usual places.
    Prefer the *_console.exe variant on Windows — the plain one detaches from the terminal and
    you will not see the world's log output.
#>
[CmdletBinding()]
param(
    [string]$Godot = $env:GODOT,
    [switch]$Import
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\AvaWorld.Server'

function Find-Godot {
    if ($Godot -and (Test-Path $Godot)) { return $Godot }

    $onPath = Get-Command godot -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Prefer a console build (visible stdout), and prefer mono builds over standard ones.
    $roots = @(
        "$env:USERPROFILE\Downloads", "$env:USERPROFILE\Desktop",
        "$env:LOCALAPPDATA\Godot", "C:\Godot", "C:\Tools\Godot", "C:\Program Files\Godot"
    ) | Where-Object { Test-Path $_ }

    $found = foreach ($root in $roots) {
        Get-ChildItem -Path $root -Recurse -Filter 'Godot*.exe' -ErrorAction SilentlyContinue -Depth 3
    }

    if (-not $found) { return $null }

    ($found |
        Sort-Object @{ Expression = { $_.Name -like '*mono*' }; Descending = $true },
                    @{ Expression = { $_.Name -like '*console*' }; Descending = $true } |
        Select-Object -First 1).FullName
}

$exe = Find-Godot
if (-not $exe) {
    Write-Host "Could not find Godot." -ForegroundColor Red
    Write-Host 'Set $env:GODOT to the .NET/Mono build, or pass -Godot with its path.'
    Write-Host 'Get it from https://godotengine.org/download - the ".NET" download, not the standard one.'
    exit 1
}

# The check this script exists for.
#
# Two independent signals, because neither is reliable alone. The version banner is definitive
# when we can read it, but the GUI executable is a Windows-subsystem app whose stdout is not
# attached to a pipe, so it reports nothing. The GodotSharp folder ships only with the .NET build
# and needs no process launch at all.
#
# Only refuse when there is positive evidence the build lacks C#. An unreadable version with no
# GodotSharp folder is inconclusive, and a false "wrong Godot" is worse than letting Godot speak
# for itself.
$banner = (& $exe --version 2>&1 | Select-Object -First 1)
$hasGodotSharp = Test-Path (Join-Path (Split-Path $exe -Parent) 'GodotSharp')
$bannerSaysMono = $banner -match 'mono'
$bannerReadable = -not [string]::IsNullOrWhiteSpace($banner)

if ($bannerReadable -and -not $bannerSaysMono -and -not $hasGodotSharp) {
    Write-Host ""
    Write-Host "This Godot cannot run C#." -ForegroundColor Red
    Write-Host "  executable : $exe"
    Write-Host "  version    : $banner"
    Write-Host ""
    Write-Host "AvaWorld.Server is a C# project, so this build fails with"
    Write-Host "  'No loader found for resource: res://Main.cs'"
    Write-Host "which does NOT mean the project is broken - Godot simply does not know what a .cs file is."
    Write-Host ""
    Write-Host 'You need the ".NET" download from https://godotengine.org/download - its version'
    Write-Host 'string contains "mono". Then set $env:GODOT to it, or pass -Godot with its path.'
    exit 1
}

if (-not $bannerSaysMono -and -not $hasGodotSharp) {
    Write-Host "Could not confirm this Godot supports C#; continuing anyway." -ForegroundColor Yellow
    Write-Host "If it fails with 'No loader found for resource: res://Main.cs', you are on the standard build."
}

Write-Host "Godot : $exe" -ForegroundColor DarkGray
if ($bannerReadable) { Write-Host "Build : $banner" -ForegroundColor DarkGray }
Write-Host ""

if ($Import) {
    & $exe --headless --path $projectPath --import
    exit $LASTEXITCODE
}

& $exe --headless --path $projectPath
exit $LASTEXITCODE
