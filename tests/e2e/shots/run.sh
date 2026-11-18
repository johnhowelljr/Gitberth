#!/bin/sh
# The docs' screenshots, on demand: never part of the impact plan, the full suite or CI. On a Docker host that allows
# privileged containers:
#   sh tests/e2e/shots/run.sh                  every picture docs/ shows, into docs/images (WebP)
#   sh tests/e2e/shots/run.sh -k "step or dashboard or settings"
#                                              pytest's -k: the set-up steps always need "step"; the rest are shot names
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

started=$(date +%s)
rc=0
sh "$ROOT/tests/e2e/run.sh" test -v /e2e/shots/test_shots.py "$@" || rc=$?
images="$ROOT/tests/e2e/out/shots/images"
n=0
if [ -d "$images" ]; then
  for f in "$images"/*.webp; do
    [ -f "$f" ] || continue
    cp "$f" "$ROOT/docs/images/"
    n=$((n + 1))
  done
fi
echo "[e2e] shots: $n picture(s) copied into docs/images in $(( ($(date +%s) - started) / 60 )) min (exit code $rc)"
exit "$rc"
