<#
.SYNOPSIS
  Run the unit suites once, then re-run only what failed.

.DESCRIPTION
  The whole set takes about 90 s, so it is worth running once and then not again: this script runs the suites (or the
  ones you name), keeps the full output in artifacts\testlogs, and finishes by printing the failing test names as a
  ready-to-paste -Only command. A fix is then verified against those tests alone (seconds), and the big run happens
  again at the end, before a merge.

  Filters are case-insensitive substrings of a test name, OR-ed within a suite, exactly as the runners take them.
  Without -Only every test of every suite runs. The suites are built first (the test project builds the plugins it
  needs into artifacts\dev\app, which is also where the suites load them from); -SkipBuild reuses what is there.

.EXAMPLE
  .\scripts\test.ps1                                     # build, run all five suites, print a re-run command
  .\scripts\test.ps1 -Suite Aux -Suite Host              # two suites
  .\scripts\test.ps1 -Only "settings:", "goal:"          # only tests whose name contains these
  .\scripts\test.ps1 -Only "a failed write" -SkipBuild   # the fix loop: no rebuild if nothing changed
#>
param(
    [string[]]$Only,
    [string[]]$Suite,
    [string]$Config = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$suites = if ($Suite) { $Suite } else { @('Providers', 'Tools', 'Agent', 'Aux', 'Host') }
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $logDir = 'artifacts\testlogs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log = Join-Path $logDir ("{0:yyyy-MM-dd-HHmmss}.txt" -f (Get-Date))
    $scope = if ($Only) { ", only: $($Only -join ' | ')" } else { '' }

    if (-not $SkipBuild) {
        foreach ($s in $suites) {
            Write-Host "building NetPI.$s.Tests" -ForegroundColor DarkGray
            dotnet build "tests\NetPI.$s.Tests\NetPI.$s.Tests.csproj" -c $Config -v q | Out-Null
            if ($LASTEXITCODE) { throw "build of NetPI.$s.Tests failed" }
        }
    }

    # the suites load the built plugins from here (NETPI_APP_DIR)
    $env:NETPI_APP_DIR = Join-Path (Get-Location) 'artifacts\dev\app'
    $failures = New-Object System.Collections.Generic.List[object]
    $summary = New-Object System.Collections.Generic.List[string]
    "NetPI unit tests, $(Get-Date -Format s) ($Config$scope)" | Set-Content $log

    foreach ($s in $suites) {
        $dll = "tests\NetPI.$s.Tests\bin\$Config\NetPI.$s.Tests.dll"
        if (-not (Test-Path $dll)) { throw "$dll is missing: build it first (drop -SkipBuild)" }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $out = & dotnet $dll @Only 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        $sw.Stop()
        "== NetPI.$s.Tests ==" | Add-Content $log
        $out | Add-Content $log
        $line = @($out | Where-Object { $_ -match '^(\d+ passed, \d+ failed|Tests: )' })[-1]
        if (-not $line) { $line = 'no output' }
        $summary.Add(("{0,-10} {1}  ({2:0.0}s)" -f $s, $line, $sw.Elapsed.TotalSeconds))
        foreach ($f in @($out | Where-Object { $_ -match '^\s*FAIL\s{2}' })) {
            $name = ($f -replace '^\s*FAIL\s{2}', '') -replace '\s+\(\d+ms\)$', ''
            $failures.Add([pscustomobject]@{ Suite = $s; Name = $name })
        }
    }
    Remove-Item Env:NETPI_APP_DIR -ErrorAction SilentlyContinue

    Write-Host ''
    $summary | ForEach-Object { Write-Host $_ }
    if (-not $failures.Count) {
        Write-Host "`nAll green. Log: $log" -ForegroundColor Green
        exit 0
    }

    Write-Host ''
    foreach ($f in $failures) { Write-Host ("  [{0}] {1}" -f $f.Suite, $f.Name) -ForegroundColor Red }
    # the re-run command: the same names as filters, so a fix is checked against the failures alone
    $filters = ($failures | Select-Object -ExpandProperty Name -Unique | ForEach-Object { '"{0}"' -f ($_ -replace '"', '""') }) -join ' -Only '
    Write-Host "`nRe-run just these:" -ForegroundColor Yellow
    Write-Host "  .\scripts\test.ps1 $filters"
    Write-Host "Full log: $log"
    exit 1
}
finally { Pop-Location }
