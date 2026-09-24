<#
.SYNOPSIS
  Build NetPI on Windows.

.DESCRIPTION
  1. (optional) builds the web UI and plugin tab bundles with npm — prebuilt bundles are committed,
     so Node.js is only needed when you change the UI;
  2. builds the solution into artifacts\app (NetPI.exe desktop app, netpi-server.exe, plugins\, wwwroot\).
     While NetPI runs from artifacts\app its host DLLs are locked: then everything except netpi-server and the
     desktop shell is built, the plugins hot-reload, and host changes wait for the next build with NetPI closed.

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

# ---- is NetPI running from artifacts\app? A running app keeps its host DLLs open (plugins load from shadow copies).
function Test-FileLocked([string] $path) {
    if (-not (Test-Path $path)) { return $false }
    try { [IO.File]::Open($path, 'Open', 'ReadWrite', 'None').Dispose(); return $false } catch { return $true }
}
$app = Join-Path $PSScriptRoot 'artifacts\app'
$running = Test-FileLocked (Join-Path $app 'NetPI.Host.dll')

# ---- .NET
Step "dotnet build ($Configuration)"
if ($running) {
    Write-Host 'NetPI is running from artifacts\app: building everything except netpi-server and the desktop shell (plugins hot-reload).' -ForegroundColor Yellow
    # the solution minus the two projects that write the host DLLs into artifacts\app
    $hostProjects = 'src/NetPI.Server/NetPI.Server.csproj', 'src/NetPI.Desktop/NetPI.Desktop.csproj'
    $projects = @(([xml](Get-Content NetPI.slnx -Raw)).SelectNodes('//Project') | ForEach-Object { $_.Path } |
        Where-Object { $hostProjects -notcontains $_ } | ForEach-Object { $_ -replace '/', '\' })
    $filter = Join-Path $PSScriptRoot 'artifacts\build\running.slnf'
    New-Item -ItemType Directory -Force (Split-Path $filter) | Out-Null
    @{ solution = @{ path = '..\..\NetPI.slnx'; projects = $projects } } | ConvertTo-Json -Depth 4 | Set-Content $filter -Encoding utf8
    dotnet build $filter -c $Configuration --nologo
    if ($LASTEXITCODE) { throw 'dotnet build failed' }
    # the running app keeps its kernel and contracts until it restarts
    $stale = @(foreach ($n in 'NetPI.Abstractions', 'NetPI.Host') {
        $built = Join-Path $PSScriptRoot "src\$n\bin\$Configuration\$n.dll"
        $inApp = Join-Path $app "$n.dll"
        if ((Test-Path $built) -and (Get-FileHash $built).Hash -ne (Get-FileHash $inApp).Hash) { $n }
    })
    if ($stale.Count) {
        Write-Host "$($stale -join ' and ') changed: close NetPI and build again (build.ps1 or build.cmd) to update it (until then, plugins that use new contracts may fail to load)." -ForegroundColor Yellow
    }
}
else {
    dotnet build NetPI.slnx -c $Configuration --nologo
    if ($LASTEXITCODE) { throw 'dotnet build failed' }
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
