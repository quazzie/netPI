#!/usr/bin/env bash
# Build NetPI on Linux/macOS (headless server + plugins; the WebView2 desktop shell is Windows-only).
#   ./build.sh            build web UI (if npm exists) + server + plugins + tests
#   ./build.sh --no-web   skip npm (prebuilt web/dist is committed)
#   ./build.sh --test     also run the unit suites
set -euo pipefail
cd "$(dirname "$0")"

WEB=1; TEST=0
for a in "$@"; do
  case "$a" in
    --no-web) WEB=0 ;;
    --test) TEST=1 ;;
    *) echo "unknown option $a"; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo ".NET 10 SDK not found"; exit 1; }

if [ "$WEB" = 1 ] && command -v npm >/dev/null; then
  echo "== web UI"
  [ -d node_modules ] || npm ci
  npm run build
fi

# plugins that were renamed (NetPI.Lanes is NetPI.Agents, NetPI.Agent is NetPI.Runtime): their old output would load next to the new one
rm -rf artifacts/app/plugins/NetPI.Lanes artifacts/app/plugins/NetPI.Agent

echo "== dotnet build (every project except the Windows desktop shell)"
B="dotnet build -nologo -v q -clp:ErrorsOnly -p:BuildProjectReferences=false"
$B src/NetPI.Abstractions/NetPI.Abstractions.csproj
$B src/NetPI.Host/NetPI.Host.csproj
$B src/NetPI.Server/NetPI.Server.csproj
for p in plugins/*/*.csproj tests/*/*.csproj; do
  case "$p" in tests/SamplePlugin/*) continue ;; esac
  $B "$p"
done

echo "Built artifacts/app/netpi-server (run it and open the printed URL)"

if [ "$TEST" = 1 ]; then
  for t in Providers Tools Agent Aux; do dotnet "tests/NetPI.$t.Tests/bin/Debug/NetPI.$t.Tests.dll"; done
  tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests
fi
