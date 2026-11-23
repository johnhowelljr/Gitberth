#!/bin/sh
# The docs' screenshots, on demand: never part of the impact plan, the full suite or CI. On a Docker host that allows
# privileged containers, with Python 3.11 or later and git:
#   sh tests/e2e/shots/run.sh                  the pictures a change made stale (stale.py): those that show a file that
#                                              changed after the commit they were taken from (taken.toml), or whose shot
#                                              changed. None stale: nothing runs.
#   sh tests/e2e/shots/run.sh --also a,b       those pictures too, stale or not
#   sh tests/e2e/shots/run.sh --all            every picture docs/ shows
#   sh tests/e2e/shots/run.sh -k "step or dashboard or settings"
#                                              pytest's -k: the set-up steps always need "step"; the rest are shot names
# A picture taken is kept only when it looks different from the one docs/images has, and no test failed before it; the
# others it takes on the way (a step's own) aren't kept. The pictures kept are copied into docs/images, and taken.toml
# says which were taken from the head commit: commit both. E2E_SHOTS_GIT names the git repo with the tree's history when
# the tree isn't a checkout itself, and E2E_SHOTS_HEAD the commit the tree is (HEAD). E2E_SHOTS_SINCE (the last
# release): notes on what changed since then that may change pictures without making any stale, a set-up step say.
# It runs `run.sh test` on rig c (E2E_INSTANCE=c, unless E2E_INSTANCE is set: b, or empty for the first rig): a fresh
# Gitberth, made to look like the docs' example home server (host name homelab, Gitberth at http://192.168.50.10:8080 on
# the LAN 192.168.50.0/24), set up through the stand-ins with the docs' demo apps and settings, then pictured in a 1440 x
# 900 window at twice the pixels, each picture framed as frame.py does and saved as WebP. The apps stay (E2E_KEEP), so
# its UI can be looked at afterwards (port 18094 for rig c; run.sh password). The pictures land in
# tests/e2e/out/shots/images and are copied into docs/images; the run's report is tests/e2e/out/shots/report.txt.
# 192.168.50.0/24 is the rig's own network while it's set up this way: one rig at a time.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)

export E2E_INSTANCE="${E2E_INSTANCE-c}"
export E2E_HOSTNAME="${E2E_HOSTNAME:-homelab}"
export E2E_LAN_SUBNET="${E2E_LAN_SUBNET:-192.168.50.0/24}"
export E2E_LAN_GATEWAY="${E2E_LAN_GATEWAY:-192.168.50.1}"
export E2E_RIG_IP="${E2E_RIG_IP:-192.168.50.10}"
# As someone who built it from the repo runs it (docker-compose.build.yml), not under the rig's own name.
export E2E_GITBERTH_IMAGE="${E2E_GITBERTH_IMAGE:-gitberth:local}"
# With an install's own waits, so a deploy's log says what the docs say (it stays up 15 s, not the rig's 3).
export E2E_TIMINGS="${E2E_TIMINGS:-production}"
# The rig's own stand-ins, fake cloudflared, runner and network out of Gitberth's pages and audit log (rig/inner.sh).
export E2E_HIDE_RIG=1
export E2E_KEEP=1
export E2E_OUT_NAME=shots

say() { echo "[e2e] shots: $*"; }

mode=stale
also=
case "${1:-}" in
  --all) mode=all; shift ;;
  --also) also=${2:?--also takes picture names: a,b}; shift 2 ;;
esac
# pytest's own arguments (-k ...): the shots they name, stale or not.
[ $# -eq 0 ] || mode=args

# Which pictures, and the commit the tree is (stale.py; it says why each is stale).
plan() {
  python3 "$HERE/stale.py" plan --git "${E2E_SHOTS_GIT:-$ROOT}" --head "${E2E_SHOTS_HEAD:-HEAD}" --tree "$ROOT" --format shell "$@"
}
SHOTS_HEAD=
SHOTS_ONLY=
SHOTS_K=
if [ "$mode" = stale ]; then
  picked=$(plan ${also:+--also "$also"} ${E2E_SHOTS_SINCE:+--since "$E2E_SHOTS_SINCE"})
else
  picked=$(plan --all) || { say "no git history here: taken.toml stays as it is"; picked=; }
fi
eval "$picked"
if [ "$mode" = stale ]; then
  if [ -z "$SHOTS_ONLY" ]; then
    say "no picture is stale (nothing they show changed since they were taken): nothing to take"
    exit 0
  fi
  export E2E_SHOTS_ONLY="$SHOTS_ONLY"
  set -- -k "$SHOTS_K"
fi

started=$(date +%s)
rc=0
sh "$ROOT/tests/e2e/run.sh" test -v /e2e/shots/test_shots.py "$@" || rc=$?
out="$ROOT/tests/e2e/out/shots"
n=0
if [ -d "$out/images" ]; then
  for f in "$out/images"/*.webp; do
    [ -f "$f" ] || continue
    cp "$f" "$ROOT/docs/images/"
    n=$((n + 1))
  done
fi
# What was taken (kept, or found to look as docs/images has it) is as of the head now.
same=0
if [ -s "$out/taken.txt" ]; then
  same=$(grep -c ' same$' "$out/taken.txt" || true)
  if [ -n "$SHOTS_HEAD" ]; then
    python3 "$HERE/stale.py" record --head "$SHOTS_HEAD" --names "$(cut -d' ' -f1 "$out/taken.txt" | paste -sd, -)" --root "$ROOT"
  fi
fi
say "$n picture(s) copied into docs/images, $same looked the same, in $(( ($(date +%s) - started) / 60 )) min (exit code $rc)"
exit "$rc"
