<#
.SYNOPSIS
  Run the unit suites, then re-run only what failed.

.DESCRIPTION
  Builds the selected suites once (one generated solution, so shared dependencies are built once
  instead of once per suite), runs them, keeps the full output in artifacts\testlogs, and finishes by
  printing the failing test names as a ready-to-paste -Only command.

  Filters are case-insensitive substrings of a test name, OR-ed within a suite, exactly as the runners
  take them. Without -Only every test of every suite runs. -SkipBuild reuses what is there.

  Each suite gets its own temporary root (NETPI_TEST_ROOT), so two invocations of the same suite never
  delete each other's files, and the caller's NETPI_APP_DIR is restored on the way out.

  The summary reports, per suite, the process time next to the time the tests themselves claim. The
  difference is not noise: it is what the runner spent waiting for output that never arrived, which is
  how a surviving child process shows up. Anything over a second is called out.

.EXAMPLE
  .\scripts\test.ps1                                     # build, run all six suites, print a re-run command
  .\scripts\test.ps1 -Suite Aux -Suite Host              # two suites
  .\scripts\test.ps1 -Only "settings:", "goal:"          # only tests whose name contains these
  .\scripts\test.ps1 -Only "a failed write" -SkipBuild   # the fix loop: no rebuild if nothing changed
  .\scripts\test.ps1 -Parallel 3                         # run up to 3 suite processes at once
  .\scripts\test.ps1 -Serial                             # one suite at a time (diagnostics)
#>
param(
    [string[]]$Only,
    [string[]]$Suite,
    [string]$Config = 'Release',
    [switch]$SkipBuild,
    [switch]$Serial,
    [ValidateRange(1, 5)][int]$Parallel = 2
)

