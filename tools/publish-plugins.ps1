# Publishes each netPI plugin as an IMMUTABLE build (astra-1 P1).
#
# Per plugin (discovered by *.csproj marker under plugins/, not a hardcoded
# list):
#   dotnet publish  -> temp staging dir (TFM net10.0 from Directory.Build.props,
#                     no RID, framework-dependent class library)
#   + post-process  -> Kestrel class-lib plugins get a hand-written
#                      <asm>.runtimeconfig.json (framework Microsoft.AspNetCore.App)
#                      and NetPI.Storage.Sqlite gets e_sqlite3.dll at
#                      runtimes/win-x64/native/ for the host's native pre-load.
#   finalize        -> .artifacts/plugins/<id>/<buildId>/  (+ artifact.json manifest
#                      pinning every file by relative posix path + sha256 + size)
#   validate        -> parse the manifest, re-hash every listed file in place;
#                      on failure the pointer is NOT flipped and the artifact is
#                      left untouched (or cleaned up when it is new).
#   activate        -> atomically replace plugins/<id>/current.json (the build
#                      pointer the host resolves at discovery).
#   prune           -> keep the 3 newest artifact builds per plugin (locked ones
#                      are skipped, never a failure).
#
# buildId = 12 lower-hex of SHA-256 over lines "path\nsha256\n" (every artifact
# file, relative posix path, sorted by path Ordinal) — the exact algorithm of
# NetPI.Abstractions.PluginPublication.ComputeBuildId. Same bytes => same buildId
# => an existing artifact is reused (idempotent, no rewrite, no pointer flip).
#
# Outcomes (one line per plugin):
#   built <id>                          publish ran (a publish ran; see next line)
#   published <id> <buildId>            pointer moved (or first publish)
#   up-to-date <id> <buildId>           same bytes already published (no flip)
#   reloaded <id> <old> -> <new>        running host confirmed the new buildId
#   failed <id> <reason>                nothing was changed for this plugin
#
# Usage:
#   pwsh tools/publish-plugins.ps1 [-Configuration Debug] [-Plugins id,id]
#                                  [-IncludeTestPlugin] [-NoBuild] [-Reload]
param(
  [string]$Configuration = "Debug",
  [string[]]$Plugins = @(),
  [switch]$IncludeTestPlugin,
  [switch]$NoBuild,
  [switch]$Reload
)
$ErrorActionPreference = 'Stop'

$root       = Split-Path $PSScriptRoot -Parent
$pluginsDir = Join-Path $root 'plugins'
$artifacts  = Join-Path $root '.artifacts'

$env:DOTNET_ROOT = $env:DOTNET_ROOT ?? (Join-Path $env:USERPROFILE '.dotnet')
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

# Kestrel class-library plugins: emit no runtimeconfig.json, but the host's
# collectible ALC needs one declaring the ASP.NET Core shared framework.
$kestrel = @{
  'NetPI.Web'             = 'netPI.Web'
  'NetPI.Diagnostics'     = 'netPI.Diagnostics'
  'NetPI.BackgroundTasks' = 'netPI.BackgroundTasks'
}

function Run-DotNet([string[]]$DotNetArgs) {
  & dotnet @DotNetArgs 2>&1 | ForEach-Object { Write-Host "    $_" }
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet $($DotNetArgs[0]) $($DotNetArgs[1..($DotNetArgs.Count-1)] -join ' ') failed (exit $LASTEXITCODE)"
  }
}

function Get-FileSha256([string]$path) {
  (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
}

# Exact C# ComputeBuildId algorithm: sha256 over "path\nsha256\n" lines,
# sorted by path Ordinal, first 12 hex lower.
function Get-BuildId([string[]]$files, [string]$rootDir) {
  $lines = foreach ($f in $files) {
    $rel = $f.Substring($rootDir.Length).TrimStart('\','/').Replace('\','/')
    "$rel`n$(Get-FileSha256 $f)`n"
  }
  $sorted = $lines | Sort-Object { $_.Substring(0, $_.LastIndexOf("`n")) }   # sort by path
  $blob = -join $sorted
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($blob))) -replace '-',''
  $hash.ToLower().Substring(0, 12)
}

