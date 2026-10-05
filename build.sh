#!/usr/bin/env bash
# Build NetPI on Linux/macOS: the headless server and the plugins. The WebView2 desktop shell, the windows tool's
# UI Automation helper and the window its tests drive target net10.0-windows and are skipped off Windows.
#
# A build lands in artifacts/dev/app and nothing else: a running netpi-server loads its plugins from artifacts/app and
# reloads one as soon as that file changes, so building must not write there. Installing is a separate step.
#
#   ./build.sh                          build (Release) into artifacts/dev/app: a running server is not touched
#   ./build.sh --test                   also run the six unit suites: every suite runs, and the script fails if any did
#   ./build.sh --no-web                 skip npm (the built web/dist and plugins/*/wwwroot/ui.js are committed)
#   ./build.sh --configuration Debug    Debug instead of Release (the suites are then under bin/Debug)
#   ./build.sh --publish                install the build into artifacts/app, or into the folder a running server says
#                                       it runs from (server.json's appDir). A running server hot-reloads the plugins
#                                       whose files changed (every chat holding one of their tools gets a notice); the
#                                       host files it has loaded are moved into .old and the new ones put in place, so
#                                       its next start runs the new host; after a contract change the rebuilt plugins
#                                       wait in .pending for that start (the host installs .pending before it loads)
#   ./build.sh --publish --next-start   the running server gets nothing: its plugins and the web UI wait in .pending.
#                                       The host files are installed now, as above (the host never installs them
#                                       from .pending); a restart brings it all
#   ./build.sh --pending                what the next start would install
#   ./build.sh --discard                drop the staged install
#   ./build.sh --app-dir DIR            install into DIR instead (without it: the running server's own folder when
#                                       server.json names one, else artifacts/app)
set -euo pipefail
cd "$(dirname "$0")"

WEB=1; TEST=0; PUBLISH=0; NEXTSTART=0; PENDING=0; DISCARD=0; APP_DIR=""; CONFIG=Release
while [ $# -gt 0 ]; do
  case "$1" in
    --no-web) WEB=0 ;;
    --test) TEST=1 ;;
    --publish) PUBLISH=1 ;;
    --next-start) NEXTSTART=1; PUBLISH=1 ;;
    --pending) PENDING=1 ;;
    --discard) DISCARD=1 ;;
    --app-dir) [ $# -ge 2 ] || { echo "--app-dir needs a directory" >&2; exit 2; }; APP_DIR=$2; shift ;;
    --app-dir=*) APP_DIR=${1#--app-dir=} ;;
    --configuration|-c) [ $# -ge 2 ] || { echo "--configuration needs Debug or Release" >&2; exit 2; }; CONFIG=$2; shift ;;
    --configuration=*) CONFIG=${1#--configuration=} ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
case "$CONFIG" in Debug|Release) ;; *) echo "--configuration must be Debug or Release, not $CONFIG" >&2; exit 2 ;; esac

