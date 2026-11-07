<#
.SYNOPSIS
    Local co-op test on one PC: builds the mod, installs it into the game and opens two game windows that connect
    by themselves (A hosts, B joins by IP). No friend and no Steam invite needed.

.DESCRIPTION
    1. Builds src\SoulstoneCoop (Debug). The whole build output also goes to dist\test-local\build.log.
    2. Copies SoulstoneCoop.dll and LiteNetLib.dll into <game>\BepInEx\plugins\SoulstoneCoop. The plugin that was
       there the first time is kept in dist\test-local\plugin-before (copy it back to play with the released
       version again).
    3. Creates <game>\steam_appid.txt for the session: without it Steam relaunches the game and drops the launch
       options. It's removed when the windows are closed (with it, Steam invites don't work).
    4. Opens window A (--coop-host --coop-name A), then window B (--coop-connect 127.0.0.1 --coop-name B), both with
       --coop-test-keys (the F-key test keys, README "Test keys").
    5. When every window is closed: copies the logs to dist\test-local\<date-time>\
       (LogOutput.log = A, LogOutput.1.log = B, ...).

    In the match, F3 in window A spawns a chest/ore in front of the host (docs\baus-notas.md).
    Close the windows normally (or Ctrl+C here) so step 5 runs; closing this console skips it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-local.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-local.ps1 -NoBuild -Players 3
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Soulstone Survivors',
    [switch]$NoBuild,
    [ValidateRange(2, 4)][int]$Players = 2,
    [int]$Width = 960,
    [int]$Height = 540,
    [int]$SecondsBetweenWindows = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutRoot = Join-Path $RepoRoot 'dist\test-local'
$Exe = Join-Path $GameDir 'Soulstone Survivors.exe'
$PluginDir = Join-Path $GameDir 'BepInEx\plugins\SoulstoneCoop'
$AppIdFile = Join-Path $GameDir 'steam_appid.txt'
$SteamAppId = '2066020'
$BuildOut = Join-Path $RepoRoot 'src\SoulstoneCoop\bin\Debug\net6.0'

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not (Test-Path $Exe)) { Fail "game not found: $Exe (pass -GameDir)" }
if (Get-Process -Name 'Soulstone Survivors' -ErrorAction SilentlyContinue) { Fail "the game is already running: close it first" }
New-Item -ItemType Directory -Force $OutRoot | Out-Null

