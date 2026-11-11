#!/bin/sh
# The rig's inner half. It runs inside the rig's own Docker engine container (gitberth-e2e, docker:dind), against that
# engine, which nothing but the rig uses. run.sh copies the tree to /e2e/src first, then calls:
#   inner.sh up              build what changed and (re)start only what changed: stand-ins, Gitberth, fake cloudflared
#   inner.sh reset           a fresh Gitberth (data wiped, every app and its containers, images and volumes removed) and
#                            empty stand-ins
#   inner.sh test [args]     run the scenarios (pytest args), then print the report
#   inner.sh logs [what] [n] gitberth, standin, cloudflared, or any container's name
#   inner.sh password        the rig's admin password, for a person who wants to look at the UI
set -eu

SRC=/e2e/src
E2E=$SRC/tests/e2e
OUT=/e2e/out
LOGS=/e2e/logs
NET=gbe2e
SUBNET=10.89.0.0/24
STANDIN=gbe2e-standin
STANDIN_IP=10.89.0.2
GITBERTH_IP=10.89.0.3
PUBLIC_HOST=${E2E_PUBLIC_HOST:-192.168.70.10}
ENV_FILE=/e2e/gitberth.env

say() { echo "[rig] $*"; }
die() { echo "[rig] $*" >&2; exit 1; }

compose() { docker compose -p gitberth -f "$SRC/docker-compose.yml" -f "$E2E/rig/compose.e2e.yml" --env-file "$ENV_FILE" "$@"; }

image_id() { docker image inspect -f '{{.Id}}' "$1" 2>/dev/null || true; }
container_image() { docker container inspect -f '{{.Image}}' "$1" 2>/dev/null || true; }
running() { [ "$(docker container inspect -f '{{.State.Running}}' "$1" 2>/dev/null || true)" = "true" ]; }

# build <tag> <context> [dockerfile]: quiet unless it fails. BuildKit's cache makes an unchanged build give the same
# image, so nothing restarts: without provenance and SBOM attestations, whose timestamps would make every build new.
build() {
  tag=$1
  ctx=$2
  log="$LOGS/build-$(echo "$tag" | tr '/:' '__').log"
  mkdir -p "$LOGS"
  if [ -n "${3:-}" ]; then set -- -f "$3"; else set --; fi
  if ! docker build --progress=plain --provenance=false --sbom=false "$@" -t "$tag" "$ctx" >"$log" 2>&1; then
    tail -n 80 "$log" >&2
    die "building $tag failed (full log in the rig at $log)"
  fi
}

# wait_http <url> <seconds>
wait_http() {
  i=0
  while [ "$i" -lt "$2" ]; do
    if wget -q -O /dev/null -T 3 "$1" 2>/dev/null; then return 0; fi
    sleep 1
    i=$((i + 1))
  done
  return 1
}

network() {
  docker network inspect "$NET" >/dev/null 2>&1 || docker network create --subnet "$SUBNET" "$NET" >/dev/null
  for v in gbe2e-state gbe2e-trust; do docker volume inspect "$v" >/dev/null 2>&1 || docker volume create "$v" >/dev/null; done
}

up_standin() {
  build gbe2e-standin:latest "$E2E" "$E2E/standins/Dockerfile"
  if [ "$(container_image "$STANDIN")" != "$(image_id gbe2e-standin:latest)" ] || ! running "$STANDIN"; then
    docker rm -f "$STANDIN" >/dev/null 2>&1 || true
    docker run -d --name "$STANDIN" --hostname "$STANDIN" --network "$NET" --ip "$STANDIN_IP" --restart unless-stopped \
      -v gbe2e-state:/state -v gbe2e-trust:/trust -e E2E_GITBERTH_URL=http://gitberth:8080 \
      --label gbe2e.part=standin gbe2e-standin:latest >/dev/null
    say "stand-ins started"
  fi
  wait_http "http://$STANDIN_IP:8099/__control/health" 60 || { docker logs --tail 60 "$STANDIN"; die "the stand-ins didn't answer"; }
}

write_env() {
  pw=$(docker exec "$STANDIN" cat /state/admin-password)
  (umask 077 && cat >"$ENV_FILE" <<EOF
GITBERTH_IMAGE=gitberth:e2e
GITBERTH_ADMIN_PASSWORD=$pw
GITBERTH_PUBLIC_HOST=$PUBLIC_HOST
GITBERTH_PORT=8080
TZ=UTC
E2E_STANDIN_IP=$STANDIN_IP
E2E_GITBERTH_IP=$GITBERTH_IP
EOF
  )
}

