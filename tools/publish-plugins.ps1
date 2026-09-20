# Stages each netPI plugin for host loading (PLAN §5: a plugin directory
# contains its DLL plus its private dependencies; the collectible ALC loads
# everything from that directory).
#
# Usage:  pwsh tools/publish-plugins.ps1 [-Configuration Debug]
param([string]$Configuration = "Debug",
        [string]$DotNetRoot)

if (-not $DotNetRoot) {
  $DotNetRoot = Join-Path $env:USERPROFILE ".dotnet"
}
$env:DOTNET_ROOT = $DotNetRoot
$env:PATH = "$DotNetRoot;$env:PATH"

$root = Split-Path $PSScriptRoot -Parent
$plugins = @("NetPI.Agent","NetPI.Context.Pi","NetPI.Provider.AiProxy",
             "NetPI.Storage.Sqlite","NetPI.AutoCompact","NetPI.Retry","NetPI.TestPlugin","NetPI.Tools","NetPI.BackgroundTasks","NetPI.Web","NetPI.Diagnostics")

foreach ($p in $plugins) {
  $dir = Join-Path $root "plugins/$p"
  $cfg = Join-Path $dir "bin/$Configuration/net10.0"
  if (-not (Test-Path $cfg)) {
    dotnet publish (Join-Path $dir "$p.csproj") -c $Configuration -o $dir --nologo -v q | Out-Null
  } else {
    Copy-Item (Join-Path $cfg "*.dll") $dir -Force
    Copy-Item (Join-Path $cfg "$p.deps.json") $dir -Force
  }
  # Web plugin: class-library build emits no runtimeconfig.json; the ALC needs
  # one declaring the ASP.NET Core shared framework.
  if ($p -eq "NetPI.Web" -or $p -eq "NetPI.Diagnostics") {
    $asm = if ($p -eq "NetPI.Web") { "netPI.Web" } else { "netPI.Diagnostics" }
    Set-Content -Path (Join-Path $dir "$asm.runtimeconfig.json") -Value @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "rollForward": "LatestMajor",
    "framework": { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
  }
}
'@
  }
  # Storage plugin: pre-position the SQLite native library so the host's
  # PreloadNativeAssets picks it up (P/Invoke cannot probe a collectible ALC).
  if ($p -eq "NetPI.Storage.Sqlite") {
    $nuget = Join-Path $env:USERPROFILE ".nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.13/runtimes/win-x64/native/e_sqlite3.dll"
    $dest  = Join-Path $dir "runtimes/win-x64/native"
    if ((Test-Path $nuget) -and -not (Test-Path $dest)) {
      New-Item -ItemType Directory -Force -Path $dest | Out-Null
      Copy-Item $nuget $dest
    }
  }
  Write-Host "staged $p"
}
