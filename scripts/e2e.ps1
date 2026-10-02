<#
.SYNOPSIS
  Run the real-server end-to-end suite the way a development loop needs it: only what the change can affect, in seconds;
  the whole suite (sharded, ~35 s) once, before a merge.

.DESCRIPTION
  Do not run the whole suite to check a fix. Pick what your change can reach:

    .\scripts\e2e.ps1 -Changed                 the tests your changed files can affect (tests\NetPI.E2E\areas.json maps them)
    .\scripts\e2e.ps1 -Only control.abort-stream, retry     test ids, or substrings of an id or a name
    .\scripts\e2e.ps1 -Tag retry               every test of an area or tag (.\scripts\e2e.ps1 -List shows them)
    .\scripts\e2e.ps1 -Smoke                   one representative test per boundary (~10 s)
    .\scripts\e2e.ps1 -Failed                  the tests that failed and have not passed since (across runs)
    .\scripts\e2e.ps1                          everything, sharded: the gate before a merge (and after a change to a contract)

  One test alone costs ~2 s (server start included). A failure is explained where it happened: the console shows the
  message, and artifacts\e2elogs\<run>\failures\<id>.txt has the server log, the mock model's requests and the client
  events since that test began. Read that instead of running again; every failure of the run is listed at once, with the
  exact command to rerun them.

  A test that fails sometimes is a bug in the test or the code, not weather. Never run it again "to see"; measure it:

    .\scripts\e2e.ps1 -Only <id> -Repeat 20 -Fresh          20 fresh servers: how often, and the evidence of each failure
    .\scripts\e2e.ps1 -Fresh                                every test alone on its own server (finds hidden order dependencies)

  Builds are incremental and cheap: only the projects whose sources changed since their last build here are built (the
  dev output, artifacts\dev\app; nothing is installed and a running NetPI is never touched). -SkipBuild trusts what is
  there; -Rebuild builds the whole solution.

.PARAMETER Only
  Test ids (exact), else substrings of an id or name. Several are OR-ed.
.PARAMETER Tag
  Every test with one of these tags (an area such as retry or slots, or smoke, lifecycle, scheduling, ui).
.PARAMETER Changed
  Select by the files changed against -Base (commits on this branch, staged, unstaged and new files).
.PARAMETER Repeat
  Run every selected test this many times.
.PARAMETER Fresh
  A fresh server for every test run.
.PARAMETER Parallel
  Shards running at once: auto (default: about one per 12 s of work, at most 4) or a number.
.PARAMETER NoUi
  Leave out the tests that drive a browser.

.EXAMPLE
  .\scripts\e2e.ps1 -Changed                       # after editing a plugin: its tests, in seconds
  .\scripts\e2e.ps1 -Only chat.stream -SkipBuild   # one test, nothing rebuilt
  .\scripts\e2e.ps1 -Failed                        # the fix loop
  .\scripts\e2e.ps1 -Flakes                        # how often each test flakes, and why it last failed
  .\scripts\e2e.ps1                                # the gate
