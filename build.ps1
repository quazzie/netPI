<#
.SYNOPSIS
  Build NetPI on Windows.

.DESCRIPTION
  1. (optional) builds the web UI and plugin tab bundles with npm — prebuilt bundles are committed,
     so Node.js is only needed when you change the UI;
  2. builds the solution into artifacts\app (NetPI.exe desktop app, netpi-server.exe, plugins\, wwwroot\).
     While NetPI runs from artifacts\app, it builds into artifacts\build\stage first (a failed build changes nothing)
     and then puts the result in place: changed plugins hot-reload; each host file it replaces is moved into
     artifacts\app\.old (a running exe or DLL can be renamed, not overwritten), so the next start of NetPI.exe runs the
     new host. When the contracts changed, the rebuilt plugins wait in artifacts\app\.pending instead (the running
     NetPI would load them onto its old contracts) and that next start installs them.

.EXAMPLE
  .\build.ps1              # build everything (Release)
  .\build.ps1 -Run         # build and start the desktop app
  .\build.ps1 -SkipWeb     # don't run npm even if it is installed
  .\build.ps1 -Test        # build and run the unit test suites
  From cmd: build.cmd runs this script with the same options (build -Run, build /?).
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipWeb,
    [switch] $Run,
    [switch] $Test
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# ---- .NET SDK
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}
if (-not (dotnet --list-sdks | Where-Object { $_ -match '^1\d\.' -and [int]($_.Split('.')[0]) -ge 10 })) {
    throw '.NET 10 SDK (or newer) not found. `dotnet --list-sdks` lists what is installed.'
}

# ---- web UI (optional)
if (-not $SkipWeb) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        Step 'Web UI (npm)'
        if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } }
        npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' }
    }
    else {
        Write-Host 'npm not found: using the prebuilt web UI (web\dist and plugins\*\wwwroot\ui.js).' -ForegroundColor Yellow
    }
}

# ---- is NetPI running from artifacts\app? A running NetPI keeps its host DLLs open (plugins load from shadow copies).
# A build made while it ran moved the files it replaced into .old, so the contracts a running NetPI uses are the open
# NetPI.Abstractions.dll, here or in .old: their hashes, taken before this build moves anything.
function Test-FileLocked([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try { [IO.File]::Open($path, 'Open', 'ReadWrite', 'None').Dispose(); return $false } catch { return $true }
}
$app = Join-Path $PSScriptRoot 'artifacts\app'
$old = Join-Path $app '.old'
$pending = Join-Path $app '.pending'
$contracts = @(Join-Path $app 'NetPI.Abstractions.dll')
if (Test-Path $old) { $contracts += @(Get-ChildItem $old -Recurse -File -Filter 'NetPI.Abstractions.dll' | ForEach-Object { $_.FullName }) }
$runningContracts = @($contracts | Where-Object { Test-FileLocked $_ } | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
$running = $runningContracts.Count -gt 0

# what earlier builds moved aside is free once the NetPI that used it has exited; what is left, a running NetPI uses
if (Test-Path $old) {
    Get-ChildItem $old -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not (Get-ChildItem $old -Recurse -File -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }
}
$oldHostRunning = (Test-Path $old) -and [bool](Get-ChildItem $old -Recurse -File -Force -ErrorAction SilentlyContinue)

# ---- plugins that were renamed (2026-09-25: NetPI.Lanes is NetPI.Agents, NetPI.Agent is NetPI.Runtime): their old
# output would load next to the new one. A running app keeps them until it is closed and built again.
if (-not $running) {
    foreach ($name in 'NetPI.Lanes', 'NetPI.Agent') {
        $dir = Join-Path $app "plugins\$name"
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            Write-Host "Removed the old plugin output $dir" -ForegroundColor Yellow
        }
    }
}

# Files under $from, relative to it, that are missing under $to or differ (a deterministic build rewrites unchanged
# outputs byte for byte, so the hash tells what really changed). $skip: top-level folders to leave out.
function Get-Changed([string] $from, [string] $to, [string[]] $skip = @()) {
    if (-not (Test-Path $from)) { return }
    $root = (Resolve-Path $from).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem $from -Recurse -File -Force) {
        $rel = $f.FullName.Substring($root.Length)
        if ($skip | Where-Object { $rel -like "$_\*" }) { continue }
        $dest = Join-Path $to $rel
        if (-not (Test-Path -LiteralPath $dest) -or (Get-FileHash -LiteralPath $dest).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash) { $rel }
    }
}

function Copy-Rel([string] $from, [string] $to, [string] $rel) {
    $dest = Join-Path $to $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item -LiteralPath (Join-Path $from $rel) -Destination $dest -Force
}

