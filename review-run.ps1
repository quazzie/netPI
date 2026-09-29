param([string]$Suite, [string[]]$Filters)
$ErrorActionPreference = 'Stop'
$reviewRoot = Join-Path $PSScriptRoot 'artifacts/review-temp'
New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
$env:TEMP = $reviewRoot
$env:TMP = $reviewRoot
& dotnet "tests/NetPI.$Suite.Tests/bin/Debug/NetPI.$Suite.Tests.dll" @Filters
exit $LASTEXITCODE