#>
param(
    [string[]]$Only,
    [string[]]$Tag,
    [switch]$Smoke,
    [switch]$Failed,
    [switch]$Flakes,
    [switch]$Changed,
    [switch]$List,
    [switch]$NoUi,
    [int]$Repeat = 1,
    [switch]$Fresh,
    [string]$Parallel = 'auto',
    [switch]$SkipBuild,
    [switch]$Rebuild,
    [string]$Config = 'Release',
    [switch]$SelfTest,
    [switch]$Keep,
    [double]$TimeoutScale = 1,
    [string]$Base = 'master'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $logRoot = Join-Path $repo 'artifacts\e2elogs'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

    # ------------------------------------------------------------------ -Changed: which tests can the changed files reach?
    $selectTags = [System.Collections.Generic.List[string]]::new()
    $selectIds = [System.Collections.Generic.List[string]]::new()
    if ($Tag) { foreach ($t in $Tag) { $selectTags.Add($t) } }
    if ($Only) { foreach ($o in $Only) { $selectIds.Add($o) } }
    $runSelfTest = $SelfTest
    if ($Changed) {
        $files = [System.Collections.Generic.HashSet[string]]::new()
        $mergeBase = (git merge-base HEAD $Base 2>$null)
        $head = (git rev-parse HEAD)
        if ($mergeBase -and $head -ne $mergeBase) { git diff --name-only $mergeBase HEAD | ForEach-Object { [void]$files.Add($_) } }
        git diff --name-only HEAD | ForEach-Object { [void]$files.Add($_) }
        git ls-files --others --exclude-standard | ForEach-Object { [void]$files.Add($_) }
        if ($files.Count -eq 0) { Write-Host "-Changed: no file differs from $Base (nothing committed on this branch, nothing edited). Nothing to run." -ForegroundColor Yellow; exit 0 }
        $rules = (Get-Content (Join-Path $repo 'tests\NetPI.E2E\areas.json') -Raw | ConvertFrom-Json).rules
        $why = [ordered]@{}
        $full = $false
        foreach ($f in $files) {
            $hit = @($rules | Where-Object { $f.StartsWith($_.path) })
            $real = @($hit | Where-Object { -not $_.ignore })
            if ($real.Count -eq 0 -and $hit.Count -gt 0) { continue }   # docs, scripts, unit tests: no E2E test can see them
            if ($real.Count -eq 0) { $real = @([pscustomobject]@{ path = $f; tags = @('smoke') }); $why[$f] = 'no rule for it: the smoke set' }
            foreach ($r in $real) {
                if ($r.all) { $full = $true; $why[$r.path] = 'a contract: the full run' }
                foreach ($t in @($r.tags)) { if ($t -and -not $selectTags.Contains($t)) { $selectTags.Add($t) } }
                foreach ($i in @($r.ids)) { if ($i -and -not $selectIds.Contains($i)) { $selectIds.Add($i) } }
                if ($r.selfTest) { $runSelfTest = $true }
                if (-not $why.Contains($r.path)) { $why[$r.path] = (@($r.tags) + @($r.ids) | Where-Object { $_ }) -join ', ' }
            }
        }
        Write-Host "-Changed: $($files.Count) file(s) against $Base" -ForegroundColor Cyan
        foreach ($k in $why.Keys) { Write-Host ("  {0,-46} -> {1}" -f $k, $why[$k]) -ForegroundColor DarkGray }
        if ($full) { $selectTags.Clear(); $selectIds.Clear() }
        elseif ($selectTags.Count -eq 0 -and $selectIds.Count -eq 0 -and -not $runSelfTest) {
            Write-Host 'Nothing to run: the changed files cannot affect an end-to-end test (docs, scripts, unit tests).' -ForegroundColor Green
            exit 0
        }
    }

    # ------------------------------------------------------------------ build: only what is stale
    $stampFile = Join-Path $logRoot "build-stamps-$Config.json"
    $stamps = @{}
    if (Test-Path $stampFile) { try { (Get-Content $stampFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $stamps[$_.Name] = [long]$_.Value } } catch { $stamps = @{} } }
    function Get-Newest([string[]]$paths) {
        $newest = 0L
        foreach ($rel in $paths) {
            # absolute: .NET resolves a relative path against the process's directory, which Push-Location does not change
            $p = Join-Path $repo $rel
            if (-not (Test-Path -LiteralPath $p)) { continue }
            if ((Get-Item -LiteralPath $p).PSIsContainer) {
                foreach ($f in [IO.Directory]::EnumerateFiles($p, '*', [IO.SearchOption]::AllDirectories)) {
                    if ($f -match '\\(bin|obj|node_modules|screenshots)\\') { continue }
                    $t = [IO.File]::GetLastWriteTimeUtc($f).Ticks
                    if ($t -gt $newest) { $newest = $t }
                }
            }
            else { $t = [IO.File]::GetLastWriteTimeUtc($p).Ticks; if ($t -gt $newest) { $newest = $t } }
        }
        return $newest
    }

    $dev = 'artifacts\dev\app'
    $e2eBin = "tests\NetPI.E2E\bin\$Config"
    $shared = @('Directory.Build.props', 'plugins\Directory.Build.props')
    $projects = [System.Collections.Generic.List[object]]::new()
    $projects.Add(@{ Name = 'NetPI.Abstractions'; Dir = 'src\NetPI.Abstractions'; Out = "$dev\NetPI.Abstractions.dll"; Inputs = @('src\NetPI.Abstractions') + $shared; Core = $true })
    $projects.Add(@{ Name = 'NetPI.Contracts'; Dir = 'src\NetPI.Contracts'; Out = "$dev\NetPI.Contracts.dll"; Inputs = @('src\NetPI.Contracts') + $shared; Core = $true })
    $projects.Add(@{ Name = 'NetPI.Host'; Dir = 'src\NetPI.Host'; Out = "$dev\NetPI.Host.dll"; Inputs = @('src\NetPI.Host') + $shared; Core = $true })
    $projects.Add(@{ Name = 'NetPI.Server'; Dir = 'src\NetPI.Server'; Out = "$dev\netpi-server.dll"; Inputs = @('src\NetPI.Server', 'web\dist') + $shared })
    foreach ($d in Get-ChildItem plugins -Directory) {
        $projects.Add(@{ Name = $d.Name; Dir = "plugins\$($d.Name)"; Out = "$dev\plugins\$($d.Name)\$($d.Name).dll"; Inputs = @("plugins\$($d.Name)") + $shared })
    }
    $projects.Add(@{ Name = 'MockLlm'; Dir = 'tests\MockLlm'; Out = "$e2eBin\MockLlm.dll"; Inputs = @('tests\MockLlm') + $shared })
    $projects.Add(@{ Name = 'NetPI.E2E'; Dir = 'tests\NetPI.E2E'; Out = "$e2eBin\NetPI.E2E.dll"; Inputs = @('tests\NetPI.E2E', 'tests\NetPI.Aux.Tests\McpFixture.cs') + $shared })

    if (-not $SkipBuild) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        # the web UI: rebuild the bundles only when their sources changed after the last build here (a fresh checkout trusts the committed ones)
        $webInputs = @('web\src', 'web\index.html', 'web\vite.config.js') + @(Get-ChildItem plugins -Directory | ForEach-Object { Join-Path $_.FullName 'ui' })
        $webNewest = Get-Newest $webInputs
        if ($stamps.ContainsKey('web') -and $webNewest -gt $stamps['web'] -and (Get-Command npm -ErrorAction SilentlyContinue) -and (Test-Path node_modules)) {
            Write-Host 'web UI sources changed: npm run build' -ForegroundColor DarkGray
            $started = [DateTime]::UtcNow.Ticks
            $env:NETPI_NO_COPY = '1'
            try { npm run build --silent | Out-Null } finally { Remove-Item Env:NETPI_NO_COPY -ErrorAction SilentlyContinue }
            if ($LASTEXITCODE) { throw 'npm run build failed' }
            $stamps['web'] = $started
        }
        elseif (-not $stamps.ContainsKey('web')) { $stamps['web'] = [DateTime]::UtcNow.Ticks }

        $stale = @($projects | Where-Object {
                $_.Name -and ($Rebuild -or -not $stamps.ContainsKey($_.Name) -or -not (Test-Path $_.Out) -or (Get-Newest $_.Inputs) -gt $stamps[$_.Name])
            })
        if ($stale.Count -eq 0) { Write-Host ("build: everything is current ({0:0.0}s to check)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray }
        else {
            $started = [DateTime]::UtcNow.Ticks
            $coreStale = $Rebuild -or @($stale | Where-Object { $_.Core }).Count -gt 0
            if ($coreStale) {
                # a contract or the host changed: everything downstream is rebuilt, which the solution build does in one go
                Write-Host 'build: the contracts or the host changed, building the solution' -ForegroundColor Cyan
                dotnet build NetPI.slnx -c $Config --nologo -v q -clp:ErrorsOnly
                if ($LASTEXITCODE) { throw 'dotnet build failed' }
                foreach ($p in $projects) { $stamps[$p.Name] = $started }
            }
            else {
                Write-Host ('build: ' + (($stale | ForEach-Object { $_.Name }) -join ', ')) -ForegroundColor Cyan
                foreach ($p in $stale) {
                    $proj = Get-ChildItem $p.Dir -Filter *.csproj | Select-Object -First 1
                    dotnet build $proj.FullName -c $Config --nologo -v q -clp:ErrorsOnly -p:BuildProjectReferences=false
                    if ($LASTEXITCODE) { throw "dotnet build of $($p.Name) failed" }
                    $stamps[$p.Name] = $started
                }
            }
            Write-Host ("build: {0:0.0}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
        }
        ([pscustomobject]$stamps) | ConvertTo-Json | Set-Content $stampFile
    }

    $dll = Join-Path $repo "$e2eBin\NetPI.E2E.dll"
    if (-not (Test-Path $dll)) { throw "$dll is missing: run without -SkipBuild" }

    # ------------------------------------------------------------------ run
    if ($runSelfTest -and -not $Only -and -not $Tag -and -not $Smoke -and -not $Failed -and $selectTags.Count -eq 0 -and $selectIds.Count -eq 0) {
        & dotnet $dll --self-test
        exit $LASTEXITCODE
    }
    if ($runSelfTest) {
        Write-Host 'runner self-test (the E2E runner changed)' -ForegroundColor Cyan
        & dotnet $dll --self-test
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }

    $stamp = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([guid]::NewGuid().ToString('N').Substring(0, 4))
    $out = Join-Path $logRoot $stamp
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $argList = [System.Collections.Generic.List[string]]::new()
    foreach ($i in $selectIds) { $argList.Add($i) }
    if ($selectTags.Count) { $argList.Add('--tag'); $argList.Add(($selectTags -join ',')) }
    if ($Smoke) { $argList.Add('--smoke') }
    if ($Failed) { $argList.Add('--failed') }
    if ($Flakes) { $argList.Add('--flakes') }
    if ($NoUi) { $argList.Add('--no-ui') }
    if ($List) { $argList.Add('--list') }
    if ($Repeat -gt 1) { $argList.Add('--repeat'); $argList.Add("$Repeat") }
    if ($Fresh) { $argList.Add('--fresh') }
    if ($Keep) { $argList.Add('--keep') }
    if ($Parallel -ne 'auto') { $argList.Add('--parallel'); $argList.Add($Parallel) }
    if ($TimeoutScale -ne 1) { $argList.Add('--timeout-scale'); $argList.Add("$TimeoutScale") }
    $argList.Add('--out'); $argList.Add($out)

    $wall = [Diagnostics.Stopwatch]::StartNew()
    # the runner prints this as its rerun hint, instead of a dotnet command line. Only for this child: the variable must not stay in the
    # caller's session (the runner's own self-test, run next in the same session, read it and failed)
    $hadRerun = Test-Path Env:NETPI_E2E_RERUN
    $oldRerun = $env:NETPI_E2E_RERUN
    $env:NETPI_E2E_RERUN = ".\scripts\e2e.ps1 -Only {ids} -SkipBuild`n  .\scripts\e2e.ps1 -Failed -SkipBuild        (everything still failing, across runs)"
    try {
        # UTF-8 on purpose: Tee-Object writes UTF-16 in Windows PowerShell 5.1, and console.txt is read with grep
        $console = [IO.StreamWriter]::new((Join-Path $out 'console.txt'), $false, [Text.UTF8Encoding]::new($false))
        try {
            & dotnet $dll @argList 2>&1 | ForEach-Object { $line = "$_"; $console.WriteLine($line); $console.Flush(); $line }
            $code = $LASTEXITCODE
        }
        finally { $console.Dispose() }
    }
    finally {
        if ($hadRerun) { $env:NETPI_E2E_RERUN = $oldRerun } else { Remove-Item Env:NETPI_E2E_RERUN -ErrorAction SilentlyContinue }
    }
    $wall.Stop()
    if ($List) { exit $code }

    Write-Host ("`nwall {0:0.0}s (builds included above)  ·  results: {1}" -f $wall.Elapsed.TotalSeconds, $out) -ForegroundColor DarkGray
    exit $code
}
finally { Pop-Location }