# Read the currently active build pointer for a plugin (null when absent).
function Get-CurrentBuild([string]$pluginDir, [string]$id) {
  $p = Join-Path $pluginDir "$id\current.json"
  if (-not (Test-Path $p)) { return $null }
  try { (Get-Content $p -Raw | ConvertFrom-Json).buildId } catch { return $null }
}

# Validate an artifact dir against its manifest (mirror of
# PluginPublication.ValidateArtifact: path safety, presence, size, sha256,
# entry assembly). Returns $null on success or an error string.
function Test-ArtifactValid([string]$artifactDir, [string]$entryAsm) {
  $manifestPath = Join-Path $artifactDir 'artifact.json'
  if (-not (Test-Path $manifestPath)) { return "manifest not found: $manifestPath" }
  $m = Get-Content $manifestPath -Raw | ConvertFrom-Json
  if ($m.buildId -ne $artifactDir.Split([IO.Path]::DirectorySeparatorChar)[-1]) {
    return "manifest buildId ($($m.buildId)) != directory name"
  }
  $root = [IO.Path]::GetFullPath($artifactDir)
  $entryRoot = [IO.Path]::GetFullPath((Join-Path $artifactDir $m.entryAssembly))
  if (-not $entryRoot.StartsWith("$root\", [System.StringComparison]::OrdinalIgnoreCase)) {
    return "entry assembly escapes artifact root: $($m.entryAssembly)"
  }
  if (-not (Test-Path $entryRoot)) { return "entry assembly missing from artifact: $($m.entryAssembly)" }
  foreach ($f in @($m.files)) {
    $rel = $f.path.Replace('\','/')
    if ($rel.StartsWith('/') -or $rel.StartsWith('../') -or $rel.Contains('/../') -or $rel.EndsWith('/..')) {
      return "unsafe relative path in artifact: '$rel'"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $root $rel))
    if (-not $full.StartsWith("$root\", [System.StringComparison]::OrdinalIgnoreCase)) {
      return "path escapes artifact root: '$rel'"
    }
    if (-not (Test-Path $full)) { return "listed file missing from artifact: '$rel'" }
    $size = (Get-Item $full).Length
    if ($size -ne $f.size) { return "size mismatch for '$rel': manifest $($f.size), disk $size" }
    $actual = Get-FileSha256 $full
    if ($actual -cne $f.sha256) { return "sha256 mismatch for '$rel': manifest $($f.sha256), disk $actual" }
  }
  return $null
}

function Test-HostUp([int]$port) {
  try {
    $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/identity" -UseBasicParsing -TimeoutSec 1
    return $true
  } catch { return $false }
}

# Ask a running host to reload one plugin and wait until its plugins.state
# reports the requested buildId (or a plugin failure). Returns an outcome
# line: "reloaded <id> <old> -> <new>" / "failed <id> <reason>" /
# "timeout <id> <reason>". Never throws for a rejected/deferred reload.
function Invoke-PluginReload([string]$id, [string]$newBuildId, [int]$port) {
  $ws = [System.Net.WebSockets.ClientWebSocket]::new()
  $ws.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(15)   # must be set before ConnectAsync
  try {
    $null = $ws.ConnectAsync("ws://127.0.0.1:$port/ws", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $buf = New-Object byte[] 65536

    $send = [System.Text.Encoding]::UTF8.GetBytes('{"type":"plugin.reload","payload":{"pluginId":"$id"}}')
    $ws.SendAsync([System.ArraySegment[byte]]::new($send, 0, $send.Length),
                  [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
                  [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

    $deadline = (Get-Date).AddSeconds(60)
    $seen = $null
    while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open -and (Get-Date) -lt $deadline) {
      $msg = $null
      $done = $false
      $sb = New-Object System.Text.StringBuilder
      do {
        $res = $ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count))
        $done = $res.EndOfMessage
      } while (-not $done)
      $msg = $sb.ToString()
      try { $j = $msg | ConvertFrom-Json } catch { continue }
      if ($j.type -eq 'plugins.state' -and $j.payload.plugins) {
        $p = @($j.payload.plugins) | Where-Object { $_.Id -ieq $id } | Select-Object -First 1
        if ($p) {
          if ($null -eq $seen) { $seen = $p }
          if ($p.buildId -eq $newBuildId) {
            return "reloaded $id $($seen.buildId) -> $newBuildId"
          }
          if ($p.state -eq 'failed') {
            return "failed $id reload: $($p.lastError)"
          }
        }
      }
      elseif ($j.type -eq 'plugin.reloadFailed') {
        return "failed $id reload rejected by host"
      }
    }
    return "timeout $id host on $port never reported buildId $newBuildId"
  }
  finally {
    try { $ws.Dispose() } catch { }
  }
}

# --- discovery (marker: a *.csproj under plugins/<id>/) --------------------------
$discovered = Get-ChildItem -Path $pluginsDir -Directory | Where-Object {
  Get-ChildItem -Path $_.FullName -Filter '*.csproj' -File -ErrorAction SilentlyContinue
} | ForEach-Object { $_.Name } | Sort-Object
if (-not $IncludeTestPlugin) { $discovered = @($discovered | Where-Object { $_ -ne 'NetPI.TestPlugin' }) }
if ($Plugins.Count -gt 0) { $discovered = @($discovered | Where-Object { $Plugins -contains $_ }) }
if ($discovered.Count -eq 0) { throw "no plugin projects discovered under $pluginsDir (selection: $($Plugins -join ','))" }

# Compatibility token: hash of the netPI.Abstractions.dll bytes (first 12 hex),
# the exact form of the manifest's abstractionsBuildId field.
$abstractionsId = ''
$abstractionsDll = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter 'netPI.Abstractions.dll' -File -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -notmatch '\\obj\\' } | Sort-Object Length -Descending | Select-Object -First 1
if ($abstractionsDll) {
  $abstractionsId = (Get-FileHash -Path $abstractionsDll.FullName -Algorithm SHA256).Hash.ToLower().Substring(0, 12)
} else {
  Write-Warning "netPI.Abstractions.dll not found — run 'dotnet build NetPI.sln -c $Configuration' first; manifest will carry an empty abstractionsBuildId"
}

# --- per-plugin publication --------------------------------------------------------
foreach ($id in $discovered) {
  $csproj = (Get-ChildItem -Path (Join-Path $pluginsDir $id) -Filter '*.csproj' -File | Select-Object -First 1).FullName
  $match = [regex]::Match((Get-Content $csproj -Raw), '<AssemblyName>\s*([^<]+?)\s*</AssemblyName>')
  if (-not $match.Success) { Write-Output "failed $id no <AssemblyName> in $(Split-Path $csproj -Leaf)"; continue }
  $asm = $match.Groups[1].Value.Trim()

  $oldBuild = Get-CurrentBuild -pluginDir $pluginsDir -id $id
  $staging  = Join-Path ([IO.Path]::GetTempPath()) "netpi-pub-$id-$([Guid]::NewGuid().ToString('n'))"

  try {
    # 1) build -> temp staging
    if (-not $NoBuild) {
      Run-DotNet @('publish', $csproj, '-c', $Configuration, '-o', $staging, '--nologo', '-v', 'q')
      Write-Output "built $id"
    } else {
      $existing = Get-ChildItem (Join-Path $artifacts "plugins\$id") -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
      if ($existing.Count -eq 0) { Write-Output "failed $id -NoBuild: no prior artifact under .artifacts/plugins/$id"; continue }
      $staging = $existing[0].FullName          # consume the last finalized artifact
      Write-Output "built $id (no-build: consuming existing artifact $(Split-Path $staging -Leaf))"
    }

    # 2) post-process the payload (BEFORE hashing — these bytes are the build)
    if ($kestrel.ContainsKey($id) -and -not (Test-Path (Join-Path $staging "$($kestrel[$id]).runtimeconfig.json"))) {
      @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "rollForward": "LatestMajor",
    "framework": { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
  }
}
'@ | Set-Content -Path (Join-Path $staging "$($kestrel[$id]).runtimeconfig.json") -Encoding ascii
    }
    if ($id -eq 'NetPI.Storage.Sqlite') {
      $native = Join-Path $staging 'runtimes/win-x64/native/e_sqlite3.dll'
      if (-not (Test-Path $native)) {
        $nuget = Join-Path $env:USERPROFILE '.nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.13/runtimes/win-x64/native/e_sqlite3.dll'
        if (-not (Test-Path $nuget)) {
          throw "e_sqlite3.dll missing from the publish output and from the nuget cache ($nuget)"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $native) | Out-Null
        Copy-Item $nuget $native
      }
    }

    # 3) buildId over every staged file (relative posix path + sha256)
    $payload = @(Get-ChildItem -Path $staging -Recurse -File)
    if ($payload.Count -eq 0) { throw "publish produced no files in $staging" }
    $buildId = Get-BuildId -files $payload.FullName -rootDir $staging

    # 4) finalize the immutable artifact dir (.artifacts/plugins/<id>/<buildId>/)
    $artifactDir = Join-Path $artifacts "plugins\$id\$buildId"
    $isNew = -not (Test-Path (Join-Path $artifactDir 'artifact.json'))
    if ($isNew) {
      New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
      foreach ($f in $payload) {
        $rel = $f.FullName.Substring($staging.Length).TrimStart('\','/')
        $dst = Join-Path $artifactDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item $f.FullName $dst -Force
      }
      $files = @()
      foreach ($f in (Get-ChildItem -Path $artifactDir -Recurse -File | Where-Object { $_.Name -ne 'artifact.json' })) {
        $rel = $f.FullName.Substring($artifactDir.Length).TrimStart('\','/').Replace('\','/')
        $files += [pscustomobject]@{
          path   = $rel
          sha256 = (Get-FileSha256 $f.FullName)
          size   = $f.Length
        }
      }
      $manifest = [ordered]@{
        schema              = 1
        plugin              = $id
        entryAssembly       = "$asm.dll"
        buildId             = $buildId
        targetFramework     = 'net10.0'
        runtimeIdentifier   = 'win-x64'
        abstractionsBuildId = $abstractionsId
        files               = @($files | Sort-Object { $_.path })
      }
      $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $artifactDir 'artifact.json') -Encoding ascii
    }

    # 5) validate the FINAL artifact in place (re-hash; failure = no pointer flip)
    $verr = Test-ArtifactValid -artifactDir $artifactDir -entryAsm "$asm.dll"
    if ($verr) {
      if ($isNew) { Remove-Item $artifactDir -Recurse -Force -ErrorAction SilentlyContinue }
      Write-Output "failed $id artifact validation: $verr — pointer not flipped"
      continue
    }

    # 6) activate: atomically replace plugins/<id>/current.json
    $pointer = [ordered]@{
      schema      = 1
      plugin      = $id
      buildId     = $buildId
      artifactDir = $artifactDir
      publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $pointerDir = Join-Path $pluginsDir $id
    $tmp = Join-Path $pointerDir "current.json.tmp-$([Guid]::NewGuid().ToString('n'))"
    $pointer | ConvertTo-Json -Depth 5 | Set-Content -Path $tmp -Encoding ascii
    $final = Join-Path $pointerDir 'current.json'
    if (Test-Path $final) { Move-Item $tmp $final -Force } else { Move-Item $tmp $final }

    # 7) prune: keep the 3 newest artifact builds per plugin (locked ones survive)
    $builds = Get-ChildItem (Join-Path $artifacts "plugins\$id") -Directory |
      Sort-Object LastWriteTime -Descending
    foreach ($d in $builds | Select-Object -Skip 3) {
      try { Remove-Item $d.FullName -Recurse -Force }
      catch { Write-Host "  prune-skipped $id $($d.Name) ($($_.Exception.Message))" }
    }

    # 8) outcome + optional reload confirmation
    if ($oldBuild -and $oldBuild -ne $buildId) {
      Write-Output "published $id $buildId (was $oldBuild)"
    }
    elseif ($oldBuild -eq $buildId) {
      Write-Output "up-to-date $id $buildId"
    }
    else {
      Write-Output "published $id $buildId"
    }
    if ($Reload) {
      if (Test-HostUp 5173) {
        Write-Output (Invoke-PluginReload -id $id -newBuildId $buildId -port 5173)
      } else {
        Write-Output "no host running — reload skipped"
      }
    }
  }
  catch {
    Write-Output "failed $id $($_.Exception.Message)"
  }
  finally {
    if (-not $NoBuild -and (Test-Path $staging)) {
      Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}