wait_gitberth() {
  wait_http "http://$GITBERTH_IP:8080/login" 180 || { docker logs --tail 80 gitberth 2>&1; die "Gitberth didn't answer on /login"; }
}

start_gitberth() {
  write_env
  compose up -d --remove-orphans gitberth >"$LOGS/compose.log" 2>&1 || { cat "$LOGS/compose.log"; die "docker compose up failed"; }
  wait_gitberth
}

up_gitberth() {
  before=$(image_id gitberth:e2e)
  say "building Gitberth from the tree (only what changed; the very first build takes a few minutes)"
  build gitberth:e2e "$SRC"
  [ "$before" = "$(image_id gitberth:e2e)" ] || say "Gitberth's image changed"
  # The CA bundle Gitberth trusts: its own image's system CAs, plus the rig's test CA.
  docker run --rm --entrypoint sh -v gbe2e-trust:/trust gitberth:e2e -c \
    'cat /etc/ssl/certs/ca-certificates.crt /trust/ca.pem >/trust/gitberth-bundle.pem.new && mv /trust/gitberth-bundle.pem.new /trust/gitberth-bundle.pem'
  start_gitberth
}

# The stand-ins' container back on the rig's network alone: a scenario that ran the fake cloudflared on another network
# joined the stand-ins to that one (engine.py, recreate_fake_cloudflared).
standin_home() {
  for n in $(docker container inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$STANDIN" 2>/dev/null || true); do
    [ "$n" = "$NET" ] || docker network disconnect -f "$n" "$STANDIN" >/dev/null 2>&1 || true
  done
}

up_cloudflared() {
  # Built from standins/: it reads ingress with the stand-ins' ingress.py.
  build gbe2e-cloudflared:fake "$E2E/standins" "$E2E/standins/cloudflared/Dockerfile"
  token=$(wget -q -O - "http://$STANDIN_IP:8099/__control/cloudflare/cloudflared-token")
  # Recreated when its image or the tunnel changed, or when a scenario left it on another network than the host's. On
  # the host's network, like the live install's, its edge port (7844) is where the stand-in sends the tunnel's traffic.
  if [ "$(container_image cloudflare-tunnel)" != "$(image_id gbe2e-cloudflared:fake)" ] || ! running cloudflare-tunnel \
     || [ "$(docker container inspect -f '{{.HostConfig.NetworkMode}}' cloudflare-tunnel 2>/dev/null || true)" != "host" ] \
     || ! docker container inspect -f '{{json .Args}}' cloudflare-tunnel 2>/dev/null | grep -qF "$token"; then
    docker rm -f cloudflare-tunnel >/dev/null 2>&1 || true
    standin_home
    docker run -d --name cloudflare-tunnel --network host --restart unless-stopped -e STANDIN="http://$STANDIN_IP:8099" \
      -e E2E_NETWORK=host --label gbe2e.part=cloudflared gbe2e-cloudflared:fake tunnel run --token "$token" >/dev/null
    say "fake cloudflared started"
  fi
  # It's running the tunnel once the stand-in has heard from it. One that runs a newer version than the stand-in now has
  # (its state was lost) ignores the older ones, as cloudflared does, until it restarts and takes the current one.
  i=0
  until wget -q -O - "http://$STANDIN_IP:8099/__control/cloudflare/connector" 2>/dev/null | grep -q '"runs_current": *true'; do
    i=$((i + 1))
    if [ "$i" -eq 15 ]; then docker restart cloudflare-tunnel >/dev/null 2>&1 || true; fi
    if [ "$i" -ge 40 ]; then docker logs --tail 40 cloudflare-tunnel 2>&1; die "the fake cloudflared doesn't run the tunnel"; fi
    sleep 1
  done
}

up() {
  network
  up_standin
  up_gitberth
  up_cloudflared
  build gbe2e-runner:latest "$E2E/scenarios"
  say "up: Gitberth http://$GITBERTH_IP:8080 (inside), stand-ins $STANDIN ($STANDIN_IP)"
}

# Every app Gitberth made in the rig's engine: its containers (and helpers), images and volumes. The lists split into one
# argument each on purpose.
# shellcheck disable=SC2086
remove_apps() {
  ids=$(docker ps -aq --filter label=gitberth.managed=true)
  [ -z "$ids" ] || docker rm -f $ids >/dev/null 2>&1 || true
  ids=$(docker ps -aq --filter label=gitberth.helper)
  [ -z "$ids" ] || docker rm -f $ids >/dev/null 2>&1 || true
  images=$(docker image ls --format '{{.Repository}}:{{.Tag}}' | grep '^gitberth/' || true)
  [ -z "$images" ] || docker rmi -f $images >/dev/null 2>&1 || true
  volumes=$(docker volume ls -q | grep '^gb-' || true)
  [ -z "$volumes" ] || docker volume rm -f $volumes >/dev/null 2>&1 || true
  for n in gitberth gitberth-lan; do docker network rm "$n" >/dev/null 2>&1 || true; done
}

reset() {
  say "reset: a fresh Gitberth with no apps, and empty stand-ins"
  compose down -v --remove-orphans >"$LOGS/compose-down.log" 2>&1 || true
  # Off Gitberth's network before it's removed (a network with containers on it can't be): the stand-ins, and a fake
  # cloudflared a scenario left off the host's network (up_cloudflared below makes it again, on the host's).
  standin_home
  [ "$(docker container inspect -f '{{.HostConfig.NetworkMode}}' cloudflare-tunnel 2>/dev/null || echo host)" = "host" ] \
    || docker rm -f cloudflare-tunnel >/dev/null 2>&1 || true
  remove_apps
  wget -q -O /dev/null --post-data='{}' --header='Content-Type: application/json' "http://$STANDIN_IP:8099/__control/reset" \
    || die "the stand-ins didn't reset"
  start_gitberth
  # The fake cloudflared starts afresh too (its log, and the configuration it runs), on the host's network.
  docker restart cloudflare-tunnel >/dev/null 2>&1 || true
  up_cloudflared
}

run_tests() {
  mkdir -p "$OUT"
  rm -rf "${OUT:?}"/* 2>/dev/null || true
  pw=$(docker exec "$STANDIN" cat /state/admin-password)
  docker rm -f gbe2e-runner >/dev/null 2>&1 || true
  rc=0
  # The runner sees GitHub's and Cloudflare's names as Gitberth does (the stand-in, trusted through the rig's CA), so a
  # scenario can call them as Gitberth would, and nothing in it can reach the real ones.
  hosts=""
  for h in github.com www.github.com api.github.com codeload.github.com uploads.github.com raw.githubusercontent.com \
           objects.githubusercontent.com api.cloudflare.com; do hosts="$hosts --add-host $h:$STANDIN_IP"; done
  # shellcheck disable=SC2086
  docker run --rm --name gbe2e-runner --network "$NET" --shm-size 1g $hosts \
    -v gbe2e-trust:/trust:ro -e SSL_CERT_FILE=/trust/gitberth-bundle.pem \
    -v /var/run/docker.sock:/var/run/docker.sock -v "$E2E":/e2e:ro -v gitberth_gitberth-data:/gbdata -v "$OUT":/out \
    -e E2E_GITBERTH_URL=http://gitberth:8080 -e E2E_STANDIN_URL="http://$STANDIN:8099" -e E2E_ADMIN_PASSWORD="$pw" \
    -e E2E_PUBLIC_HOST="$PUBLIC_HOST" -e E2E_KEEP="${E2E_KEEP:-}" -e E2E_GITBERTH_CONTAINER=gitberth -e E2E_OUT=/out \
    -w /e2e/scenarios gbe2e-runner:latest python3 -m pytest -p no:cacheprovider "$@" || rc=$?
  echo "===== E2E REPORT ====="
  cat "$OUT/report.txt" 2>/dev/null || echo "(no report: pytest exited with $rc before writing one)"
  echo "===== END OF REPORT ====="
  return "$rc"
}

case "${1:-}" in
  up) up ;;
  reset) reset ;;
  test) shift; run_tests "$@" ;;
  logs)
    what=${2:-gitberth}
    case "$what" in standin) what=$STANDIN ;; cloudflared) what=cloudflare-tunnel ;; esac
    docker logs --timestamps --tail "${3:-200}" "$what" 2>&1 ;;
  password) docker exec "$STANDIN" cat /state/admin-password; echo ;;
  status) docker ps -a --format '{{.Names}}\t{{.Image}}\t{{.Status}}' ;;
  *) die "usage: inner.sh up | reset | test [pytest args] | logs [what] [lines] | password | status" ;;
esac