$ErrorActionPreference = 'Stop'
$suites = if ($Suite) { $Suite } else { @('Providers', 'Tools', 'Agent', 'Aux', 'Host', 'Storage') }
$repoRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
Push-Location $repoRoot
$previousAppDir = $env:NETPI_APP_DIR
$previousTestRoot = $env:NETPI_TEST_ROOT
$runRoots = @()   # this run's temp roots, removed on the way out whatever happens
try {
    $logDir = 'artifacts\testlogs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stamp = "{0:yyyy-MM-dd-HHmmss}" -f (Get-Date)
    $log = Join-Path $logDir "$stamp.txt"
    $json = Join-Path $logDir "$stamp.json"
    $scope = if ($Only) { ", only: $($Only -join ' | ')" } else { '' }
    $runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)

    # the suites load the built plugins from here (NETPI_APP_DIR)
    $env:NETPI_APP_DIR = Join-Path (Get-Location) 'artifacts\dev\app'
    $results = New-Object System.Collections.Generic.List[object]
    $failures = New-Object System.Collections.Generic.List[object]

    "NetPI unit tests, $(Get-Date -Format s) ($Config$scope, run $runId)" | Set-Content $log

    # ---------------------------------------------------------------- build the selected graph once
    $build = [ordered]@{ seconds = 0.0; built = $false }
    if (-not $SkipBuild) {
        $slnDir = 'artifacts\test-speed'
        $sln = Join-Path $slnDir "netpi-tests-$runId.slnx"
        New-Item -ItemType Directory -Path $slnDir -Force | Out-Null
        # A solution holding just the requested suites: one restore, one build, shared dependencies
        # compiled once. Building each project separately re-entered the same graph five times.
        # Project paths in a solution are relative to the solution file, hence the way back up.
        $body = ($suites | ForEach-Object {
                $rel = "tests/NetPI.$_.Tests/NetPI.$_.Tests.csproj"
                "    <Project Path=`"../../$rel`" />"
            }) -join "`n"
        "<Solution>`n$body`n</Solution>" | Set-Content $sln
        Write-Host "building $($suites.Count) suite project(s) in one graph" -ForegroundColor DarkGray
        $sw = [Diagnostics.Stopwatch]::StartNew()
        # Referenced projects outside the generated solution must keep the selected configuration.
        dotnet build $sln -c $Config -p:ShouldUnsetParentConfigurationAndPlatform=false -v q | Out-Null
        $code = $LASTEXITCODE
        $sw.Stop()
        $build = [ordered]@{ seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); built = $true; exitCode = $code }
        if ($code) { throw "build of the test graph failed (exit $code)" }
        Remove-Item $sln -Force -ErrorAction SilentlyContinue
    }
    else {
        foreach ($s in $suites) {
            $dll = "tests\NetPI.$s.Tests\bin\$Config\NetPI.$s.Tests.dll"
            if (-not (Test-Path $dll)) { throw "$dll is missing: build it first (drop -SkipBuild)" }
        }
    }

    # ---------------------------------------------------------------- run
    $parallel = if ($Serial) { 1 } else { [math]::Max(1, [math]::Min($Parallel, $suites.Count)) }
    if ($parallel -gt 1) { Write-Host "running up to $parallel suite processes at once" -ForegroundColor DarkGray }

    # Longest first: with a fixed worker count that keeps the tail from being one long suite.
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($s in ($suites | Sort-Object { $known = @{ Tools = 3; Aux = 3; Agent = 2; Host = 2; Storage = 1; Providers = 1 }[$_] } -Descending)) { $queue.Enqueue($s) }

    $running = @{}
    $pending = $suites.Count
    while ($pending -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $parallel) {
            $s = $queue.Dequeue()
            # A root of its own, so a suite cannot remove another one's files. It lives under the
            # system temp, not the repo: the git tests make real repositories, and a repository
            # created inside this one is a nested repo with different behaviour.
            $env:NETPI_TEST_ROOT = Join-Path ([IO.Path]::GetTempPath()) "netpi-tests-$runId-$s"
            $runRoots += $env:NETPI_TEST_ROOT
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $job = Start-Job -ScriptBlock {
                param($dll, $only, $appDir, $testRoot, $root)
                # A job starts in the user's profile folder, not where this script was invoked from, so the suite's
                # relative paths (and the repo it reads) need the repository root set explicitly.
                Set-Location $root
                $env:NETPI_APP_DIR = $appDir
                $env:NETPI_TEST_ROOT = $testRoot
                $out = & dotnet $dll @only 2>&1 | ForEach-Object { "$_" }
                [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
            } -ArgumentList (Join-Path $repoRoot "tests\NetPI.$s.Tests\bin\$Config\NetPI.$s.Tests.dll"), $Only, $env:NETPI_APP_DIR, $env:NETPI_TEST_ROOT, $repoRoot
            $running[$s] = @{ Job = $job; Sw = $sw }
        }
        $done = @($running.Keys | Where-Object { $running[$_].Job.State -ne 'Running' })
        if ($done.Count -eq 0) { Start-Sleep -Milliseconds 50; continue }
        foreach ($s in $done) {
            $entry = $running[$s]
            $entry.Sw.Stop()
            $out = @(); $code = 0
            try { $r = Receive-Job $entry.Job -ErrorAction Stop; $out = @($r.Out); $code = [int]$r.Code }
            catch { $out = @("the suite process could not be run: $_") }
            Remove-Job $entry.Job -Force -ErrorAction SilentlyContinue
            $running.Remove($s); $pending--

            "== NetPI.$s.Tests (exit $code, $([math]::Round($entry.Sw.Elapsed.TotalSeconds, 1))s) ==" | Add-Content $log
            $out | Add-Content $log

            # Two runners, two formats: the console runners print "12 passed, 0 failed, 59 total in 9.4s",
            # the providers runner prints "Tests: 12 passed, 0 failed. Checks: ...".
            $line = @($out | Where-Object { $_ -match '^\d+ passed, \d+ failed' -or $_ -match '^Tests: ' })[-1]
            $bodies = if ($line -match 'in (\d+([.,]\d+)?)s') { [double]($Matches[1] -replace ',', '.') } else { $null }
            $wall = [math]::Round($entry.Sw.Elapsed.TotalSeconds, 2)
            $overhead = if ($null -ne $bodies) { [math]::Round($wall - $bodies, 2) } else { $null }
            # A filter that matched nothing is a broken selection, not a pass. The console runners say
            # so themselves; a crash before any output would report a zero total too.
            $noMatch = @($out | Where-Object { $_ -match 'No test matches the filter' }).Count -gt 0
            if (-not $noMatch -and $line -and $line -match '^\d+ passed, \d+ failed, 0 total') { $noMatch = $true }

            $results.Add([ordered]@{
                    suite = $s; exitCode = $code; wallSeconds = $wall; reportedBodySeconds = $bodies
                    outsideTestTimersSeconds = $overhead; matchedNothing = $noMatch
                    summary = if ($line) { $line } else { 'no summary line' }
                })

            if ($noMatch) { $failures.Add([pscustomobject]@{ Suite = $s; Name = "(no test matched the filter)" }) }

            $failedLines = @($out | Where-Object { $_ -match '^\s*FAIL\s{2}' })
            foreach ($f in $failedLines) {
                # "(123ms)" and "(123 ms)" are both used, by different runners.
                $name = ($f -replace '^\s*FAIL\s{2}', '') -replace '\s*\(\d+\s*m?s\)\s*$', ''
                $failures.Add([pscustomobject]@{ Suite = $s; Name = $name })
            }
            # The exit code is the authority. A suite that exits non-zero without a FAIL line died
            # part-way through — reporting that as green is how a broken run hides. A run that matched
            # nothing is already reported above (the runner exits 2 for it), so it is not a crash here.
            if ($code -ne 0 -and $failedLines.Count -eq 0 -and -not $noMatch) {
                $failures.Add([pscustomobject]@{ Suite = $s; Name = "(suite process exited $code without reporting a failure - crash?)" })
            }
        }
    }
    $env:NETPI_TEST_ROOT = $previousTestRoot

    # ---------------------------------------------------------------- report
    $total = [math]::Round(($results | ForEach-Object { $_.wallSeconds } | Measure-Object -Sum).Sum, 2)
    # Start-up and log collection cost about a second per suite; only a real stall is worth reporting.
    $slow = @($results | Where-Object { $_.outsideTestTimersSeconds -and $_.outsideTestTimersSeconds -gt 3.0 })
    Write-Host ''
    $results | Sort-Object -Property wallSeconds -Descending | ForEach-Object {
        $extra = if ($null -ne $_.outsideTestTimersSeconds) { " (+{0:0.0}s outside the tests)" -f $_.outsideTestTimersSeconds } else { '' }
        Write-Host ("{0,-10} {1}{2}  ({3:0.0}s)" -f $_.suite, $_.summary, $extra, $_.wallSeconds)
    }
    Write-Host ("{0,-10} {1}  (build {2:0.0}s)" -f 'TOTAL', "$($results.Count) suites", $build.seconds)

    if ($slow) {
        Write-Host ''
        Write-Host "A suite spent this long outside its own test timers, i.e. waiting for something to finish:" -ForegroundColor Yellow
        foreach ($r in $slow) { Write-Host ("  {0}: {1:0.0}s  (a child process outliving its command keeps the output pipe open)" -f $r.suite, $r.outsideTestTimersSeconds) }
    }

    [ordered]@{
        run = $runId; config = $Config; only = $Only; build = $build
        suitesWallSeconds = $total; suites = $results
        failures = @($failures | ForEach-Object { [ordered]@{ suite = $_.Suite; name = $_.Name } })
    } | ConvertTo-Json -Depth 6 | Set-Content $json

    if (-not $failures.Count) {
        Write-Host "`nAll green. Log: $log`nTimings: $json" -ForegroundColor Green
        exit 0
    }

    Write-Host ''
    foreach ($f in $failures) { Write-Host ("  [{0}] {1}" -f $f.Suite, $f.Name) -ForegroundColor Red }
    # The re-run command: the same names as filters, so a fix is checked against the failures alone.
    # One -Only with an array of arguments — repeating -Only would only keep the last one.
    # Only real test names make a usable filter: the "(suite process exited ...)" and
    # "(no test matched ...)" entries are the script's own remarks about the run.
    $quoted = ($failures | Select-Object -ExpandProperty Name -Unique | Where-Object { -not $_.StartsWith('(') } |
        ForEach-Object { '"{0}"' -f ($_ -replace '"', '""') }) -join ' '
    Write-Host "`nRe-run just these:" -ForegroundColor Yellow
    if ($quoted) { Write-Host "  .\scripts\test.ps1 -Only $quoted" } else { Write-Host "  (nothing to re-run: the run itself failed)" }
    Write-Host "Full log: $log"
    exit 1
}
finally {
    # this run's temp roots, and only those: a suite may still have left files locked, so best effort
    foreach ($root in $runRoots) { try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } catch { } }
    # leave the caller's environment as we found it
    if ($null -eq $previousAppDir) { Remove-Item Env:NETPI_APP_DIR -ErrorAction SilentlyContinue } else { $env:NETPI_APP_DIR = $previousAppDir }
    if ($null -eq $previousTestRoot) { Remove-Item Env:NETPI_TEST_ROOT -ErrorAction SilentlyContinue } else { $env:NETPI_TEST_ROOT = $previousTestRoot }
    Pop-Location
}
