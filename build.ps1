<#
.SYNOPSIS
  Build NetPI on Windows.

.DESCRIPTION
  1. (optional) builds the web UI and plugin tab bundles with npm — prebuilt bundles are committed,
     so Node.js is only needed when you change the UI;
  2. builds the solution into artifacts\app (NetPI.exe desktop app, netpi-server.exe, plugins\, wwwroot\).

.EXAMPLE
  .\build.ps1              # build everything (Release)
  .\build.ps1 -Run         # build and start the desktop app
  .\build.ps1 -SkipWeb     # don't run npm even if it is installed
  .\build.ps1 -Test        # build and run the unit test suites
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

# ---- is NetPI running? (its DLLs would be locked)
if (Get-Process -Name NetPI, netpi-server -ErrorAction SilentlyContinue) {
    Write-Host 'NetPI is running: close it first (plugins alone can be rebuilt while it runs: dotnet build plugins\<Name>).' -ForegroundColor Yellow
}

# ---- .NET
Step "dotnet build ($Configuration)"
dotnet build NetPI.slnx -c $Configuration --nologo
if ($LASTEXITCODE) { throw 'dotnet build failed' }

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

if ($Run) { Start-Process (Join-Path $PSScriptRoot 'artifacts\app\NetPI.exe') }