# ---- .NET
Step "dotnet build ($Configuration)"
$restart = @()
if (-not $running) {
    # a full build replaces what a build made while NetPI ran left for its next start
    if (Test-Path $pending) { Remove-Item -Recurse -Force $pending }
    dotnet build NetPI.slnx -c $Configuration --nologo
    if ($LASTEXITCODE) { throw 'dotnet build failed' }
}
else {
    Write-Host 'NetPI is running from artifacts\app: building into artifacts\build\stage first; artifacts\app changes only if that succeeds.' -ForegroundColor Yellow
    $stage = Join-Path $PSScriptRoot 'artifacts\build\stage'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage } # nothing stale (a removed plugin) comes back from it
    dotnet build NetPI.slnx -c $Configuration --nologo "-p:AppOutDir=$stage/"
    if ($LASTEXITCODE) { throw 'dotnet build failed: artifacts\app is unchanged' }

    # host: each changed file is moved into .old (the running NetPI keeps using it) and the new one copied in. All or
    # nothing: if a file can't be moved, the ones already moved go back. Symbols in use stay (they only give line numbers).
    $hostFiles = @(Get-Changed $stage $app 'plugins', 'wwwroot')
    $aside = Join-Path $old (Get-Date -Format 'yyyyMMdd-HHmmss')
    $moved = @()
    $keptSymbols = @()
    foreach ($rel in $hostFiles) {
        $dest = Join-Path $app $rel
        if (-not (Test-Path -LiteralPath $dest)) { continue }
        $to = Join-Path $aside $rel
        New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
        try {
            Move-Item -LiteralPath $dest -Destination $to -ErrorAction Stop
            $moved += $rel
        }
        catch {
            if ($rel -like '*.pdb') { $keptSymbols += $rel; continue }
            foreach ($m in $moved) { Move-Item -LiteralPath (Join-Path $aside $m) -Destination (Join-Path $app $m) }
            throw "Could not replace $rel while NetPI runs ($($_.Exception.Message)): artifacts\app is unchanged. Close NetPI and build again."
        }
    }
    foreach ($rel in $hostFiles) { if ($keptSymbols -notcontains $rel) { Copy-Rel $stage $app $rel } }
    $restart += @($hostFiles | Where-Object { $_ -match '\.(dll|exe|json)$' })

    # web UI: replaced as a whole, like a normal build does (a file being served may stay; it is overwritten then)
    $web = Join-Path $app 'wwwroot'
    if (@(Get-Changed (Join-Path $stage 'wwwroot') $web).Count) {
        Remove-Item -LiteralPath $web -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $web | Out-Null
        Copy-Item -Path (Join-Path $stage 'wwwroot\*') -Destination $web -Recurse -Force
    }

    # plugins: changed files go into place and the running NetPI reloads those plugins; built against contracts it
    # doesn't have (then every plugin's output changes), they wait in .pending for its next start instead
    $stagePlugins = Join-Path $stage 'plugins'
    $pluginFiles = @(Get-Changed $stagePlugins (Join-Path $app 'plugins'))
    $pluginNames = @($pluginFiles | ForEach-Object { $_.Split('\')[0] } | Sort-Object -Unique)
    $builtContracts = (Get-FileHash (Join-Path $stage 'NetPI.Abstractions.dll')).Hash
    $newContracts = @($runningContracts | Where-Object { $_ -ne $builtContracts }).Count -gt 0
    if (Test-Path $pending) { Remove-Item -Recurse -Force $pending } # this build supersedes it
    if ($newContracts) {
        foreach ($name in $pluginNames) {
            New-Item -ItemType Directory -Force (Join-Path $pending 'plugins') | Out-Null
            Copy-Item -Recurse (Join-Path $stagePlugins $name) (Join-Path $pending "plugins\$name")
        }
    }
    else {
        foreach ($rel in $pluginFiles) { Copy-Rel $stagePlugins (Join-Path $app 'plugins') $rel }
    }

    Write-Host ''
    if ($pluginNames.Count -and -not $newContracts) {
        Write-Host "Plugins updated in place (the running NetPI reloads them): $($pluginNames -join ', ')" -ForegroundColor Green
    }
    if ($newContracts) {
        Write-Host "The contracts changed: the running NetPI keeps its plugins, and the $($pluginNames.Count) rebuilt ones wait in artifacts\app\.pending until it starts again." -ForegroundColor Yellow
    }
    if ($keptSymbols.Count) {
        Write-Host "Symbols in use were kept: $($keptSymbols -join ', ') (stack traces lack line numbers until a build with NetPI closed)." -ForegroundColor DarkYellow
    }
    if ($restart.Count -or $newContracts -or $oldHostRunning) {
        $what = if ($restart.Count) { ": $($restart -join ', ')" } else { '' }
        Write-Host "The new host is ready$what. Close NetPI and start it again (artifacts\app\NetPI.exe) to use it." -ForegroundColor Yellow
    }
    elseif (-not $pluginNames.Count) {
        Write-Host 'Nothing changed for the running NetPI.'
    }
}

Write-Host "`nBuilt artifacts\app:" -ForegroundColor Green
Write-Host '  NetPI.exe          desktop app (WebView2 window, starts its own server)'
Write-Host '  netpi-server.exe   headless server (open the printed URL in a browser)'

if ($Test) {
    Step 'Unit tests'
    $failed = 0
    foreach ($t in 'Providers', 'Tools', 'Agent', 'Aux', 'Host') {
        $dll = "tests\NetPI.$t.Tests\bin\$Configuration\NetPI.$t.Tests.dll"
        Write-Host "-- $t"
        dotnet $dll
        if ($LASTEXITCODE) { $failed++ }
    }
    if ($failed) { throw "$failed test suite(s) failed" }
}

if ($Run) {
    if ($running) { Write-Host 'NetPI is already running.' -ForegroundColor Yellow }
    else { Start-Process (Join-Path $PSScriptRoot 'artifacts\app\NetPI.exe') }
}
