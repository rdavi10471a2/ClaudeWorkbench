<#
.SYNOPSIS
    Publishes a ClaudeWorkbench "live" install: the Blazor host, the Node sidecar and the
    Launcher, side by side in one folder, plus a shortcut to the Launcher.

.DESCRIPTION
    Produces this layout, which the Launcher recognises as a workbench root:

        <Destination>\
            host\      ClaudeWorkbench.Host.exe (the Blazor app) + its config\
            sidecar\   dist\index.js + production node_modules
            launcher\  ClaudeWorkbench.Launcher.exe
            runtime\   created on first run: one folder per workspace

    The Launcher finds the host next to itself (<root>\host), the sidecar at <root>\sidecar,
    and provisions every instance into <root>\runtime\<workspace>. So this install works
    wherever it is put, and does not depend on the source checkout it was built from.

    runtime\ is never touched by this script: re-publishing over an existing install keeps
    the workspaces and indexes that are already there.

    ASCII only, on purpose: Windows PowerShell 5.1 reads this file as ANSI and mangles any
    non-ASCII punctuation into a parse error.

.PARAMETER Destination
    Where to publish. Defaults to C:\ClaudeWorkBenchLive.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoShortcut
    Skip creating the desktop shortcut (one is still written into the install folder).

.PARAMETER Clean
    Remove the host\, sidecar\ and launcher\ folders first. runtime\ is preserved.

.EXAMPLE
    .\scripts\publish-live.ps1
    .\scripts\publish-live.ps1 -Destination D:\Workbench -Clean
