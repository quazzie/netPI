#!/usr/bin/env bash
# Build NetPI on Linux/macOS (headless server + plugins; the WebView2 desktop shell is Windows-only).
#
# A build lands in artifacts/dev/app and nothing else: a running netpi-server loads its plugins from artifacts/app and
# reloads one as soon as that file changes, so building must not write there. Installing is a separate step.
#
#   ./build.sh                          build into artifacts/dev/app: a running server is not touched
#   ./build.sh --test                   also run the unit suites
#   ./build.sh --no-web                 skip npm (prebuilt web/dist is committed)
#   ./build.sh --publish                install the build into artifacts/app (a running server hot-reloads the plugins
#                                       whose files changed, and every chat holding one of their tools gets a notice)
#   ./build.sh --publish --next-start   stage the install in artifacts/app/.pending; the next start of the server picks
#                                       it up (the host installs .pending before it loads anything)
#   ./build.sh --pending                what the next start would install
#   ./build.sh --discard                drop the staged install
#   ./build.sh --app-dir DIR            install into DIR instead of artifacts/app (point it at the running server's
#                                       own folder - server.json's appDir - when building in a worktree)
set -euo pipefail
cd "$(dirname "$0")"

WEB=1; TEST=0; PUBLISH=0; NEXTSTART=0; PENDING=0; DISCARD=0; APP_DIR=""
for a in "$@"; do
  case "$a" in
    --no-web) WEB=0 ;;
    --test) TEST=1 ;;
    --publish) PUBLISH=1 ;;
    --next-start) NEXTSTART=1; PUBLISH=1 ;;
    --pending) PENDING=1 ;;
    --discard) DISCARD=1 ;;
    --app-dir) APP_DIR="${2:-}"; shift ;;
    --app-dir=*) APP_DIR="${a#--app-dir=}" ;;
    *) echo "unknown option $a"; exit 2 ;;
  esac
done

DEV=artifacts/dev/app
APP=${APP_DIR:-artifacts/app}
PENDING_DIR="$APP/.pending"

# ---- pending / discard: what a restart would install, and dropping it
if [ "$PENDING" = 1 ] || [ "$DISCARD" = 1 ]; then
  if [ "$DISCARD" = 1 ]; then
    if [ -d "$PENDING_DIR" ]; then rm -rf "$PENDING_DIR"; echo "Dropped $PENDING_DIR"; else echo "Nothing was staged."; fi
    [ -d "$APP/.old" ] && echo "$APP/.old keeps the host files a running server has open; they go on their own."
    exit 0
  fi
  if [ ! -d "$PENDING_DIR" ]; then
    echo "Nothing is staged: a restart brings the build already in $APP."
  else
    echo "Waiting in $PENDING_DIR for the next start:"
    (cd "$PENDING_DIR" && find . -type f | sed 's|^\./||' | sort |
      while IFS= read -r f; do printf '  %s (%s bytes)\n' "$f" "$(wc -c < "$f" | tr -d ' ')"; done)
  fi
  exit 0
fi

command -v dotnet >/dev/null || { echo ".NET 10 SDK not found"; exit 1; }

if [ "$WEB" = 1 ] && command -v npm >/dev/null; then
  echo "== web UI"
  [ -d node_modules ] || npm ci
  NETPI_COPY= npm run build   # the bundles go to the source folders; the .NET build copies them into its output
fi

echo "== dotnet build (every project except the Windows desktop shell) into $DEV"
rm -rf "$DEV"
B="dotnet build -nologo -v q -clp:ErrorsOnly -p:BuildProjectReferences=false"
$B src/NetPI.Abstractions/NetPI.Abstractions.csproj
$B src/NetPI.Contracts/NetPI.Contracts.csproj
$B src/NetPI.Host/NetPI.Host.csproj
$B src/NetPI.Server/NetPI.Server.csproj
for p in plugins/*/*.csproj tests/*/*.csproj; do
  case "$p" in tests/SamplePlugin/*) continue ;; esac
  $B "$p"
done
echo "Built $DEV/netpi-server (run it and open the printed URL)"

# ---- install into artifacts/app (only --publish)
if [ "$PUBLISH" = 1 ]; then
  echo "== publish (install into $APP)"
  # plugins that were renamed (NetPI.Lanes is NetPI.Agents, NetPI.Agent is NetPI.Runtime): their old output would load
  # next to the new one
  rm -rf "$APP/plugins/NetPI.Lanes" "$APP/plugins/NetPI.Agent"
  rm -rf "$PENDING_DIR"   # this publish supersedes an earlier one's

  # what really changed (a deterministic build rewrites unchanged outputs byte for byte): those are the only files a
  # running server reloads a plugin for
  changed=""
  while IFS= read -r rel; do
    [ -f "$APP/$rel" ] && cmp -s "$DEV/$rel" "$APP/$rel" || changed="$changed$rel"$'\n'
  done < <(cd "$DEV" && find . -type f | sed 's|^\./||')

  # --next-start: the host installs .pending/plugins/<name> and .pending/wwwroot at its next start as WHOLE folders (it
  # moves the staged folder over the installed one), so a staged plugin or web UI must be complete: changed files only
  # would leave the plugin without its plugin.json and the UI without the other hashed chunks.
  dest="$APP"
  if [ "$NEXTSTART" = 1 ]; then dest="$PENDING_DIR"; mkdir -p "$PENDING_DIR"; fi
  plugins=""
  web_changed=0
  while IFS= read -r rel; do
    [ -z "$rel" ] && continue
    case "$rel" in
      plugins/*)
        plugins="$plugins$(echo "$rel" | cut -d/ -f2)"$'\n'
        if [ "$NEXTSTART" = 1 ]; then continue; fi
        ;;
      wwwroot/*)
        web_changed=1
        if [ "$NEXTSTART" = 1 ]; then continue; fi
        ;;
    esac
    mkdir -p "$dest/$(dirname "$rel")"
    cp "$DEV/$rel" "$dest/$rel"
  done <<< "$changed"

  if [ "$NEXTSTART" = 1 ]; then
    for name in $(echo "$plugins" | sort -u); do
      mkdir -p "$dest/plugins"
      cp -R "$DEV/plugins/$name" "$dest/plugins/$name"
    done
    if [ "$web_changed" = 1 ]; then cp -R "$DEV/wwwroot" "$dest/wwwroot"; fi
  fi

  if [ -n "$plugins" ]; then
    echo "Plugins: $(echo "$plugins" | sort -u | paste -sd, - | sed 's/,/, /g')"
  fi
  if [ "$NEXTSTART" = 1 ]; then
    echo "The install waits in $PENDING_DIR for the next start of the server." \
      "Check with 'node scripts/netpi.mjs diag.overview' what is running, or ./build.sh --pending."
  else
    echo "Installed into $APP. A running server reloads the changed plugins; chats holding their tools get a notice."
  fi
else
  echo
  echo "The running app was not touched: $APP is the installed build, $DEV is this one."
  echo "Install it with ./build.sh --publish (now) or --publish --next-start (at the next start)."
fi

if [ "$TEST" = 1 ]; then
  for t in Providers Tools Agent Aux Storage; do dotnet "tests/NetPI.$t.Tests/bin/Debug/NetPI.$t.Tests.dll"; done
  tests/NetPI.Host.Tests/bin/Debug/NetPI.Host.Tests
fi
