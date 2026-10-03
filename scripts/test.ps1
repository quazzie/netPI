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

  Each runner writes its run to a result file (NETPI_TEST_RESULT), and this script reads that file, not the
  console lines: per-test outcomes, the skip count, and the time the runner itself claims. A suite that dies
  before writing one is a crash, and the run says so. Run a suite directly with the same contract:
  NETPI_TEST_RESULT=out.json dotnet tests/NetPI.<X>.Tests/bin/Release/NetPI.<X>.Tests.dll, and split one suite
  across machines with --shard i/n (1-based parts of the selected tests, in registration order).

  The summary reports, per suite, the process time next to the time the tests themselves claim. The
  difference is not noise: it is what the runner spent waiting for output that never arrived, which is
  how a surviving child process shows up. Anything over a second is called out.

  A test that could not run here (no git, no browser, nothing built) reports itself as skipped: the runners
  count those apart from the passes and this script prints how many there were. A skip is not a failure - it
  says what was missing - but it is never read as a pass either. A filter that matches no test at all is a
  broken selection and fails the run; a filter that matches nothing in one suite while another suite runs it
  is normal, and the re-run command names the suites that failed.

.EXAMPLE
  .\scripts\test.ps1                                     # build, run all six suites, print a re-run command
  .\scripts\test.ps1 -Suite Aux,Host                       # two suites (a [string[]] takes a comma, not a second -Suite)
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
    [ValidateRange(1, 5)][int]$Parallel = 3
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
        # The output goes to the log (and its tail is printed when the build fails): a build that failed with
        # "exit 1" and no compiler error is the least helpful report there is.
        $errFile = Join-Path $slnDir "build-$runId.err"
        $buildOut = @(dotnet build $sln -c $Config -p:ShouldUnsetParentConfigurationAndPlatform=false -v q 2> $errFile)
        $code = $LASTEXITCODE
        $sw.Stop()
        $buildOut += @(Get-Content $errFile -ErrorAction SilentlyContinue)
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
        $buildOut | Add-Content $log
        $build = [ordered]@{ seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); built = $true; exitCode = $code }
        if ($code) {
            Write-Host "the test build failed (exit $code) - the last of its output:" -ForegroundColor Red
            $buildOut | Select-Object -Last 25 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            throw "build of the test graph failed (exit $code)"
        }
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

    # Longest first: with a fixed worker count that keeps the tail from being one long suite. Order by the
    # previous run's wall time (the newest run json in artifacts\testlogs) so the queue tracks what actually
    # took long; when there is no log yet (or a suite is new), fall back to the known relative costs.
    $prevWall = @{}
    $runJson = Get-ChildItem -Path $logDir -Filter '*.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.result.json' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($runJson) {
        try {
            $prev = Get-Content -LiteralPath $runJson.FullName -Raw | ConvertFrom-Json
            foreach ($su in $prev.suites) { $prevWall[$su.suite] = [double]$su.wallSeconds }
        } catch { $prevWall = @{} }
    }
    $fallback = @{ Tools = 3; Aux = 3; Agent = 2; Host = 2; Storage = 1; Providers = 1 }
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($s in ($suites | Sort-Object { $prevWall[$s] ?? $fallback[$s] } -Descending)) { $queue.Enqueue($s) }

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
            $resultFile = Join-Path $logDir "$stamp-$s.result.json"
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $job = Start-Job -ScriptBlock {
                param($dll, $only, $appDir, $testRoot, $root, $resultFile)
                # A job starts in the user's profile folder, not where this script was invoked from, so the suite's
                # relative paths (and the repo it reads) need the repository root set explicitly.
                Set-Location $root
                $env:NETPI_APP_DIR = $appDir
                $env:NETPI_TEST_ROOT = $testRoot
                # The runner writes its run there (JSON); the script below reads that, not the console lines.
                $env:NETPI_TEST_RESULT = $resultFile
                $out = & dotnet $dll @only 2>&1 | ForEach-Object { "$_" }
                [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
            } -ArgumentList (Join-Path $repoRoot "tests\NetPI.$s.Tests\bin\$Config\NetPI.$s.Tests.dll"), $Only, $env:NETPI_APP_DIR, $env:NETPI_TEST_ROOT, $repoRoot, $resultFile
            # The result file travels with the job: with -Parallel the $resultFile above is already the next
            # suite's by the time this one finishes, so the post-run block must not read the loop variable.
            $running[$s] = @{ Job = $job; Sw = $sw; ResultFile = $resultFile }
        }
        $done = @($running.Keys | Where-Object { $running[$_].Job.State -ne 'Running' })
        if ($done.Count -eq 0) { Start-Sleep -Milliseconds 50; continue }
        foreach ($s in $done) {
            $entry = $running[$s]
            $entry.Sw.Stop()
            $resultFile = $entry.ResultFile
            $out = @(); $code = 0
            try { $r = Receive-Job $entry.Job -ErrorAction Stop; $out = @($r.Out); $code = [int]$r.Code }
            catch { $out = @("the suite process could not be run: $_") }
            Remove-Job $entry.Job -Force -ErrorAction SilentlyContinue
            $running.Remove($s); $pending--

            "== NetPI.$s.Tests (exit $code, $([math]::Round($entry.Sw.Elapsed.TotalSeconds, 1))s) ==" | Add-Content $log
            $out | Add-Content $log

            # The runner's result file is the authority: per-test outcomes, the skip count, and the time the
            # runner itself reports — the process-gap column used to be blind to the providers suite, whose
            # check-style summary line carried no body time. A suite that dies before writing one (a crash,
            # a killed process) falls through to the exit-code check below.
            $result = $null
            if (Test-Path $resultFile) {
                try { $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json } catch { $result = $null }
            }
            $wall = [math]::Round($entry.Sw.Elapsed.TotalSeconds, 2)
            if ($result) {
                $bodies = [double]$result.bodySeconds
                $skipped = [int]$result.skipped
                $noMatch = [bool]$result.matchedNothing
                $failedTests = @($result.tests | Where-Object { $_.result -eq 'failed' } | ForEach-Object { $_.name })
                $summary = "$([int]$result.passed) passed, $([int]$result.failed) failed, $skipped skipped, $([int]$result.total) total in $bodies s"
            }
            else {
                $bodies = $null; $skipped = 0; $failedTests = @(); $summary = 'no result file (the runner died before reporting)'
                # A filter that matched nothing is a broken selection, not a pass; without the file the
                # console line is all there is to tell them apart.
                $noMatch = @($out | Where-Object { $_ -match 'No test matches the filter' }).Count -gt 0
            }
            $overhead = if ($null -ne $bodies) { [math]::Round($wall - $bodies, 2) } else { $null }
            $results.Add([ordered]@{
                    suite = $s; exitCode = $code; wallSeconds = $wall; reportedBodySeconds = $bodies
                    outsideTestTimersSeconds = $overhead; matchedNothing = $noMatch; skipped = $skipped
                    summary = $summary
                })

            foreach ($f in $failedTests) { $failures.Add([pscustomobject]@{ Suite = $s; Name = $f }) }
            # The exit code is the authority. A suite that dies part-way through — reported as green
            # is how a broken run hides. A run that matched nothing is already reported above
            # (the runner exits 2 for it), so it is not a crash here.
            if ($code -ne 0 -and $failedTests.Count -eq 0 -and -not $noMatch) {
                $failures.Add([pscustomobject]@{ Suite = $s; Name = "(suite process exited $code without reporting a failure - crash?)" })
            }
            elseif ($code -eq 0 -and -not $result -and -not $noMatch) {
                $failures.Add([pscustomobject]@{ Suite = $s; Name = "(suite exited 0 but wrote no result file)" })
            }
        }
    }
    $env:NETPI_TEST_ROOT = $previousTestRoot

    # ---------------------------------------------------------------- report
    $total = [math]::Round(($results | ForEach-Object { $_.wallSeconds } | Measure-Object -Sum).Sum, 2)
    # A filter that matched nothing anywhere is a broken selection. One that matched nothing in a single suite while
    # another ran it is the normal "-Only <name of a test in another suite>" case and is not a failure: the suite said
    # so in its own output, and it is the line in the table below that says it again.
    $emptySuites = @($results | Where-Object { $_.matchedNothing } | ForEach-Object { $_.suite })
    if ($results.Count -gt 0 -and $emptySuites.Count -eq $results.Count) {
        $failures.Add([pscustomobject]@{ Suite = ($emptySuites -join '+'); Name = "(no test matched the filter in any suite)" })
    }
    # Start-up and log collection cost about a second per suite; only a real stall is worth reporting.
    $slow = @($results | Where-Object { $_.outsideTestTimersSeconds -and $_.outsideTestTimersSeconds -gt 3.0 })
    Write-Host ''
    $results | Sort-Object -Property wallSeconds -Descending | ForEach-Object {
        $extra = if ($null -ne $_.outsideTestTimersSeconds) { " (+{0:0.0}s outside the tests)" -f $_.outsideTestTimersSeconds } else { '' }
        if ($_.matchedNothing) { $extra += ' (nothing matched the filter here)' }
        Write-Host ("{0,-10} {1}{2}  ({3:0.0}s)" -f $_.suite, $_.summary, $extra, $_.wallSeconds)
    }
    Write-Host ("{0,-10} {1}  (build {2:0.0}s)" -f 'TOTAL', "$($results.Count) suites", $build.seconds)

    # Skips are counted, not failed on: a test that could not run here (no git, no browser, nothing built) says so
    # instead of quietly passing, and the number is here so a suite that quietly skips half of itself is visible.
    $skippedSuites = @($results | Where-Object { $_.skipped } | ForEach-Object { "{0} {1}" -f $_.suite, $_.skipped })
    if ($skippedSuites.Count) {
        Write-Host ''
        Write-Host ("Skipped (something was missing: git, a browser, a build): {0}" -f ($skippedSuites -join ', ')) -ForegroundColor DarkYellow
    }

    if ($slow) {
        Write-Host ''
        Write-Host "A suite spent this long outside its own test timers, i.e. waiting for something to finish:" -ForegroundColor Yellow
        foreach ($r in $slow) { Write-Host ("  {0}: {1:0.0}s  (a child process outliving its command keeps the output pipe open)" -f $r.suite, $r.outsideTestTimersSeconds) }
    }

    [ordered]@{
        run = $runId; config = $Config; only = $Only; build = $build
        suitesWallSeconds = $total; suites = $results
        skippedTests = ($results | ForEach-Object { $_.skipped } | Measure-Object -Sum).Sum
        failures = @($failures | ForEach-Object { [ordered]@{ suite = $_.Suite; name = $_.Name } })
    } | ConvertTo-Json -Depth 6 | Set-Content $json

    if (-not $failures.Count) {
        Write-Host "`nAll green. Log: $log`nTimings: $json" -ForegroundColor Green
        exit 0
    }

    Write-Host ''
    foreach ($f in $failures) { Write-Host ("  [{0}] {1}" -f $f.Suite, $f.Name) -ForegroundColor Red }
    # The re-run command: the same names as filters, so a fix is checked against the failures alone, and the suites
    # that failed, so it does not run the other four into a "no test matched the filter" (which is not a failure
    # here, but only because a fix loop should not need to know that).
    # One -Only with an array of arguments — repeating -Only would only keep the last one.
    # Only real test names make a usable filter: the "(suite process exited ...)" and "(no test matched ...)"
    # entries are the script's own remarks about the run.
    $realFailures = @($failures | Where-Object { -not $_.Name.StartsWith('(') })
    $quoted = ($realFailures | Select-Object -ExpandProperty Name -Unique |
        ForEach-Object { '"{0}"' -f ($_ -replace '"', '""') }) -join ' '
    $suiteArgs = (@($realFailures | ForEach-Object { $_.Suite }) | Sort-Object -Unique) -join ','
    $suiteArgs = if ($suiteArgs) { " -Suite $suiteArgs" } else { '' }
    Write-Host "`nRe-run just these:" -ForegroundColor Yellow
    if ($quoted) { Write-Host "  .\scripts\test.ps1$suiteArgs -Only $quoted" } else { Write-Host "  (nothing to re-run: the run itself failed)" }
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