# ---- helpers
# the absolute, link-free form of a folder (a folder that does not exist yet keeps its spelling)
abs() { if [ -d "$1" ]; then (cd "$1" && pwd -P); else case "$1" in /*) echo "$1" ;; *) echo "$PWD/$1" ;; esac; fi; }
# a top-level string / integer member of a JSON file the host writes (one member per line, as it indents them)
json_str() { sed -n 's/^[[:space:]]*"'"$2"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*$/\1/p' "$1" | head -n 1 || true; }
json_int() { sed -n 's/^[[:space:]]*"'"$2"'"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*$/\1/p' "$1" | head -n 1 || true; }

DEV=artifacts/dev/app
SERVER_FILE="${NETPI_HOME:-$HOME/.netpi}/server.json"

# Where to install. The app that is *running* says where it is (server.json's appDir), which is not this repository's
# artifacts/app when the build happens in a worktree - installing into a folder nothing watches is the silent no-op that
# makes "I published" a lie. --app-dir overrides both.
APP=artifacts/app; APP_NOTE=""
if [ -n "$APP_DIR" ]; then APP=$APP_DIR
elif [ -f "$SERVER_FILE" ]; then
  dir=$(json_str "$SERVER_FILE" appDir)
  if [ -n "$dir" ] && [ -d "$dir" ]; then
    APP=$dir
    if [ "$(abs "$dir")" != "$(abs artifacts/app)" ]; then APP_NOTE="A NetPI is running from $dir (not this repository): installing there."; fi
  fi
fi
PENDING_DIR="$APP/.pending"; OLD_DIR="$APP/.old"; LOCK="$APP/.install.lock"

# ---- is a server running from $APP? Nothing on Linux locks a loaded file (build.ps1 tells by the locked DLLs), so the
# running one says so itself: server.json holds its pid and appDir, written at start and removed at exit.
RUNNING=0; RUNNING_PID=""
if [ -f "$SERVER_FILE" ]; then
  pid=$(json_int "$SERVER_FILE" pid); dir=$(json_str "$SERVER_FILE" appDir)
  if [ -n "$pid" ] && [ -n "$dir" ] && [ "$(abs "$dir")" = "$(abs "$APP")" ] && kill -0 "$pid" 2>/dev/null; then
    RUNNING=1; RUNNING_PID=$pid
  fi
fi

# ---- pending / discard: what a restart would install, and dropping it
if [ "$PENDING" = 1 ] || [ "$DISCARD" = 1 ]; then
  if [ "$DISCARD" = 1 ]; then
    if [ -d "$PENDING_DIR" ]; then rm -rf "$PENDING_DIR"; echo "Dropped $PENDING_DIR"; else echo "Nothing was staged."; fi
    [ -d "$OLD_DIR" ] && echo "$OLD_DIR keeps the host files a running server has open; they go on their own."
    exit 0
  fi
  if [ ! -d "$PENDING_DIR" ]; then
    echo "Nothing is staged: a restart brings the build already in $APP."
  else
    echo "Waiting in $PENDING_DIR for the next start:"
    (cd "$PENDING_DIR" && find . -type f | sed 's|^\./||' | sort |
      while IFS= read -r f; do printf '  %s (%s bytes)\n' "$f" "$(wc -c < "$f" | tr -d ' ')"; done)
  fi
  if [ -d "$OLD_DIR" ]; then
    n=$(find "$OLD_DIR" -type f | wc -l | tr -d ' ')
    [ "$n" -gt 0 ] && echo "  .old: $n host file(s) a restart replaces"
  fi
  exit 0
fi

command -v dotnet >/dev/null || { echo ".NET 10 SDK not found"; exit 1; }

# ---- the tree we build from: its index must be HEAD (a branch moved from another worktree does not update this
# tree, and a commit there would revert those commits). Advisory, and only in the main checkout.
if command -v git >/dev/null && git rev-parse --absolute-git-dir >/dev/null 2>&1; then
  GIT_DIR=$(git rev-parse --absolute-git-dir); TOP=$(git rev-parse --show-toplevel)
  if [ "$(dirname "$GIT_DIR")" = "$(dirname "$TOP")" ] && ! git diff --cached --quiet; then
    echo ""
    echo "This checkout's index is not HEAD: $(git diff --cached --name-only | wc -l) file(s) are staged here." >&2
    echo "  A 'git commit' here would commit those, not HEAD - they are usually the reverse of commits" >&2
    echo "  that landed since (a branch moved from another worktree does not update this tree)." >&2
    echo "  Move the branch with:  git -C \"$TOP\" merge --ff-only <branch>" >&2
    echo "  (it moves ref, index and tree together, and refuses while the tree has changes)." >&2
    echo ""
  fi
fi

if [ "$WEB" = 1 ] && command -v npm >/dev/null; then
  echo "== web UI"
  [ -d node_modules ] || npm ci
  # the bundles go to the source folders; the .NET build copies them into its output, and the publish below installs
  # them with everything else. Never into the app folder from here: that is before the install lock.
  NETPI_NO_COPY=1 NETPI_COPY= npm run build
fi

echo "== dotnet build ($CONFIG) into $DEV"
rm -rf "$DEV"
B="dotnet build -nologo -v q -clp:ErrorsOnly -c $CONFIG -p:BuildProjectReferences=false"
$B src/NetPI.Abstractions/NetPI.Abstractions.csproj
$B src/NetPI.Contracts/NetPI.Contracts.csproj
$B src/NetPI.Host/NetPI.Host.csproj
$B src/NetPI.Server/NetPI.Server.csproj
# Projects that target net10.0-windows (tests/NetPI.WinTestApp, like src/NetPI.Desktop and src/NetPI.WindowsAgent, which
# are not in this loop) need the Windows targeting pack, which a non-Windows SDK refuses (NETSDK1100): skipped off
# Windows. A plugin references the windows helper only under '$(OS)' == 'Windows_NT', so the plugins build everywhere.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ON_WINDOWS=1 ;; *) ON_WINDOWS=0 ;; esac
for p in plugins/*/*.csproj tests/*/*.csproj; do
  case "$p" in tests/SamplePlugin/*) continue ;; esac   # the Host suite builds it itself, per variant
  if [ "$ON_WINDOWS" = 0 ] && grep -q '<TargetFramework>net10.0-windows' "$p"; then
    echo "   skipped $p (Windows only)"
    continue
  fi
  $B "$p"
done
echo "Built $DEV/netpi-server (run it and open the printed URL)"

# ---- install into the app folder (only --publish)
if [ "$PUBLISH" = 1 ]; then
  echo "== publish (install into $APP)"
  [ -n "$APP_NOTE" ] && echo "$APP_NOTE"

  # One install at a time: two agents publishing into the same app folder would otherwise interleave file by file and
  # leave a mix of two builds. The lock is the file build.ps1 takes (.install.lock, created exclusively, so the two scripts
  # exclude each other on a shared folder); a publisher that died leaves it behind, and it is taken over once its pid
  # is gone or the file is a couple of minutes old (build.ps1 holds its own open, so a live one can't be deleted there).
  enter_install_lock() {
    mkdir -p "$APP"
    local waited=0 holder
    while :; do
      if (set -C; printf 'pid %s, since %s\n' "$$" "$(date +%Y-%m-%dT%H:%M:%S%z)" > "$LOCK") 2>/dev/null; then return 0; fi
      holder=$(sed -n 's/^pid \([0-9][0-9]*\),.*/\1/p' "$LOCK" 2>/dev/null | head -n 1 || true)
      if { [ -n "$holder" ] && ! kill -0 "$holder" 2>/dev/null; } || [ -n "$(find "$LOCK" -mmin +1 2>/dev/null)" ]; then
        rm -f "$LOCK"
        continue
      fi
      if [ "$waited" -ge 120 ]; then echo "Another publish has held $LOCK for two minutes. If no build is running, delete it." >&2; return 1; fi
      [ $((waited % 15)) -eq 0 ] && echo "Another publish is installing into $APP; waiting..."
      sleep 1; waited=$((waited + 1))
    done
  }
  enter_install_lock
  trap 'rm -f "$LOCK"' EXIT   # whatever the install does: a failed one must not hold the next publisher

  if [ "$RUNNING" = 0 ]; then
    # nothing holds the files: the whole tree, as a normal build would. Plugins that were renamed (NetPI.Lanes is
    # NetPI.Agents, NetPI.Agent is NetPI.Runtime) would load next to the new ones; .old and .pending belong to a
    # running server, and there is none.
    rm -rf "$APP/plugins/NetPI.Lanes" "$APP/plugins/NetPI.Agent" "$PENDING_DIR" "$OLD_DIR"
    mkdir -p "$APP"
    # which plugins this install changes, while the app folder still holds the old ones
    plugins=$(find "$DEV/plugins" -type f 2>/dev/null | sed "s|^$DEV/||" | sort | while IFS= read -r rel; do
      if [ ! -f "$APP/$rel" ] || ! cmp -s "$DEV/$rel" "$APP/$rel"; then echo "$rel" | cut -d/ -f2; fi
    done | sort -u)
    find "$DEV" -type f | sed "s|^$DEV/||" | while IFS= read -r rel; do
      mkdir -p "$APP/$(dirname "$rel")"
      cp "$DEV/$rel" "$APP/$rel"
    done
    # what the dev tree no longer has goes too (a plugin deleted from the repository, old hashed wwwroot assets): only
    # under plugins/ and wwwroot/, never .old, .pending, the lock or anything else beside them
    for top in plugins wwwroot; do
      [ -d "$APP/$top" ] || continue
      (cd "$APP" && find "$top" -type f) | while IFS= read -r rel; do
        if [ ! -f "$DEV/$rel" ]; then rm -f "$APP/$rel"; echo "  removed $rel (no longer built)"; fi
      done
      find "$APP/$top" -mindepth 1 -type d -empty -delete 2>/dev/null || true
    done
    n=$(printf '%s\n' "$plugins" | grep -c . || true)
    echo "Installed into $APP ($n plugin(s) changed: $(if [ "$n" -gt 0 ]; then printf '%s\n' "$plugins" | paste -sd, - | sed 's/,/, /g'; else echo none; fi))."
    [ "$NEXTSTART" = 1 ] && echo "Nothing runs from $APP, so nothing had to wait: the next start runs this build."
  else
    echo "NetPI is running from $APP (pid $RUNNING_PID): the install is file by file, so a failed one changes nothing."

    # the contracts the running server loaded: NetPI.Abstractions.dll and NetPI.Contracts.dll in the app folder, or in
    # .old when a publish during its life moved them aside (the host empties .old at every start). A built contract that
    # differs from any of them is a contract change, and the rebuilt plugins then wait for the next start: hot-reloaded,
    # the running server would load them onto its old contracts. Taken before anything below moves a file.
    new_contracts=0
    for name in NetPI.Abstractions.dll NetPI.Contracts.dll; do
      while IFS= read -r f; do
        if [ -n "$f" ] && ! cmp -s "$DEV/$name" "$f"; then new_contracts=1; fi
      done < <({ [ -f "$APP/$name" ] && echo "$APP/$name"; [ -d "$OLD_DIR" ] && find "$OLD_DIR" -type f -name "$name"; } || true)
    done

    # what really changed (a deterministic build rewrites unchanged outputs byte for byte): those are the only files a
    # running server reloads a plugin for, and the only host files worth moving aside
    changed=$(find "$DEV" -type f | sed "s|^$DEV/||" | sort | while IFS= read -r rel; do
      if [ ! -f "$APP/$rel" ] || ! cmp -s "$DEV/$rel" "$APP/$rel"; then printf '%s\n' "$rel"; fi
    done)
    host_changed=$(printf '%s\n' "$changed" | grep -E -v '^(plugins|wwwroot)/' | grep . || true)
    web_changed=$(printf '%s\n' "$changed" | grep -E '^wwwroot/' || true)
    plugins=$(printf '%s\n' "$changed" | grep -E '^plugins/' | cut -d/ -f2 | sort -u | grep . || true)

    # host: each changed file the server has loaded is moved into .old (the running server keeps using it: a loaded
    # executable or assembly is renamed, never overwritten in place) and the new one copied in. All or nothing: if a
    # file can't be moved, the ones already moved go back. The host deletes .old at its next start.
    aside="$OLD_DIR/$(date +%Y%m%d-%H%M%S)"
    moved=""
    while IFS= read -r rel; do
      [ -n "$rel" ] && [ -f "$APP/$rel" ] || continue
      mkdir -p "$aside/$(dirname "$rel")"
      if mv "$APP/$rel" "$aside/$rel"; then moved="$moved$rel"$'\n'
      else
        printf '%s' "$moved" | while IFS= read -r m; do [ -n "$m" ] && mv "$aside/$m" "$APP/$m"; done
        echo "Could not replace $rel while NetPI runs: $APP is unchanged. Close NetPI and publish again." >&2
        exit 1
      fi
    done <<< "$host_changed"
    while IFS= read -r rel; do
      [ -n "$rel" ] || continue
      mkdir -p "$APP/$(dirname "$rel")"
      cp "$DEV/$rel" "$APP/$rel"
    done <<< "$host_changed"
    restart=$(printf '%s\n' "$host_changed" | grep -E '(\.(dll|json)|^netpi-server)$' || true)

    # --next-start: the host installs .pending/plugins/<name> and .pending/wwwroot at its next start as WHOLE folders (it
    # moves the staged folder over the installed one), so a staged plugin or web UI must be complete: changed files only
    # would leave the plugin without its plugin.json and the UI without the other hashed chunks. Plugins built against
    # new contracts are staged the same way without --next-start. This publish supersedes an earlier one's.
    rm -rf "$PENDING_DIR"
    dest="$PENDING_DIR"
    plugins_wait=0
    if [ "$NEXTSTART" = 1 ] || [ "$new_contracts" = 1 ]; then plugins_wait=1; fi
    while IFS= read -r rel; do
      case "$rel" in plugins/*) ;; *) continue ;; esac
      [ "$plugins_wait" = 1 ] && continue
      mkdir -p "$APP/$(dirname "$rel")"
      cp "$DEV/$rel" "$APP/$rel"
    done <<< "$changed"
    if [ "$plugins_wait" = 1 ] && [ -n "$plugins" ]; then
      mkdir -p "$dest/plugins"
      for name in $plugins; do cp -R "$DEV/plugins/$name" "$dest/plugins/$name"; done
    fi
    if [ -n "$web_changed" ]; then
      if [ "$NEXTSTART" = 1 ]; then
        mkdir -p "$dest"
        cp -R "$DEV/wwwroot" "$dest/wwwroot"
      else
        # replaced as a whole, like a normal build does (a file being served may stay; it is overwritten then)
        rm -rf "$APP/wwwroot"
        cp -R "$DEV/wwwroot" "$APP/wwwroot"
      fi
    fi

    echo
    names=$(printf '%s\n' "$plugins" | paste -sd, - | sed 's/,/, /g')
    if [ -n "$plugins" ] && [ "$plugins_wait" = 0 ]; then
      echo "Plugins updated in place (the running server reloads them; chats holding their tools get a notice): $names"
    elif [ -n "$plugins" ]; then
      if [ "$new_contracts" = 1 ]; then why="The contracts changed: the running server keeps its plugins"; else why="The running server keeps its plugins"; fi
      echo "$why; the rebuilt ones wait in $PENDING_DIR for its next start: $names"
    fi
    if [ -n "$web_changed" ] && [ "$NEXTSTART" = 1 ]; then echo "The new web UI waits in $PENDING_DIR for the next start."; fi
    if [ -n "$restart" ] || { [ -d "$OLD_DIR" ] && [ -n "$(find "$OLD_DIR" -type f 2>/dev/null | head -n 1 || true)" ]; }; then
      what=""
      if [ -n "$restart" ]; then what=" The new host: $(printf '%s\n' "$restart" | paste -sd, - | sed 's/,/, /g')."; fi
      echo "Ready for the next start: stop the server and start it again ($APP/netpi-server).$what"
    fi
    echo "Installed into $APP (the running app). $DEV is what you just built; ./build.sh --pending lists what a restart waits for."
  fi
  rm -f "$LOCK"; trap - EXIT   # released here, not at exit: --test must not hold the next publisher
else
  echo
  echo "The running app was not touched: $APP is the installed build, $DEV is this one."
  echo "Install it with ./build.sh --publish (now) or --publish --next-start (at the next start)."
fi

if [ "$TEST" = 1 ]; then
  echo "== unit tests ($CONFIG)"
  # the suites load the built plugins from here (NETPI_APP_DIR); a worktree has no artifacts/app to fall back on. The
  # suites are under bin/$CONFIG, what the build above made. Every suite runs; the failures are counted at the end.
  export NETPI_APP_DIR="$PWD/$DEV"
  failed=0
  for t in Providers Tools Agent Aux Host Storage; do
    echo "-- $t"
    dotnet "tests/NetPI.$t.Tests/bin/$CONFIG/NetPI.$t.Tests.dll" || failed=$((failed + 1))
  done
  unset NETPI_APP_DIR
  if [ "$failed" -gt 0 ]; then echo "$failed test suite(s) failed" >&2; exit 1; fi
fi
