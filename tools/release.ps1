<#
.SYNOPSIS
    Publishes a GitHub release of the mod: Release build, zip (tools/package.ps1), tag v<version>, release with the zip.

.DESCRIPTION
    The version comes from PluginVersion in src/SoulstoneCoop/Plugin.cs; bump it (and commit) before releasing.
    Runs locally on purpose: the plugin only compiles against the interop assemblies BepInEx generates from the
    installed game, which can't live in the repo or on a CI runner.

    Steps: sanity checks (on master, clean tree, in sync with origin, tag not taken, gh logged in) -> Release build
    with -p:SkipDeploy=true (the installed game is left alone) -> package.ps1 from the build output -> annotated tag
    pushed to origin -> gh release create with the zip.

    Release notes: docs/releases/<version>.md if it exists, otherwise GitHub's generated notes.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -DryRun
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Prerelease
#>
[CmdletBinding()]
param(
    [switch]$Prerelease,
    [switch]$Draft,
    [switch]$DryRun,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Soulstone Survivors'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Invoke-Git { & git.exe @args; if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed" } }

$ghCmd = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($ghCmd) { $ghCmd.Source } else { 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { Fail "GitHub CLI not found (winget install GitHub.cli)" }

# ---------------------------------------------------------------- version and checks

$m = Select-String -Path 'src\SoulstoneCoop\Plugin.cs' -Pattern 'PluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $m) { Fail "PluginVersion not found in src\SoulstoneCoop\Plugin.cs" }
$Version = $m.Matches[0].Groups[1].Value
$Tag = "v$Version"
Step "Releasing $Tag$(if ($DryRun) { ' (dry run)' })"

$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') { Fail "releases come from master (current branch: $branch)" }
if (& git status --porcelain) { Fail "working tree is not clean; commit or stash first" }

Invoke-Git fetch --quiet origin --tags
$local = (& git rev-parse HEAD).Trim()
$remote = (& git rev-parse origin/master).Trim()
if ($local -ne $remote) { Fail "master ($local) is not in sync with origin/master ($remote); push or pull first" }
if (& git tag --list $Tag) {
    if ($DryRun) { Write-Warning "tag $Tag already exists (dry run continues; a real release needs a PluginVersion bump)" }
    else { Fail "tag $Tag already exists; bump PluginVersion first" }
}

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Fail "gh is not logged in (gh auth login)" }

# ---------------------------------------------------------------- build and package

Step "Release build (no deploy into the game)"
$buildOut = Join-Path $RepoRoot 'src\SoulstoneCoop\bin\Release\net6.0'
& dotnet build 'src\SoulstoneCoop' -c Release -p:SkipDeploy=true -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail "build failed" }

Step "Packaging"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'package.ps1') -GameDir $GameDir -PluginSource $buildOut
if ($LASTEXITCODE -ne 0) { Fail "package.ps1 failed" }
$zip = Join-Path $RepoRoot "dist\SoulstoneCoop-$Version.zip"
if (-not (Test-Path $zip)) { Fail "zip not found: $zip" }

# Checksum: published next to the zip and in the notes, so players can tell the official zip from a re-upload.
$zipName = Split-Path $zip -Leaf
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
$shaFile = "$zip.sha256"
[IO.File]::WriteAllText($shaFile, "$sha  $zipName`n", (New-Object Text.UTF8Encoding($false)))
Step "SHA-256 $sha"

$checksumSection = @"

## Verify your download

Only download Soulstone Coop from this repository's [Releases](https://github.com/gskolber/soulstone-survivors-coop/releases). Copies, re-uploads or ports hosted elsewhere are not checked by the author.

``SHA-256 ($zipName): $sha``

In PowerShell: ``Get-FileHash .\$zipName`` must print the same hash (case doesn't matter).
"@
$notesFile = Join-Path $RepoRoot "docs\releases\$Version.md"
$notesOut = Join-Path $RepoRoot "dist\release-notes-$Version.md"
$body = if (Test-Path $notesFile) { [IO.File]::ReadAllText($notesFile).TrimEnd() } else { '' }
[IO.File]::WriteAllText($notesOut, ($body + "`n" + $checksumSection), (New-Object Text.UTF8Encoding($false)))
$notesArgs = if (Test-Path $notesFile) { @('--notes-file', $notesOut) } else { @('--generate-notes', '--notes-file', $notesOut) }

if ($DryRun) {
    Step "Dry run: would tag $Tag at $local and publish $zip + $(Split-Path $shaFile -Leaf) with notes $notesOut"
    exit 0
}

# ---------------------------------------------------------------- tag and release

Step "Tagging $Tag"
Invoke-Git tag -a $Tag -m "Soulstone Coop $Version"
Invoke-Git push origin $Tag

Step "Creating the GitHub release"
$relArgs = @('release', 'create', $Tag, $zip, $shaFile, '--title', "Soulstone Coop $Version", '--verify-tag') + $notesArgs
if ($Prerelease) { $relArgs += '--prerelease' }
if ($Draft) { $relArgs += '--draft' }
& $gh @relArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (the tag $Tag is already pushed; fix and run: gh release create $Tag ...)" }

Write-Host ""
Write-Host "OK: $Tag published." -ForegroundColor Green
& $gh release view $Tag --json url --jq .url
