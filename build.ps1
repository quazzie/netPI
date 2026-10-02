<#
.SYNOPSIS
  Build NetPI on Windows.

.DESCRIPTION
  A build lands in artifacts\dev\app (NetPI.exe, netpi-server.exe, plugins\, wwwroot\) and nothing else: a running
  NetPI loads its plugins from artifacts\app and reloads one as soon as that file changes, so building — even just
  `dotnet build` of a plugin or a test project that references one — must not write there. That is why the default
  output is the dev tree and installing is a separate, deliberate step:

    .\build.ps1                build into artifacts\dev\app. The running app is untouched.
    .\build.ps1 -Publish       build, then install into artifacts\app. A running NetPI hot-reloads the changed plugins,
                               so every chat that holds one of their tools gets a "tools" notice; the script says which
                               plugins and how many chats are mid-turn before it does.
    .\build.ps1 -Publish -NextStart
                               install nothing now: everything waits in artifacts\app\.pending for the next start.
    .\build.ps1 -Pending       what a restart would bring (.pending, and the host files in .old).
    .\build.ps1 -Discard       drop the staged build in .pending (the host files in .old belong to the running app).

  Publishing while NetPI runs moves each changed host file into artifacts\app\.old (a running exe or DLL can be
  renamed, not overwritten), so the next start of NetPI.exe runs the new host; when the contracts changed, the
  rebuilt plugins wait in .pending instead, because the running NetPI would load them onto its old contracts.

.EXAMPLE
  .\build.ps1                # build (Release) into artifacts\dev\app; the running app sees nothing
  .\build.ps1 -Test          # build and run the unit test suites
  .\build.ps1 -Publish       # build and install into the running app
  .\build.ps1 -Publish -NextStart -WaitUntilIdle
  .\build.ps1 -Publish -WaitUntilIdle   # wait until no chat is mid-turn, then install
  .\build.ps1 -Pending      # what the next start of NetPI would pick up
  .\build.ps1 -Run           # publish and start the desktop app
  .\build.ps1 -SkipWeb       # don't run npm even if it is installed
  From cmd: build.cmd runs this script with the same options (build -Run, build /?).
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipWeb,
    [switch] $Publish,
    [switch] $NextStart,
    [switch] $WaitUntilIdle,
    [switch] $Pending,
    [switch] $Discard,
    [switch] $Run,
    [switch] $Test,
    [string] $AppDir
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

$dev = Join-Path $PSScriptRoot 'artifacts\dev\app'
$app = Join-Path $PSScriptRoot 'artifacts\app'
# Where to install. The app that is *running* says where it is (server.json's appDir), which is not this repository's
# artifacts\app when the build happens in a worktree - installing into a folder nothing watches is the silent no-op that
# makes "I published" a lie. -AppDir overrides both.
if ($AppDir) { $app = (Resolve-Path -LiteralPath $AppDir -ErrorAction SilentlyContinue)?.Path ?? [IO.Path]::GetFullPath($AppDir) }
else {
    $netpiHome = if ($env:NETPI_HOME) { $env:NETPI_HOME } else { Join-Path $HOME '.netpi' }   # $HOME is read-only
    $serverFile = Join-Path $netpiHome 'server.json'
    if (Test-Path $serverFile) {
        try {
            $running = Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json
            if ($running.appDir -and (Test-Path $running.appDir)) {
                $app = (Resolve-Path -LiteralPath $running.appDir).Path
                if ((Split-Path -Parent (Split-Path -Parent $app)) -ne $PSScriptRoot) {
                    Write-Host "A NetPI is running from $app (not this repository) - installing there." -ForegroundColor Yellow
                }
            }
        } catch { Write-Host "Could not read ${serverFile}: installing into $app" -ForegroundColor DarkYellow }
    }
}
$oldDir = Join-Path $app '.old'
$pendingDir = Join-Path $app '.pending'
$lockFile = Join-Path $app '.install.lock'
# -NextStart, -Run and -Publish are one act: installing. The others only read or drop a staged build.
if ($NextStart -or $Run) { $Publish = $true }
$defer = $NextStart -and $Publish

