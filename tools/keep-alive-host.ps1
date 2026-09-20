# Keeps the netPI host REPL alive by holding its stdin stream open (never writes,
# never closes). Run in background. Usage: pwsh tools/keep-alive-host.ps1
#
# The host is COPIED to ~/.netpi/host and run from there — never from the source
# bin. That way the running host locks files under ~/.netpi/host (free to be
# replaced on the next launch), and `dotnet build` can freely overwrite
# src/NetPI.Host/bin while the host is live. After rebuilding, stop the host
# (or its keep-alive window) and re-run this script to pick up new binaries.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$srcBin = Join-Path $root 'src/NetPI.Host/bin/Debug/net10.0'
$env:NETPI_HOME = Join-Path $env:USERPROFILE '.netpi'
$hostDir = Join-Path $env:NETPI_HOME 'host'
$hostDll = Join-Path $hostDir 'netPI.Host.dll'

# Refuse to start over a live copy (its DLLs would be locked mid-copy).
$running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($hostDll) }
if ($running) {


    Write-Error "A keep-alive host is already running from '$hostDir' (PID $($running.ProcessId -join ', ')). Stop it (close its window or Stop-Process) before re-running."
}

# Fresh snapshot: copy the built host into the runtime home.
if (Test-Path $hostDir) { Remove-Item $hostDir -Recurse -Force }
Copy-Item -Path $srcBin -Destination $hostDir -Recurse
if (-not (Test-Path $hostDll)) {
    Write-Error "Build the host first: dotnet build src/NetPI.Host -c Debug"
}

$env:NETPI_PLUGINS = Join-Path $root 'plugins'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "`"$hostDll`""
$psi.WorkingDirectory = $root
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

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
