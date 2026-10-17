#!/usr/bin/env bash
# End-to-end tests: builds the plugin, starts a real Jellyfin in Docker with it installed and some sample
# media, then runs e2e.mjs against it.
#
#   tests/e2e/run.sh                       build, start, test, stop; the test data is deleted when everything passed
#   KEEP=1 tests/e2e/run.sh                leave Jellyfin running afterwards, with its data, on http://127.0.0.1:18096
#   tests/e2e/run.sh "plugin" "cover"      run only the checks whose names contain one of these words (the checks
#                                          that set the server up always run); a word that matches no check fails
#   PORT=18100 tests/e2e/run.sh            use another port on this machine (it only listens on 127.0.0.1)
#   JELLYFIN_IMAGE=jellyfin/jellyfin:12.1 tests/e2e/run.sh    test against another Jellyfin
#
# Two runs at the same time need their own container name, port and work folder, for example
#   CONTAINER_NAME=lcm-e2e-b PORT=18100 WORK=/tmp/lcm-e2e-b tests/e2e/run.sh
# and a second run from the same checkout would also share the plugin project's bin/ and obj/ folders.
#
# Needs: the .NET 10 SDK, docker (a reachable daemon), ffmpeg (see make-media.sh), curl, node 20 or later, and for
# the page checks Playwright with its Chromium (`playwright install chromium`). Playwright is found through
# NODE_PATH; when NODE_PATH is not set and a `playwright` command is on PATH (a global npm install, or mise's
# npm:playwright), the node_modules folder it belongs to is used. The first run also builds a small image from
# alpine:3.22 with ImageMagick and libwebp's tools (see e2e.mjs), which needs the internet once.
#
# Where the .NET SDK comes from: DOTNET holds the command, split on spaces, and defaults to "dotnet", which is
# what CI has after setup-dotnet. On a machine whose SDKs come from mise:
#   DOTNET="mise exec dotnet@10.0.400 -- dotnet" tests/e2e/run.sh
#
# What a run touches: the container named CONTAINER_NAME (lcm-e2e) and the folder tests/e2e/.work (or WORK), both
# replaced at the start of a run and never removed when this script did not make them, and the bin/ and obj/
# folders of the plugin project, which `dotnet publish` writes (git ignores them). When a run does not pass, the
# test data stays in the work folder (Jellyfin's own log files are in its config/log) and its path is printed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
E2E="$ROOT/tests/e2e"
WORK="${WORK:-$E2E/.work}"
# A fixed image (Jellyfin 12.2), so a run only changes when the code or this line does.
PINNED_IMAGE=jellyfin/jellyfin:12.2@sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037
IMAGE="${JELLYFIN_IMAGE:-$PINNED_IMAGE}"
NAME="${CONTAINER_NAME:-lcm-e2e}"
PORT="${PORT:-18096}"
LABEL=org.library-cover-maker.e2e
MARKER=.library-cover-maker-e2e-work
read -r -a DOTNET_CMD <<<"${DOTNET:-dotnet}"

die() { echo "run.sh: $*" >&2; exit 1; }

# --- Cleanup ------------------------------------------------------------------------------------------

container_made=0   # set before the container is asked for, so one that was made but never started is removed too
work_made=0
node_pid=