# ---- is NetPI running from artifacts\app? A running NetPI keeps its host DLLs open (plugins load from shadow copies).
# A build made while it ran moved the files it replaced into .old, so the contracts a running NetPI uses are the open
# NetPI.Abstractions.dll and NetPI.Contracts.dll (the two contract assemblies every plugin shares), here or in .old: their
# hashes, taken before this build moves anything.
function Test-FileLocked([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try { [IO.File]::Open($path, 'Open', 'ReadWrite', 'None').Dispose(); return $false } catch { return $true }
}
$contractNames = @('NetPI.Abstractions.dll', 'NetPI.Contracts.dll')
$contracts = @($contractNames | ForEach-Object { Join-Path $app $_ })
if (Test-Path $oldDir) { $contracts += @(Get-ChildItem $oldDir -Recurse -File | Where-Object { $contractNames -contains $_.Name } | ForEach-Object { $_.FullName }) }
# "name:hash": a change of either assembly is a contract change
$runningContracts = @($contracts | Where-Object { Test-FileLocked $_ } | ForEach-Object { (Split-Path $_ -Leaf) + ':' + (Get-FileHash -LiteralPath $_).Hash })
$running = $runningContracts.Count -gt 0

# ---- what a restart would bring, and dropping it
if ($Pending -or $Discard) {
    Step $(if ($Discard) { 'Staged build' } else { 'Pending for the next start' })
    if ($Discard) {
        if (Test-Path $pendingDir) { Remove-Item -Recurse -Force $pendingDir; Write-Host "Dropped $pendingDir" -ForegroundColor Green }
        else { Write-Host 'Nothing was staged.' }
        if (Test-Path $oldDir) { Write-Host "artifacts\app\.old keeps the host files the running NetPI has open; they go on their own." }
        return
    }
    if (-not (Test-Path $pendingDir)) { Write-Host 'Nothing is staged: a restart brings the build already in artifacts\app.' }
    else {
        Get-ChildItem $pendingDir -Recurse -File -Force | ForEach-Object {
            Write-Host ("  {0} ({1:N0} bytes)" -f $_.FullName.Substring($pendingDir.Length + 1), $_.Length)
        }
    }
    if (Test-Path $oldDir) {
        $n = @(Get-ChildItem $oldDir -Recurse -File -Force).Count
        if ($n) { Write-Host "  .old: $n host file(s) a restart replaces" -ForegroundColor DarkGray }
    }
    return
}

# ---- web UI (optional). The bundles land in the source folders (web\dist, plugins\*\wwwroot\ui.js); the .NET build
# copies them into its output, so nothing is written into artifacts\app here.
if (-not $SkipWeb) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        Step 'Web UI (npm)'
        if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } }
        # --copy installs the bundles into artifacts\app as well (UI edits then hot-reload without a .NET build); the
        # default leaves the app alone, and NETPI_NO_COPY (build.ps1 -NextStart) does too.
        if ($Publish -and -not $defer) { $env:NETPI_COPY = '1' }
        try { npm run build; if ($LASTEXITCODE) { throw 'npm run build failed' } }
        finally { Remove-Item Env:NETPI_COPY -ErrorAction SilentlyContinue }
    }
    else {
        Write-Host 'npm not found: using the prebuilt web UI (web\dist and plugins\*\wwwroot\ui.js).' -ForegroundColor Yellow
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

# One install at a time. Two agents publishing into the same app folder would otherwise interleave file by file and
# leave a mix of two builds. The lock is a file held open for the duration; if a publisher dies the handle is released by
# the OS and the file goes stale, which the next publisher takes over after a minute.
function Enter-InstallLock([string] $path) {
    $dir = Split-Path $path
    New-Item -ItemType Directory -Force $dir | Out-Null
    for ($waited = 0; $waited -lt 120; $waited++) {
        try {
            $fs = [IO.File]::Open($path, 'CreateNew', 'Write', 'None')
            $bytes = [Text.Encoding]::UTF8.GetBytes("pid $PID, since $(Get-Date -Format o)")
            $fs.Write($bytes, 0, $bytes.Length)
            $fs.Flush($true)
            return $fs
        }
        catch [IO.IOException] {
            if (Test-Path $path) {
                $age = (Get-Date) - (Get-Item -LiteralPath $path).LastWriteTime
                if ($age -gt [TimeSpan]::FromMinutes(1)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue; continue }
            }
            if ($waited % 15 -eq 0) { Write-Host "Another publish is installing into $dir; waiting…" -ForegroundColor Yellow }
            Start-Sleep -Seconds 1
        }
    }
    throw "Another publish has held $path for two minutes. If no build is running, delete it."
}

# ---- what the running app is doing right now: a publish that hot-reloads plugins disturbs the chats that hold their
# tools, so say so before doing it. Read-only, best effort: any failure just means no report. Only a plugin that
# registers tools can take any away (a hook-only one swaps under a run and announces nothing), so the report names the
# tools actually at stake instead of warning about every reload.
function Get-LiveChats {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $null }
    try {
        $o = node scripts/netpi.mjs diag.overview --compact 2>$null | ConvertFrom-Json
        if (-not $o) { return $null }
        $busy = @($o.runs | Where-Object { $_.status -in 'running', 'queued' })
        return [pscustomobject]@{ Busy = $busy; Total = @($o.runs).Count }
    } catch { return $null }
}