#>
[CmdletBinding()]
param(
    [string]$Destination = 'C:\ClaudeWorkBenchLive',
    [string]$Configuration = 'Release',
    [switch]$NoShortcut,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# Break the recursion: this script runs `dotnet publish -c Release`, which builds the same
# projects whose Release build triggers this script (see Directory.Build.targets). The child
# builds inherit this variable and skip the post-build publish target.
$env:CWB_SKIP_PUBLISH_LIVE = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src\ClaudeWorkbench.Host\ClaudeWorkbench.Host.csproj'
$launcherProject = Join-Path $repoRoot 'src\ClaudeWorkbench.Launcher\ClaudeWorkbench.Launcher.csproj'
$sidecarSource = Join-Path $repoRoot 'sidecar'

foreach ($required in @($hostProject, $launcherProject, $sidecarSource)) {
    if (-not (Test-Path $required)) {
        throw "Not a ClaudeWorkbench checkout - missing $required"
    }
}

$hostOut = Join-Path $Destination 'host'
$sidecarOut = Join-Path $Destination 'sidecar'
$launcherOut = Join-Path $Destination 'launcher'

# A running install holds its exes open and publish fails partway through with an unhelpful
# MSBuild error. Say so up front instead.
$inUse = Get-Process -Name 'ClaudeWorkbench.Launcher', 'ClaudeWorkbench.Host' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
if ($inUse) {
    $names = ($inUse | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    throw "Close the running install first - $names is using $Destination."
}

Write-Host "Publishing ClaudeWorkbench ($Configuration) -> $Destination" -ForegroundColor Cyan

if ($Clean) {
    # Deliberately only the three build outputs: runtime\ holds the user's instance state.
    foreach ($stale in @($hostOut, $sidecarOut, $launcherOut)) {
        if (Test-Path $stale) {
            Write-Host "  cleaning $stale"
            Remove-Item $stale -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# --- 1. Blazor host -------------------------------------------------------------------
Write-Host ''
Write-Host '[1/4] Publishing host (Blazor + MCP surface)...' -ForegroundColor Cyan
dotnet publish $hostProject -c $Configuration -o $hostOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)." }

# The mutable watched-solution config must not ship: each instance gets its own, written by
# the Launcher. Shipping one would point every fresh install at the build machine's workspace.
$strayConfig = Join-Path $hostOut 'config\appsettings.json'
if (Test-Path $strayConfig) {
    Remove-Item $strayConfig -Force
    Write-Host '  removed build-machine config\appsettings.json (instances get their own)'
}

# --- 2. Launcher ----------------------------------------------------------------------
Write-Host ''
Write-Host '[2/4] Publishing launcher...' -ForegroundColor Cyan
dotnet publish $launcherProject -c $Configuration -o $launcherOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($LASTEXITCODE)." }

# --- 3. Sidecar -----------------------------------------------------------------------
Write-Host ''
Write-Host '[3/4] Building sidecar...' -ForegroundColor Cyan
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) { $npm = Get-Command npm -ErrorAction SilentlyContinue }
if (-not $npm) { throw 'npm was not found on PATH - needed to build the sidecar.' }

Push-Location $sidecarSource
try {
    if (-not (Test-Path (Join-Path $sidecarSource 'node_modules'))) {
        & $npm.Source install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)." }
    }

    & $npm.Source run build
    if ($LASTEXITCODE -ne 0) { throw "Sidecar build failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $sidecarOut | Out-Null
Copy-Item (Join-Path $sidecarSource 'dist') $sidecarOut -Recurse -Force
Copy-Item (Join-Path $sidecarSource 'package.json') $sidecarOut -Force
$lockFile = Join-Path $sidecarSource 'package-lock.json'
if (Test-Path $lockFile) { Copy-Item $lockFile $sidecarOut -Force }

# Runtime dependencies only (the Agent SDK + express). Falls back to copying the checkout's
# node_modules when npm cannot reach the registry, so an offline publish still works.
Write-Host '  installing production dependencies...'
Push-Location $sidecarOut
try {
    if (Test-Path $lockFile) { & $npm.Source ci --omit=dev } else { & $npm.Source install --omit=dev }
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $sidecarOut 'node_modules'))) {
    Write-Warning 'npm install failed (offline?) - copying the checkout node_modules instead.'
    Copy-Item (Join-Path $sidecarSource 'node_modules') $sidecarOut -Recurse -Force
}

if (-not (Test-Path (Join-Path $sidecarOut 'dist\index.js'))) {
    throw "Sidecar publish incomplete: $sidecarOut\dist\index.js is missing."
}

# --- 3b. Sample workspace -------------------------------------------------------------
# A small watched solution so a fresh install has something to open on first run. It goes in
# samples\, NOT runtime\ - runtime\ is disposable per-workspace state and gets cleared.
$sampleSource = Join-Path $repoRoot 'samples\watched-solutions\CalculatorSample'
$sampleOut = Join-Path $Destination 'samples\CalculatorSample'
if (Test-Path $sampleSource) {
    Write-Host '  copying CalculatorSample workspace...'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sampleOut) | Out-Null
    if (Test-Path $sampleOut) { Remove-Item $sampleOut -Recurse -Force }
    Copy-Item $sampleSource $sampleOut -Recurse -Force
    # Never ship the sample's own build output.
    Get-ChildItem $sampleOut -Directory -Recurse -Include 'bin', 'obj' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 4. Shortcuts ---------------------------------------------------------------------
Write-Host ''
Write-Host '[4/4] Creating shortcut...' -ForegroundColor Cyan
$launcherExe = Join-Path $launcherOut 'ClaudeWorkbench.Launcher.exe'
if (-not (Test-Path $launcherExe)) { throw "Launcher exe not found at $launcherExe" }

function New-LauncherShortcut {
    param([string]$LinkPath, [string]$TargetExe, [string]$WorkingDir)

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($LinkPath)
        $shortcut.TargetPath = $TargetExe
        $shortcut.WorkingDirectory = $WorkingDir
        $shortcut.IconLocation = $TargetExe
        $shortcut.Description = 'ClaudeWorkbench Launcher'
        $shortcut.Save()
        Write-Host "  $LinkPath"
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

New-LauncherShortcut -LinkPath (Join-Path $Destination 'ClaudeWorkbench Launcher.lnk') -TargetExe $launcherExe -WorkingDir $launcherOut
if (-not $NoShortcut) {
    $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ClaudeWorkbench Launcher.lnk'
    New-LauncherShortcut -LinkPath $desktopLink -TargetExe $launcherExe -WorkingDir $launcherOut
}

Write-Host ''
Write-Host "Done. Install root: $Destination" -ForegroundColor Green
Write-Host "  host      $hostOut"
Write-Host "  sidecar   $sidecarOut"
Write-Host "  launcher  $launcherOut"
Write-Host "  runtime   $(Join-Path $Destination 'runtime')  (per-workspace state, created on first Start)"
Write-Host ''
Write-Host 'This publish is framework-dependent. A target machine also needs:' -ForegroundColor Yellow
Write-Host '  - .NET 10 SDK       (the runtime to start; MSBuild/Roslyn from the SDK to index)'
Write-Host '  - Node.js on PATH   (the claude CLI itself ships inside the sidecar)'
Write-Host '  - a Claude login in ~\.claude'
