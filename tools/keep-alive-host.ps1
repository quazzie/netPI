# Keeps the netPI host REPL alive by holding its stdin stream open (never writes,
# never closes). Run in background. Usage: pwsh tools/keep-alive-host.ps1
#
# The host is COPIED to a NEW immutable launch directory under
# ~/.netpi/app-cache/host/<staging-id>/launch-<timestamp> and run from there —
# never from the source bin and never from a directory a later run would refresh
# in place (astra-1 P0). A failed or partial copy aborts the launch; prior launch
# dirs are pruned (keeping the 3 most recent), and a locked/locked-away dir is
# skipped rather than blocking the current launch.
#
# Launcher identity (astra-1 P0.3): an open port is not proof the listener is a
# netPI host — the script probes GET /identity on the port; a matching build id
# means the existing host is reused, a different build id (or a non-netPI
# listener) is an explicit error, and a free port means we stage+launch ours.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$srcBin = Join-Path $root 'src/NetPI.Host/bin/Debug/net10.0'
if (-not (Test-Path (Join-Path $srcBin 'netPI.Host.dll'))) {
    Write-Error "Build the host first: dotnet build src/NetPI.Host -c Debug"
}
# Honour a caller-provided NETPI_HOME; default to ~/.netpi. Never overwrite an
# already-set value (astra-1 P0.3: unified launcher home selection).
if (-not $env:NETPI_HOME) { $env:NETPI_HOME = Join-Path $env:USERPROFILE '.netpi' }
$port = 5173

# Identity probe: an open TCP port is not evidence it is OUR netPI host.
function Test-NetPiIdentity {
    param([string]$Base)
    try {
        $r = Invoke-WebRequest -Uri "$Base/identity" -UseBasicParsing -TimeoutSec 2
        $j = $r.Content | ConvertFrom-Json
        if ($j.name -eq 'netPI') { return $j }
    } catch { }
    return $null
}

# Stage the complete host payload into a fresh immutable launch directory and
# verify it. Aborts (no file-by-file fallback) if any copy or check fails.
function Stage-HostLaunch {
    param([string]$Source, [string]$StageRoot, [string]$TraceLog)
    $dll = Join-Path $Source 'netPI.Host.dll'
    $stagingId = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.ToLower().Substring(0, 12)
    $buildRoot = Join-Path $StageRoot "host\$stagingId"
    # Prune older launch dirs of this build (best-effort; locked ones are skipped).
    if (Test-Path $buildRoot) {
        $keep = Get-ChildItem -Path $buildRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3
        Get-ChildItem -Path $buildRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $keep.Name -notcontains $_.Name } |
            ForEach-Object {
                try { Remove-Item $_.FullName -Recurse -Force }
                catch { Write-Host "prune-skipped $($_.Name) ($($_.Exception.Message))" }
            }
    }
    $launchDir = Join-Path $buildRoot ("launch-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $launchDir -Force | Out-Null
    try {
        Copy-Item -Path (Join-Path $Source '*') -Destination $launchDir -Recurse -ErrorAction Stop
        # Verify: every staged file exists, same size, byte-identical.
        Get-ChildItem -Path $Source -Recurse -File | ForEach-Object {
            $dst = Join-Path $launchDir $_.FullName.Substring($Source.Length).TrimStart('\','/')
            if (-not (Test-Path $dst) -or ((Get-Item $dst).Length -ne $_.Length) -or ((Get-FileHash $dst).Hash -ne (Get-FileHash $_.FullName).Hash)) {
                throw "staged host file differs from source: $($_.Name)"
            }
        }
    } catch {
        Remove-Item $launchDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Error "could not stage a complete host snapshot: $($_.Exception.Message)"
    }
    if (Test-Path $TraceLog) { Add-Content -Path $TraceLog -Value "$(Get-Date -Format 'HH:mm:ss') host-staged $launchDir" }
    return $launchDir
}

# 1) Reuse a healthy existing host when its identity matches the pending build.
$existing = Test-NetPiIdentity -Base "http://127.0.0.1:$port"
if ($existing) {
    $pendingId = (Get-FileHash -Path (Join-Path $srcBin 'netPI.Host.dll') -Algorithm SHA256).Hash.ToLower()
    if ($existing.buildId -eq $pendingId) {
        Write-Output "netPI host already running (build $($pendingId.Substring(0,12)), dir $($existing.hostDir)) — reusing, exiting."
        exit 0
    }
    Write-Error "A netPI host with a DIFFERENT build is already running on port $port (running $($existing.buildId.Substring(0,12)), pending $($pendingId.Substring(0,12))). A host/Abstractions change is not a plugin hot reload — stop the old instance (or its launcher window) to install the pending build."
}

# 2) Otherwise stage a fresh immutable launch dir and run it.
$appCache = Join-Path $env:NETPI_HOME 'app-cache'
$launchDir = Stage-HostLaunch -Source $srcBin -StageRoot $appCache -TraceLog (Join-Path $root 'host-launch.log')
$hostExe = Join-Path $launchDir 'netPI.Host.exe'
if (-not (Test-Path $hostExe)) { $hostExe = (Join-Path $launchDir 'netPI.Host.dll') ; $exeArg = $true } else { $exeArg = $false }

# Explicit launcher identity for the child process.
$childEnv = @{
    NETPI_HOME        = $env:NETPI_HOME
    NETPI_PLUGINS     = (Join-Path $root 'plugins')
    NETPI_PROJECT_ROOT = $root
    NETPI_HOST_BUILD_ID = (Get-FileHash -Path (Join-Path $launchDir 'netPI.Host.dll') -Algorithm SHA256).Hash.ToLower()
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
if ($exeArg) { $psi.FileName = 'dotnet'; $psi.Arguments = "`"$hostExe`"" } else { $psi.FileName = $hostExe }
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($k in $childEnv.Keys) { $psi.EnvironmentVariables[$k] = $childEnv[$k] }

$p = [System.Diagnostics.Process]::Start($psi)
$stdin = $p.StandardInput   # hold reference so the writer is never GC'd/closed
# Read both streams concurrently so a full 4 KB buffer can never stall the
# process. stdin stays open for the life of the host.
$stdoutReader = $p.StandardOutput
$stderrReader = $p.StandardError
$tOut = [System.Threading.Tasks.Task]::Run([System.Action] {
    while ($null -ne ($l = $stdoutReader.ReadLine())) { Write-Output $l }
})
$tErr = [System.Threading.Tasks.Task]::Run([System.Action] {
    while ($null -ne ($l = $stderrReader.ReadLine())) { Write-Warning $l }
})
$p.WaitForExit()
$tOut.Wait()
$tErr.Wait()
Write-Output "host exited with code $($p.ExitCode)"
