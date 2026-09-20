# Keeps the netPI host REPL alive by holding its stdin stream open (never writes,
# never closes). Run in background. Usage: pwsh tools/keep-alive-host.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$hostDll = Join-Path $root 'src/NetPI.Host/bin/Debug/net10.0/netPI.Host.dll'
$env:NETPI_PLUGINS = Join-Path $root 'plugins'
$env:NETPI_HOME = Join-Path $env:USERPROFILE '.netpi'

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