# The tools the plugins about to be installed register (tools.list maps every tool to its plugin).
function Get-PluginTools([string[]] $pluginNames) {
    if (-not $pluginNames.Count -or -not (Get-Command node -ErrorAction SilentlyContinue)) { return $null }
    try {
        $tools = node scripts/netpi.mjs tools.list --compact 2>$null | ConvertFrom-Json
        if (-not $tools) { return $null }
        return @($tools | Where-Object { $pluginNames -contains $_.pluginId -and $_.active } |
            Select-Object -ExpandProperty name | Sort-Object -Unique)
    } catch { return $null }
}

function Show-PublishCost([string[]] $pluginNames) {
    $live = Get-LiveChats
    if (-not $live) { return }
    $names = if ($pluginNames.Count) { $pluginNames -join ', ' } else { 'the web UI' }
    $tools = Get-PluginTools $pluginNames
    if ($null -ne $tools -and $tools.Count -eq 0) {
        Write-Host "Installing into the running app: $names. They register no tools, so no chat loses any." -ForegroundColor Green
        return
    }
    if (-not $live.Busy.Count) {
        $what = if ($null -ne $tools) { "$($tools.Count) tool(s) reload ($($tools -join ', '))" } else { 'plugins reload' }
        Write-Host "Installing into the running app: $names — $what. No chat is mid-turn." -ForegroundColor Green
        return
    }
    $who = @($live.Busy | ForEach-Object { $_.name }) -join ', '
    Write-Host "About to install into the running app: $names." -ForegroundColor Yellow
    if ($null -ne $tools) {
        Write-Host "$($live.Busy.Count) chat(s) mid-turn ($who); $($tools.Count) tool(s) go away for a moment ($($tools -join ', ')), and a chat holding one gets a notice at its next model call." -ForegroundColor Yellow
    }
    else {
        Write-Host "$($live.Busy.Count) chat(s) mid-turn ($who); -NextStart defers the whole install to a restart." -ForegroundColor Yellow
    }
}

# ---- .NET: always into the dev tree. Nothing below this point can fail halfway into artifacts\app.
Step "dotnet build ($Configuration)"
if (Test-Path $dev) { Remove-Item -Recurse -Force $dev }   # nothing stale (a removed plugin, an old wwwroot) survives
dotnet build NetPI.slnx -c $Configuration --nologo
if ($LASTEXITCODE) { throw 'dotnet build failed' }

Write-Host "`nBuilt artifacts\dev\app:" -ForegroundColor Green
Write-Host '  NetPI.exe          desktop app (WebView2 window, starts its own server)'
Write-Host '  netpi-server.exe   headless server (open the printed URL in a browser)'