# The EXIT trap below runs this function. Once a script ends with an exit, shellcheck loses track of that: 0.9
# (the Ubuntu 24.04 package) calls the body unreachable (SC2317), 0.11 calls the function never invoked (SC2329).
# shellcheck disable=SC2317,SC2329
cleanup() {
  local code=$?
  if [ -n "$node_pid" ] && kill -0 "$node_pid" 2>/dev/null; then
    kill "$node_pid" 2>/dev/null || true
    wait "$node_pid" 2>/dev/null || true
  fi
  if [ "${KEEP:-0}" = 1 ]; then
    if [ "$container_made" = 1 ] \
      && [ "$(docker container inspect -f '{{.State.Running}}' "$NAME" 2>/dev/null)" = true ]; then
      echo "== KEEP=1: Jellyfin stays up on http://127.0.0.1:$PORT (admin account as in e2e.mjs), data in $WORK"
      echo "   run the checks again with:"
      echo "   JF=http://127.0.0.1:$PORT CONTAINER=$NAME WORK=$WORK node tests/e2e/e2e.mjs [names]"
      echo "   stop it with: docker rm -f $NAME"
    fi
    return
  fi
  # Only a container this script made (it carries the label) is removed, whatever happened to the name.
  if [ "$container_made" = 1 ] \
    && [ "$(docker container inspect -f "{{index .Config.Labels \"$LABEL\"}}" "$NAME" 2>/dev/null)" = 1 ]; then
    docker rm -f "$NAME" >/dev/null 2>&1 || true
  fi
  if [ "$work_made" = 1 ]; then
    if [ "$code" = 0 ]; then
      rm -rf -- "$WORK"
    else
      echo "== The run did not pass, so the test data stays in $WORK" \
        "(Jellyfin's own log files are in $WORK/config/log). The next run replaces it."
    fi
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# --- What this machine needs -----------------------------------------------------------------------------

for tool in docker ffmpeg ffprobe curl node; do
  command -v "$tool" >/dev/null || die "$tool is not installed or not on PATH"
done
docker info >/dev/null 2>&1 \
  || die "cannot reach the Docker daemon: is it running, and may this user use it? ('docker info' fails)"
node -e 'process.exit(Number(process.versions.node.split(".")[0]) >= 20 ? 0 : 1)' \
  || die "node 20 or later is needed, found $(node --version)"
sdk="$("${DOTNET_CMD[@]}" --version 2>/dev/null)" \
  || die "cannot run '${DOTNET_CMD[*]}'. Set DOTNET to a command that runs the .NET 10 SDK," \
    "for example DOTNET='mise exec dotnet@10.0.400 -- dotnet'"
case "$sdk" in
  10.*) ;;
  *) die "the plugin needs the .NET 10 SDK, but '${DOTNET_CMD[*]}' is $sdk" ;;
esac

# --- What is left from an earlier run --------------------------------------------------------------------

if docker container inspect "$NAME" >/dev/null 2>&1; then
  [ "$(docker container inspect -f "{{index .Config.Labels \"$LABEL\"}}" "$NAME")" = 1 ] \
    || die "a container named $NAME exists that this script did not start. Remove it yourself, or set CONTAINER_NAME"
  docker rm -f "$NAME" >/dev/null
fi
if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
  die "port $PORT on this machine is already in use. Set PORT to a free one"
fi
if [ -e "$WORK" ]; then
  if [ -n "$(ls -A "$WORK")" ] && [ ! -f "$WORK/$MARKER" ]; then
    die "$WORK exists and was not made by this script. Remove it yourself, or set WORK to another path"
  fi
  rm -rf -- "$WORK"
fi
mkdir -p "$WORK/config/plugins/LibraryCoverMaker" "$WORK/cache"
: > "$WORK/$MARKER"
work_made=1

# --- Build and media -------------------------------------------------------------------------------------

echo "== Building the plugin"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
"${DOTNET_CMD[@]}" publish "$ROOT/Jellyfin.Plugin.LibraryCoverMaker/Jellyfin.Plugin.LibraryCoverMaker.csproj" \
  -c Release -o "$WORK/build" -v quiet -nologo
# Only the plugin's own DLL: Jellyfin supplies everything else.
cp "$WORK/build/Jellyfin.Plugin.LibraryCoverMaker.dll" "$WORK/config/plugins/LibraryCoverMaker/"

echo "== Making sample media"
bash "$E2E/make-media.sh" "$WORK/media"

# --- Jellyfin --------------------------------------------------------------------------------------------

echo "== Starting Jellyfin ($IMAGE)"
# It runs as the caller, not root, so nothing it writes under $WORK is out of reach of the cleanup.
container_made=1
docker run -d --name "$NAME" --label "$LABEL=1" --user "$(id -u):$(id -g)" -p "127.0.0.1:$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" "$IMAGE" >/dev/null

echo "== Waiting for Jellyfin"
ready=0
deadline=$((SECONDS + 180))
while [ "$SECONDS" -lt "$deadline" ]; do
  # Jellyfin answers from a start-up page at first, and its /System/Info/Public gives 200 too. Only the real
  # server's answer has StartupWizardCompleted in it.
  if body="$(curl -sf --max-time 5 "http://127.0.0.1:$PORT/System/Info/Public")" \
    && grep -q '"StartupWizardCompleted"' <<<"$body"; then
    ready=1
    break
  fi
  [ "$(docker container inspect -f '{{.State.Running}}' "$NAME")" = true ] || break
  sleep 1
done
if [ "$ready" != 1 ]; then
  docker logs --tail 40 "$NAME" >&2 || true
  die "Jellyfin did not answer on port $PORT"
fi

# --- Checks ----------------------------------------------------------------------------------------------

# The [ERR] and [FTL] entries of the server log that name the plugin. An entry starts with "[21:07:57.227]
# [INF]" (the console log has the time only; the log file has the date and zone too) and the lines after it
# (a stack trace, say) belong to it, so an entry counts when any of its lines names the plugin. Errors from
# other parts of Jellyfin are none of this plugin's business.
plugin_errors() {
  docker logs "$NAME" 2>&1 | awk '
    function flush() { if (bad && named) printf "%s", entry; entry = ""; bad = 0; named = 0 }
    /^\[[0-9][0-9:. +-]*\] \[[A-Z]+\]/ {
      flush()
      level = substr($0, index($0, "] [") + 3, 3)
      bad = (level == "ERR" || level == "FTL")
    }
    { entry = entry $0 "\n" }
    tolower($0) ~ /librarycovermaker|library cover maker/ { named = 1 }
    END { flush() }'
}

# A global npm install puts the command at <prefix>/bin/playwright and the package at
# <prefix>/lib/node_modules/playwright, which the command links to.
if [ -z "${NODE_PATH:-}" ] && command -v playwright >/dev/null; then
  cli="$(readlink -f "$(command -v playwright)")"
  case "$cli" in
    */node_modules/playwright/cli.js) export NODE_PATH="${cli%/playwright/cli.js}" ;;
  esac
fi

echo "== Running the checks"
status=0
# In the background, so that a signal sent to this shell acts at once: a trap otherwise waits for the foreground
# command to end. The cleanup stops node if it is still running.
JF="http://127.0.0.1:$PORT" CONTAINER="$NAME" WORK="$WORK" node "$E2E/e2e.mjs" "$@" &
node_pid=$!
wait "$node_pid" || status=$?
node_pid=

# The checks read the log too, but only up to the point they run; this reads all of it.
errors="$(plugin_errors)" || die "cannot read the log of container $NAME"
if [ -n "$errors" ]; then
  echo "FAIL  the server log has plugin [ERR] or [FTL] entries:"
  printf '%s\n' "$errors" | head -40
  status=1
else
  echo "PASS  the server log has no plugin [ERR] or [FTL] entries"
fi
if [ "$status" != 0 ]; then
  # Jellyfin logs plenty of harmless warnings of its own at start-up, so only the plugin's are listed.
  echo "== Errors from the server log, and warnings that name the plugin (last 20 lines)"
  { docker logs "$NAME" 2>&1 \
      | grep -iE -e '^\[[0-9][0-9:. +-]*\] \[(ERR|FTL)\]' \
        -e '^\[[0-9][0-9:. +-]*\] \[WRN\].*(LibraryCoverMaker|Library Cover Maker)' \
      | tail -20; } || true
fi
exit "$status"
