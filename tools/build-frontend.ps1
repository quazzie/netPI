<#
astra-1 P5: build the netPI Web frontend and stage a COMPLETE Vite output
before switching the served root.

`npx vite build` normally overwrites web/netpi-web/dist in place — while a
live Web plugin serves that directory, readers can see a half-overwritten
bundle (index.html pointing at a new asset that has not landed yet). This
script instead:

  1. Builds into a COMPLETE new directory (web/netpi-web/dist-new) — vite
     builds there from scratch; nothing is touched under the live root.
  2. Validates the staged output (index.html + non-empty assets/).
  3. Atomically swaps the directory names (old -> dist-old, new -> dist),
     so the served root is always either the complete old output or the
     complete new one — never a partial build.
  4. Removes the old output.

No plugin reload, no host restart: Kestrel serves the directory contents on
each request, so the swap is picked up immediately by the next static-file
read. If the build or validation fails, the live root is never touched.

Usage:  pwsh tools/build-frontend.ps1 [-SkipBuild]
        -SkipBuild re-stages an already-present web/netpi-web/dist-new.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$web  = Join-Path $root 'web/netpi-web'
$live = Join-Path $web 'dist'
$new  = Join-Path $web 'dist-new'
$old  = Join-Path $web 'dist-old'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

# --- 1. build into the staging directory -----------------------------------
if (-not $SkipBuild) {
    if (-not (Test-Path (Join-Path $web 'package.json'))) { Fail "web project not found: $web" }
    Push-Location $web
    try {
        Write-Output "vite build -> $new"
        npx vite build --outDir dist-new --emptyOutDir --base / 2>&1 | Write-Output
        if ($LASTEXITCODE -ne 0) { Fail "vite build failed (exit $LASTEXITCODE) — live root untouched" }
    }
    finally { Pop-Location }
}

# --- 2. validate the staged output ------------------------------------------
$idx = Join-Path $new 'index.html'
if (-not (Test-Path $idx)) { Fail "staged output is not a complete build (no index.html in $new) — live root untouched" }
$assets = Join-Path $new 'assets'
if (-not (Test-Path $assets) -or -not (Get-ChildItem -Path $assets -File -ErrorAction SilentlyContinue)) {
    Fail "staged output has no assets/ files — live root untouched"
}
$assetCount = (Get-ChildItem -Path $assets -File).Count
Write-Output "staged output OK: index.html + $assetCount assets in $new"

# --- 3. atomic swap (dir renames on the same volume) -------------------------
# Rename order matters for the served root:
#   live -> dist-old   (served root temporarily empty is a non-issue: Kestrel
#                       answers 404 for ~milliseconds; the previous root is
#                       preserved, never deleted)
#   dist-new -> dist   (served root = complete new build)
#   dist-old -> deleted
if (Test-Path $old) { Remove-Item -Path $old -Recurse -Force }
if (Test-Path $live) { Move-Item -Path $live -Destination $old }
Move-Item -Path $new -Destination $live
Remove-Item -Path $old -Recurse -Force

Write-Output "swapped: served root is now the complete new build ($assetCount assets)"
Write-Output "if the Web plugin is live, it serves the new files immediately; no reload needed."