# ---- install into artifacts\app (only -Publish): the app a NetPI runs from
if ($Publish) {
    Step "Publish (install into $app)"
    if ($WaitUntilIdle) {
        while ($true) {
            $live = Get-LiveChats
            if (-not $live -or -not $live.Busy.Count) { break }
            Write-Host "Waiting for $($live.Busy.Count) chat(s) to finish a turn: $(@($live.Busy | ForEach-Object { $_.name }) -join ', ')…"
            Start-Sleep -Seconds 5
        }
    }
    # one install at a time; waiting above is before the lock, so a patient publisher does not block another one.
    # A failed install throws and leaves the lock to go stale (a minute) rather than wrapping the whole block.
    $installLock = Enter-InstallLock $lockFile

    # plugins that were renamed (2026-09-25: NetPI.Lanes is NetPI.Agents, NetPI.Agent is NetPI.Runtime): their old
    # output would load next to the new one. A running app keeps them until it is closed and published again.
    if (-not $running) {
        foreach ($name in 'NetPI.Lanes', 'NetPI.Agent') {
            $dir = Join-Path $app "plugins\$name"
            if (Test-Path $dir) {
                Remove-Item -Recurse -Force $dir
                Write-Host "Removed the old plugin output $dir" -ForegroundColor Yellow
            }
        }
    }
    if (Test-Path $oldDir) {
        Get-ChildItem $oldDir -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        if (-not (Get-ChildItem $oldDir -Recurse -File -Force -ErrorAction SilentlyContinue)) { Remove-Item -Recurse -Force $oldDir -ErrorAction SilentlyContinue }
    }
    $oldHostRunning = (Test-Path $oldDir) -and [bool](Get-ChildItem $oldDir -Recurse -File -Force -ErrorAction SilentlyContinue)

    $restart = @()
    if (-not $running) {
        # nothing holds the files: the whole tree, as a normal build would
        if (Test-Path $pendingDir) { Remove-Item -Recurse -Force $pendingDir }
        New-Item -ItemType Directory -Force $app | Out-Null
        foreach ($f in Get-ChildItem $dev -Recurse -File -Force) {
            $rel = $f.FullName.Substring($dev.Length + 1)
            Copy-Rel $dev $app $rel
        }
        $pluginNames = @((Get-ChildItem (Join-Path $dev 'plugins') -Directory -ErrorAction SilentlyContinue) |
            ForEach-Object {
                $changed = @(Get-Changed $_.FullName (Join-Path $app "plugins\$($_.Name)"))
                if ($changed.Count) { $_.Name }
            })
        Write-Host "Installed into artifacts\app ($($pluginNames.Count) plugin(s) changed: $(if ($pluginNames.Count) { $pluginNames -join ', ' } else { 'none' }))." -ForegroundColor Green
    }
    else {
        Write-Host 'NetPI is running from artifacts\app: the install is file by file, so a failed one changes nothing.' -ForegroundColor Yellow

        # host: each changed file is moved into .old (the running NetPI keeps using it) and the new one copied in. All or
        # nothing: if a file can't be moved, the ones already moved go back. Symbols in use stay (they only give line numbers).
        $hostFiles = @(Get-Changed $dev $app 'plugins', 'wwwroot')
        $aside = Join-Path $oldDir (Get-Date -Format 'yyyyMMdd-HHmmss')
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
                throw "Could not replace $rel while NetPI runs ($($_.Exception.Message)): artifacts\app is unchanged. Close NetPI and publish again."
            }
        }
        foreach ($rel in $hostFiles) { if ($keptSymbols -notcontains $rel) { Copy-Rel $dev $app $rel } }
        $restart += @($hostFiles | Where-Object { $_ -match '\.(dll|exe|json)$' })

        # what waits for the next start: -NextStart, and plugins built against contracts the running NetPI doesn't have
        # (then every plugin's output changes); this publish supersedes an earlier one's
        $builtContracts = @($contractNames | ForEach-Object { $_ + ':' + (Get-FileHash (Join-Path $dev $_)).Hash })
        $newContracts = @($runningContracts | Where-Object { $builtContracts -notcontains $_ }).Count -gt 0
        if (Test-Path $pendingDir) { Remove-Item -Recurse -Force $pendingDir }

        # web UI: replaced as a whole, like a normal build does (a file being served may stay; it is overwritten then)
        $web = Join-Path $app 'wwwroot'
        $webChanged = @(Get-Changed (Join-Path $dev 'wwwroot') $web).Count -gt 0
        if ($webChanged -and $defer) {
            New-Item -ItemType Directory -Force $pendingDir | Out-Null
            Copy-Item -Recurse (Join-Path $dev 'wwwroot') (Join-Path $pendingDir 'wwwroot')
        }
        elseif ($webChanged) {
            Remove-Item -LiteralPath $web -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $web | Out-Null
            Copy-Item -Path (Join-Path $dev 'wwwroot\*') -Destination $web -Recurse -Force
        }

        # plugins: changed files go into place and the running NetPI reloads those plugins, or wait in .pending
        $devPlugins = Join-Path $dev 'plugins'
        $pluginFiles = @(Get-Changed $devPlugins (Join-Path $app 'plugins'))
        $pluginNames = @($pluginFiles | ForEach-Object { $_.Split('\')[0] } | Sort-Object -Unique)
        $pluginsWait = $defer -or $newContracts
        if ($pluginsWait) {
            foreach ($name in $pluginNames) {
                New-Item -ItemType Directory -Force (Join-Path $pendingDir 'plugins') | Out-Null
                Copy-Item -Recurse (Join-Path $devPlugins $name) (Join-Path $pendingDir "plugins\$name")
            }
        }
        else {
            if ($pluginNames.Count) { Show-PublishCost $pluginNames }
            foreach ($rel in $pluginFiles) { Copy-Rel $devPlugins (Join-Path $app 'plugins') $rel }
        }

        Write-Host ''
        if ($pluginNames.Count -and -not $pluginsWait) {
            Write-Host "Plugins updated in place (the running NetPI reloads them): $($pluginNames -join ', ')" -ForegroundColor Green
        }
        if ($pluginNames.Count -and $pluginsWait) {
            $why = if ($newContracts) { 'The contracts changed: the running NetPI keeps its plugins' } else { 'The running NetPI keeps its plugins' }
            Write-Host "$why; the rebuilt ones wait in artifacts\app\.pending for its next start: $($pluginNames -join ', ')" -ForegroundColor Yellow
        }
        if ($webChanged -and $defer) { Write-Host 'The new web UI waits in artifacts\app\.pending for the next start.' -ForegroundColor Yellow }
        if ($keptSymbols.Count) {
            Write-Host "Symbols in use were kept: $($keptSymbols -join ', ') (stack traces lack line numbers until a publish with NetPI closed)." -ForegroundColor DarkYellow
        }
    }

    if ($restart.Count -or $oldHostRunning) {
        $what = if ($restart.Count) { " The new host: $($restart -join ', ')." } else { '' }
        Write-Host "Ready for the next start: close NetPI and start it again (artifacts\app\NetPI.exe).$what" -ForegroundColor Yellow
    }
    Write-Host "Installed into $app (the running app). artifacts\dev\app is what you just built; -Pending lists what a restart waits for." -ForegroundColor DarkGray
    $installLock.Dispose()
    Remove-Item -LiteralPath $lockFile -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "`nThe running app was not touched: $app is the installed build, artifacts\dev\app is this one." -ForegroundColor Green
    Write-Host 'Install it with .\build.ps1 -Publish (now) or -Publish -NextStart (at the next start).' -ForegroundColor DarkGray
}

if ($Test) {
    Step 'Unit tests'
    # the suites load the built plugins from here (NETPI_APP_DIR); a worktree has no artifacts\app to fall back on
    $env:NETPI_APP_DIR = $dev
    $failed = 0
    foreach ($t in 'Providers', 'Tools', 'Agent', 'Aux', 'Host', 'Storage') {
        $dll = "tests\NetPI.$t.Tests\bin\$Configuration\NetPI.$t.Tests.dll"
        Write-Host "-- $t"
        dotnet $dll
        if ($LASTEXITCODE) { $failed++ }
    }
    Remove-Item Env:NETPI_APP_DIR -ErrorAction SilentlyContinue
    if ($failed) { throw "$failed test suite(s) failed" }
}

if ($Run) {
    if ($running) { Write-Host 'NetPI is already running.' -ForegroundColor Yellow }
    else { Start-Process (Join-Path $app 'NetPI.exe') }
}