# The game rewrites its logs on every launch: keep the ones from before (a run whose console was closed early, or a
# window opened again afterwards) in dist\test-local\before-<time of the newest log>\.
$oldLogs = @(Get-ChildItem (Join-Path $GameDir 'BepInEx') -Filter '*.log' -ErrorAction SilentlyContinue)
if ($oldLogs.Count -gt 0) {
    $newest = ($oldLogs | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    $keep = Join-Path $OutRoot ('before-' + $newest.ToString('yyyyMMdd-HHmmss'))
    if (-not (Test-Path $keep)) {
        New-Item -ItemType Directory -Force $keep | Out-Null
        foreach ($l in $oldLogs) { Copy-Item -LiteralPath $l.FullName -Destination $keep -Force }
        Write-Host "Previous logs kept in $keep"
    }
}

# ---------------------------------------------------------------- build

if (-not $NoBuild) {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    $dotnet = if ($dotnetCmd) { $dotnetCmd.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
    if (-not (Test-Path $dotnet)) { Fail ".NET SDK not found (dotnet.exe): install the .NET SDK 6 or newer" }

    $buildLog = Join-Path $OutRoot 'build.log'
    Step "Building (full output also in $buildLog)"
    # Native output through PowerShell: 'Stop' would turn any stderr line into a terminating error.
    $ErrorActionPreference = 'Continue'
    & $dotnet build (Join-Path $RepoRoot 'src\SoulstoneCoop') -c Debug -p:SkipDeploy=true "-p:GameDir=$GameDir" -v minimal --nologo 2>&1 |
        ForEach-Object { "$_" } | Tee-Object -FilePath $buildLog
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { Fail "build failed (exit $code): the errors are above and in $buildLog" }
}

foreach ($f in @('SoulstoneCoop.dll', 'LiteNetLib.dll')) {
    if (-not (Test-Path (Join-Path $BuildOut $f))) { Fail "$f not found in $BuildOut (build first, without -NoBuild)" }
}

# ---------------------------------------------------------------- install

Step "Installing into $PluginDir"
$backup = Join-Path $OutRoot 'plugin-before'
if ((Test-Path (Join-Path $PluginDir 'SoulstoneCoop.dll')) -and -not (Test-Path $backup)) {
    Copy-Item -LiteralPath $PluginDir -Destination $backup -Recurse
    Write-Host "    the plugin that was installed is kept in $backup"
}
if (Test-Path (Join-Path $env:USERPROFILE '.soulstone-no-deploy')) {
    Write-Host "    note: %USERPROFILE%\.soulstone-no-deploy exists; this script installs anyway" -ForegroundColor Yellow
}
try {
    New-Item -ItemType Directory -Force $PluginDir | Out-Null
    foreach ($f in @('SoulstoneCoop.dll', 'LiteNetLib.dll')) {
        Copy-Item -LiteralPath (Join-Path $BuildOut $f) -Destination $PluginDir -Force
    }
}
catch { Fail "could not copy into the game folder ($($_.Exception.Message)); run PowerShell as administrator" }
$dllTime = (Get-Item (Join-Path $PluginDir 'SoulstoneCoop.dll')).LastWriteTime
Write-Host "    SoulstoneCoop.dll built $dllTime"

# ---------------------------------------------------------------- windows

# A marker says the file is ours, so a run whose console was closed early still gets it removed by the next one.
$appIdMarker = Join-Path $OutRoot 'steam_appid.created'
$createdAppId = Test-Path $appIdMarker
if (-not (Test-Path $AppIdFile)) {
    Set-Content -LiteralPath $AppIdFile -Value $SteamAppId -Encoding ASCII -NoNewline
    $createdAppId = $true
}
if ($createdAppId) { Set-Content -LiteralPath $appIdMarker -Value $AppIdFile -Encoding ASCII }

$started = Get-Date
$names = @('A', 'B', 'C', 'D')
$procs = @()
try {
    for ($i = 0; $i -lt $Players; $i++) {
        $name = $names[$i]
        $role = if ($i -eq 0) { '--coop-host' } else { '--coop-connect 127.0.0.1' }
        $argLine = "-screen-fullscreen 0 -screen-width $Width -screen-height $Height $role --coop-name $name --coop-test-keys"
        Step "Window $name ($role)"
        $procs += Start-Process -FilePath $Exe -ArgumentList $argLine -WorkingDirectory $GameDir -PassThru
        if ($i -lt $Players - 1) {
            Write-Host "    waiting $SecondsBetweenWindows s so A is hosting before the next one connects..."
            Start-Sleep -Seconds $SecondsBetweenWindows
        }
    }

    Write-Host ""
    Write-Host "Windows open. In each one, a black BepInEx console opens too: leave it." -ForegroundColor Green
    Write-Host "  - A hosts by itself; B connects by itself (if B says it couldn't connect: Co-op tab -> Join, 127.0.0.1)."
    Write-Host "  - Pick a character in each window; A picks the map and presses Fight; B follows."
    Write-Host "  - In the match, F3 in window A spawns a chest/ore in front of A (press again for the next kind)."
    Write-Host "  - A stays where you left it while you play in B's window, so both can stand on a chest."
    Write-Host "  - Steps to check: docs\baus-notas.md (Roteiro de teste)."
    Write-Host "Close the game windows when done; this console then collects the logs." -ForegroundColor Green
    foreach ($p in $procs) { $p.WaitForExit() }
}
finally {
    if ($createdAppId) {
        if (Test-Path $AppIdFile) { Remove-Item -LiteralPath $AppIdFile -Force }
        if (Test-Path $appIdMarker) { Remove-Item -LiteralPath $appIdMarker -Force }
        Write-Host "steam_appid.txt removed (Steam invites work again)."
    }
    $dest = Join-Path $OutRoot ($started.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $dest | Out-Null
    $logs = Get-ChildItem (Join-Path $GameDir 'BepInEx') -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started }
    foreach ($l in $logs) { Copy-Item -LiteralPath $l.FullName -Destination $dest -Force }
    Write-Host "Logs copied to $dest ($(@($logs).Count) file(s))."
}
