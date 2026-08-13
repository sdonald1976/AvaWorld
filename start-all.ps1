<#
.SYNOPSIS
    Brings the whole thing up: the world, her brain, and a window to watch through.

.DESCRIPTION
    Three processes have to be running, in this order, and knowing which of them is missing is not
    obvious from inside the game — a world with no companion looks identical to a companion with
    nothing to do, and both look like "nothing is happening".

        world server    the place. Runs headless, keeps going when nothing is watching.
        companion       her mind. Decides where she goes and why.
        client          a window. Optional; the world does not care whether anyone is looking.

    Already-running pieces are left alone rather than started twice, because a second copy fails on
    a bound port and reads as a crash.

.PARAMETER NoClient
    Start the world and her brain, but do not open a window.

.PARAMETER NoCompanion
    Start the world and a window without her brain. She falls back to drifting between rooms at
    random, which is the world's placeholder rather than her deciding anything.

.PARAMETER Companion
    Where the companion repo is. Defaults to a sibling of this one.

.EXAMPLE
    .\start-all.ps1
#>
[CmdletBinding()]
param(
    [string]$Godot,
    [string]$Companion,
    [switch]$NoClient,
    [switch]$NoCompanion
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\tools\Common.ps1"

# Not a parameter default: $PSScriptRoot is not populated during parameter binding in Windows
# PowerShell, so the default silently became an empty path and Split-Path threw before the script
# had done anything at all.
if (-not $Companion) {
    $Companion = Join-Path (Split-Path $PSScriptRoot -Parent) 'Persisten_AI'
}

$worldProject = Join-Path $PSScriptRoot 'src\AvaWorld.Server'
$wirePort = 8738
$companionPort = 5266

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# ---- the world ----

if (Test-PortListening $wirePort) {
    Say "world      already running (wire on $wirePort)" DarkGray
}
else {
    $exe = Find-GodotExe -Preferred $Godot
    if (-not $exe) {
        Say 'Could not find Godot. Set $env:GODOT to the .NET build, or pass -Godot.' Red
        exit 1
    }
    if (-not (Test-GodotRunsCSharp -Exe $exe)) { Write-WrongGodot $exe; exit 1 }

    Start-Process -FilePath $exe -ArgumentList '--headless', '--path', $worldProject -WindowStyle Minimized
    Say "world      starting ($(Split-Path $exe -Leaf))" Green

    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-PortListening $wirePort) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }

    if (-not (Test-PortListening $wirePort)) {
        Say "world      did NOT come up on $wirePort - run .\run-server.ps1 to see why" Red
        exit 1
    }
    Say "world      up (wire on $wirePort)" Green
}

# ---- her brain ----

if ($NoCompanion) {
    Say 'companion  skipped - she will drift at random, which is the placeholder, not her deciding' Yellow
}
elseif (Test-PortListening $companionPort) {
    Say "companion  already running (api on $companionPort)" DarkGray
}
elseif (-not (Test-Path (Join-Path $Companion 'src\Companion.Api\Companion.Api.csproj'))) {
    Say "companion  not found at $Companion - pass -Companion with its path" Yellow
}
else {
    # It reads the world's address and token from appsettings.local.json. If that is missing, say
    # so plainly: a companion that starts without a world is the failure that looks like success.
    $localSettings = Join-Path $Companion 'src\Companion.Api\appsettings.local.json'
    if (-not (Test-Path $localSettings)) {
        $token = Get-WorldToken -WorldProject $worldProject
        if ($token) {
            Say '           no appsettings.local.json - passing the world address in this session only' Yellow
            $env:World__Url = "ws://127.0.0.1:$wirePort"
            $env:World__Token = $token
        }
        else {
            Say '           no appsettings.local.json and no token - she will run WITHOUT a world' Yellow
        }
    }

    Push-Location $Companion
    try {
        Start-Process -FilePath 'dotnet' -ArgumentList 'run', '--project', 'src\Companion.Api' -WindowStyle Minimized
    }
    finally { Pop-Location }
    Say 'companion  starting' Green

    $deadline = (Get-Date).AddSeconds(120)   # a cold build plus migrations is not quick
    while (-not (Test-PortListening $companionPort) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }

    if (Test-PortListening $companionPort) { Say "companion  up (api on $companionPort)" Green }
    else { Say '           still starting - it may just be building' Yellow }
}

# ---- a window ----

if ($NoClient) {
    Say 'client     skipped' DarkGray
}
else {
    $clientExe = Find-GodotExe -Preferred $Godot -Windowed
    $token = Get-WorldToken -WorldProject $worldProject
    if (-not $token) {
        Say 'client     no token found - cannot join' Yellow
    }
    else {
        $env:AVAWORLD_TOKEN = $token
        Start-Process -FilePath $clientExe -ArgumentList '--path', $worldProject, '--client'
        Say 'client     opening - WASD to walk, mouse to look, Escape frees the cursor' Green
    }
}

# ---- what to expect ----

Write-Host ''
if (-not $NoCompanion) {
    Say 'She will mostly stand still, and that is correct: her policy only moves her when there is' DarkGray
    Say 'a reason - something on her mind, or something in a room that needs looking after. The' DarkGray
    Say "companion's window logs every decision and why she made it." DarkGray
}
Write-Host ''
Say 'Stop everything with .\stop-all.ps1' DarkGray
