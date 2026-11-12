#!/bin/sh
# Gitberth's end-to-end rig, on a Docker host that allows privileged containers (CT 101 here; CI later).
#   run.sh up              start the rig, or bring it up to date: its own Docker engine, the stand-ins for GitHub and
#                          Cloudflare, the fake cloudflared, and Gitberth built from this tree (only what changed is
#                          rebuilt or restarted; the engine's volume keeps base images and build caches between runs)
#   run.sh test [args]     up, then a fresh Gitberth (no apps, empty stand-ins), then the scenarios; args go to pytest,
#                          e.g. run.sh test -k branch_previews
#   run.sh down            remove everything the rig made on this host: its engine container, volume and network
#   run.sh logs [what] [n] the last n lines of gitberth (default), standin, cloudflared, or any container in the rig
#   run.sh password        the admin password of the rig's Gitberth (its UI is on this host's port 18092)
# Everything it makes on this host is named gitberth-e2e or gbe2e-*. It never touches anything else.
# E2E_INSTANCE=b (or c) runs another rig beside the first, so two runs never share one Gitberth: gitberth-e2e-b on
# gbe2e-lan-b (192.168.71.0/24, UI on port 18093), or gitberth-e2e-c (192.168.72.0/24, port 18094).
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
INSTANCE=${E2E_INSTANCE:-}
case "$INSTANCE" in
  '') NET_N=70; PORT_N=18092 ;;
  b) NET_N=71; PORT_N=18093 ;;
  c) NET_N=72; PORT_N=18094 ;;
  *) echo "[e2e] E2E_INSTANCE is b or c, or unset for the first rig" >&2; exit 2 ;;
esac
SUFFIX=${INSTANCE:+-$INSTANCE}
RIG=gitberth-e2e$SUFFIX
LAN=gbe2e-lan$SUFFIX
VOLUME=gitberth-e2e$SUFFIX
LAN_SUBNET=${E2E_LAN_SUBNET:-192.168.$NET_N.0/24}
LAN_GATEWAY=${E2E_LAN_GATEWAY:-192.168.$NET_N.1}
RIG_IP=${E2E_RIG_IP:-192.168.$NET_N.10}
UI_PORT=${E2E_UI_PORT:-$PORT_N}
DIND_IMAGE=docker:29-dind

say() { echo "[e2e] $*"; }

engine() {
  docker network inspect "$LAN" >/dev/null 2>&1 \
    || docker network create --subnet "$LAN_SUBNET" --gateway "$LAN_GATEWAY" "$LAN" >/dev/null
  if ! docker container inspect "$RIG" >/dev/null 2>&1; then
    say "starting the rig's own Docker engine ($RIG, $RIG_IP on $LAN, UI on port $UI_PORT)"
    docker run -d --privileged --name "$RIG" --hostname gbe2e-host --network "$LAN" --ip "$RIG_IP" -p "$UI_PORT:8080" \
      -v "$VOLUME:/var/lib/docker" --label gbe2e.rig=1 "$DIND_IMAGE" >/dev/null
  fi
  [ "$(docker container inspect -f '{{.State.Running}}' "$RIG")" = "true" ] || docker start "$RIG" >/dev/null
  i=0
  until docker exec "$RIG" docker info >/dev/null 2>&1; do
    i=$((i + 1))
    if [ "$i" -ge 60 ]; then docker logs --tail 40 "$RIG"; say "the rig's engine didn't start"; exit 1; fi
    sleep 2
  done
}

# Only what the rig builds and runs goes in: Gitberth's sources and this folder.
sync_tree() {
  (cd "$ROOT" && tar --exclude=bin --exclude=obj --exclude=__pycache__ --exclude=.pytest_cache \
    -cf - Dockerfile .dockerignore LICENSE.md docker-compose.yml src/Gitberth tests/e2e) \
    | docker exec -i "$RIG" sh -c 'rm -rf /e2e/src.new && mkdir -p /e2e/src.new && tar -xf - -C /e2e/src.new && rm -rf /e2e/src && mv /e2e/src.new /e2e/src'
}

inner() { docker exec -e E2E_PUBLIC_HOST="$RIG_IP" -e E2E_KEEP="${E2E_KEEP:-}" "$RIG" sh /e2e/src/tests/e2e/rig/inner.sh "$@"; }

case "${1:-}" in
  up)
    engine
    sync_tree
    inner up
    say "the rig is up: Gitberth's UI is on this host's port $UI_PORT (run.sh password)"
    ;;
  test)
    shift
    engine
    sync_tree
    inner up
    inner reset
    rc=0
    inner test "$@" || rc=$?
    exit "$rc"
    ;;
  down)
    docker rm -f "$RIG" >/dev/null 2>&1 || true
    docker volume rm "$VOLUME" >/dev/null 2>&1 || true
    docker network rm "$LAN" >/dev/null 2>&1 || true
    say "removed $RIG, its volume $VOLUME and the network $LAN"
    ;;
  logs)
    shift
    inner logs "$@"
    ;;
  password)
    inner password
    ;;
  status)
    inner status
    ;;
  *)
    sed -n '2,12p' "$0"
    exit 2
    ;;
esac
