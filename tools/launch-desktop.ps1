# Developer launch path for the netPI desktop shell (astra-1 P0.1).
#
# The desktop shell and its bundled host must execute OUTSIDE the repository
# bin/obj/publish outputs. This script:
#   1. Builds the desktop app (unless -NoBuild) so bin/Debug is current.
#   2. Stages the COMPLETE desktop payload (the shell + WebView2 + its bundled
#      host/) into a NEW immutable runtime dir under
#         ~/.netpi/app-cache/desktop/<stagingId>/launch-<timestamp>
#      and verifies every staged file is byte-identical to the source. A failed
#      or partial stage is discarded and never launched from (P0.2 — no
#      file-by-file fallback).
#   3. Launches the staged netPI.Desktop.exe with explicit launcher identity
#      (NETPI_HOME / NETPI_PROJECT_ROOT / NETPI_PLUGINS). The shell itself then
#      re-stages its bundled host/ into ~/.netpi/app-cache/host/... before
#      starting it, so the running host is also never from a bin dir.
#
# The staging id is the first 12 hex chars of the sha256 of netPI.Desktop.dll,
# so a code change produces a new staging id and a fresh launch dir; an unchanged
# build reuses the same staging id and prunes to the 3 most recent launch dirs.
#
# Usage:
#   pwsh tools/launch-desktop.ps1            # build, stage, launch (default)
#   pwsh tools/launch-desktop.ps1 -NoBuild   # stage + launch an existing build
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Honour a caller-provided NETPI_HOME; default to ~/.netpi. Never overwrite an
# already-set value (astra-1 P0.3: unified launcher home selection).
if (-not $env:NETPI_HOME) { $env:NETPI_HOME = Join-Path $env:USERPROFILE '.netpi' }

# 1) Build the desktop app (default: build first; -NoBuild stages an existing one).
if (-not $NoBuild) {
    Write-Output "building desktop app…"
    & dotnet build (Join-Path $root 'src/NetPI.Desktop/netPI.Desktop.csproj') -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "desktop build failed (exit $LASTEXITCODE)" }
}

# The complete desktop payload lives in bin/Debug/net10.0-windows/.
$srcBin = Join-Path $root 'src/NetPI.Desktop/bin/Debug/net10.0-windows'
$dll = Join-Path $srcBin 'netPI.Desktop.dll'
if (-not (Test-Path $dll)) {
    Write-Error "Desktop not built. Run: dotnet build src/NetPI.Desktop  (or drop -NoBuild)"
}

# Stage-DesktopLaunch: copy the whole payload into a fresh immutable launch dir
# under <StageRoot>/<stagingId>/launch-<ts> and verify byte-identity. Aborts (no
# file-by-file fallback) if any copy or check fails.
function Stage-DesktopLaunch {
    param([string]$Source, [string]$StageRoot, [string]$TraceLog)
    $stagingId = (Get-FileHash -Path (Join-Path $Source 'netPI.Desktop.dll') -Algorithm SHA256).Hash.ToLower().Substring(0, 12)
    $buildRoot = Join-Path $StageRoot "desktop\$stagingId"
    # Prune older launch dirs of this build (keep 3; locked ones are skipped).
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
    $launchDir = Join-Path $buildRoot ("launch-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $launchDir -Force | Out-Null
    try {
        Copy-Item -Path (Join-Path $Source '*') -Destination $launchDir -Recurse -ErrorAction Stop
        # Verify: every staged file exists, same size, byte-identical.
        Get-ChildItem -Path $Source -Recurse -File | ForEach-Object {
            $dst = Join-Path $launchDir $_.FullName.Substring($Source.Length).TrimStart('\','/')
            if (-not (Test-Path $dst) -or ((Get-Item $dst).Length -ne $_.Length) -or ((Get-FileHash $dst).Hash -ne (Get-FileHash $_.FullName).Hash)) {
                throw "staged desktop file differs from source: $($_.Name)"
            }
        }
    } catch {
        Remove-Item $launchDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Error "could not stage a complete desktop payload: $($_.Exception.Message)"
    }
    if (Test-Path $TraceLog) { Add-Content -Path $TraceLog -Value "$(Get-Date -Format 'HH:mm:ss') desktop-staged $launchDir" }
    return $launchDir
}

# 2) Stage + verify.
$appCache = Join-Path $env:NETPI_HOME 'app-cache'
$traceLog = Join-Path $root 'desktop-launch.log'
$launchDir = Stage-DesktopLaunch -Source $srcBin -StageRoot $appCache -TraceLog $traceLog
$childExe = Join-Path $launchDir 'netPI.Desktop.exe'
if (-not (Test-Path $childExe)) {
    Write-Error "staged launch dir is missing netPI.Desktop.exe: $launchDir"
}

# 3) Launch the staged copy with explicit launcher identity. The shell is a GUI
#    app (WinExe) — UseShellExecute=$false is REQUIRED to set its EnvironmentVariables
#    (the .NET API refuses env vars with shell-execute on). It writes no console
#    output, so the three redirected streams are closed immediately (not pumped);
#    the shell owns its own window.
$childEnv = @{
    NETPI_HOME         = $env:NETPI_HOME
    NETPI_PROJECT_ROOT = $root
    NETPI_PLUGINS      = (Join-Path $root 'plugins')
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $childExe
$psi.UseShellExecute = $false
# Deliberately NOT WindowStyle='Hidden'/CreateNoWindow: the shell's main window IS the app
# window, and a hidden WindowStyle sets STARTUPINFO.wShowWindow=SW_HIDE, which suppresses
# the process's main window (the netPI form is then created but never shown). The shell
# writes no console output, so the streams are just closed below.
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($k in $childEnv.Keys) { $psi.EnvironmentVariables[$k] = $childEnv[$k] }
$p = [System.Diagnostics.Process]::Start($psi)
# No console output to drain: close the pipes immediately so the GUI app never
# blocks on a full buffer, and release the process handle so the launcher exits.
$p.StandardInput.Close(); $p.StandardOutput.Close(); $p.StandardError.Close()
$desktopPid = $p.Id
$p.Dispose()
Write-Output "launched netPI desktop from staged dir: $launchDir (pid $desktopPid)"
