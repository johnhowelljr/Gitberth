#!/bin/sh
# Gitberth's end-to-end rig, on a Docker host that allows privileged containers (CT 101 here, and GitHub's runners).
#   run.sh [impact] [--base REF | --base-tree DIR] [--lanes N] [--budget MIN] [--no-unit]
#                          the default way to test a change, in 10 minutes in all: the unit tests, then the scenario
#                          tests the change touches (tests/e2e/impact.toml) and a short smoke, most specific first, as
#                          many as fit (--budget 0: all of them); the plan lists what's left for the full suite. REF
#                          defaults to origin/main; DIR is a copy of the base, for a copy of the tree without git
#   run.sh plan [impact's options, or a tier and its options] [--out FILE]
#                          what would run, and why; touches nothing (--out FILE keeps the plan, for run-plan)
#   run.sh smoke | wide | full | release [--lanes N] [--no-unit]
#                          the smoke (6 tests); the hub files' set; every group, slow tests too; full, then every
#                          template (production timings). full and release are the whole suite: on demand, before a
#                          release (GitHub Actions runs them in about 25 min: Actions > E2E, or an e2e-* tag)
#   run.sh groups NN|@alias ... [--smoke] [--templates a,b] [--lanes N] [--no-unit]
#                          the groups named: 12+slow keeps 12's slow tests, 31 is the template smoke (--templates: only those)
#   run.sh run-plan FILE [--no-unit]
#                          a plan made elsewhere (impact.py plan --format tsv); every command above ends in it
#   run.sh unit            the unit tests (dotnet test tests/Gitberth.Tests) in mcr.microsoft.com/dotnet/sdk:10.0
#   run.sh test [args]     one rig: up, a fresh Gitberth (no apps, empty stand-ins), then the scenarios; args go to pytest,
#                          e.g. run.sh test -k branch_previews (no args: every group but the template smoke). Its
#                          junit.xml, report.txt and screenshots are copied to tests/e2e/out/<a|b|c>
#   run.sh up              start the rig, or bring it up to date: its own Docker engine, the stand-ins for GitHub and
#                          Cloudflare, the fake cloudflared, and Gitberth built from this tree (only what changed is
#                          rebuilt or restarted; the engine's volume keeps base images and build caches between runs)
#   run.sh down            remove everything the rig made on this host: its engine container, volume and network
#   run.sh logs [what] [n] the last n lines of gitberth (default), standin, cloudflared, or any container in the rig
#   run.sh password        the admin password of the rig's Gitberth (its UI is on this host's port 18092)
#   run.sh status | busy [--lanes N] | help
# Lanes: --lanes 2 runs the first rig and b side by side, 3 adds c; the template smoke stays on the first rig, where its
# images are. With one lane, E2E_INSTANCE picks the rig. A rig that's running scenarios is never shared: a second run is
# refused (it holds a flock on /tmp/gbe2e-lock-<rig>, and the runs it starts know it from E2E_LOCKED).
# Variables:
#   E2E_INSTANCE=b|c   another rig beside the first, so two runs never share one Gitberth: gitberth-e2e-b on gbe2e-lan-b
#                      (192.168.71.0/24, UI on port 18093), or gitberth-e2e-c (192.168.72.0/24, port 18094)
#   E2E_KEEP=1         leave the apps the scenarios made in place
#   E2E_TIMINGS        test (the default: the rig's shorter waits, listed in rig/compose.e2e.yml) or production (as an
#                      install runs); E2E_TEST_TIMINGS, when set, is passed on as the waits themselves
#   E2E_TEMPLATES=a,b  the template smoke's templates (empty: every template)
#   E2E_DIND_IMAGE, E2E_DIND_ARGS   the rig's engine (docker:29-dind) and dockerd's flags, e.g. --registry-mirror=...
#   E2E_HOSTNAME       the rig's host name (gbe2e-host), E2E_LAN_SUBNET, E2E_LAN_GATEWAY and E2E_RIG_IP its LAN,
#   E2E_GITBERTH_IMAGE the name Gitberth's image runs under (gitberth:e2e), and E2E_HIDE_RIG=1 the rig's own containers
#                      out of Gitberth's pages (its helper label): the docs' screenshots (shots/run.sh) set them
#   E2E_NUGET_VOLUME   the unit tests' NuGet cache (gbe2e-nuget)
# Everything it makes on this host is named gitberth-e2e or gbe2e-*. It never touches anything else.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
SELF=$HERE/run.sh
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
RIG_HOSTNAME=${E2E_HOSTNAME:-gbe2e-host}
DIND_IMAGE=${E2E_DIND_IMAGE:-docker:29-dind}
DIND_ARGS=${E2E_DIND_ARGS:-}
TAB=$(printf '\t')
W=""

say() { echo "[e2e] $*"; }
die() { echo "[e2e] $*" >&2; exit 1; }
refuse() { echo "[e2e] $*" >&2; exit 2; }
usage() { sed -n '2,/^set -eu$/p' "$SELF" | sed -e '$d' -e 's/^# \{0,1\}//'; }

# A work folder for this run, removed when it ends. Stopped (Ctrl-C, or the connection gone), it stops its lanes and
# unit tests too: they run in the background, where a shell without job control ignores Ctrl-C.
on_signal() { trap '' INT TERM HUP; kill 0 2>/dev/null || true; exit "$1"; }
workdir() {
  [ -z "$W" ] || return 0
  W=$(mktemp -d "${TMPDIR:-/tmp}/gbe2e-run.XXXXXX")
  trap 'rm -rf "$W"' EXIT
  trap 'on_signal 130' INT
  trap 'on_signal 143' TERM
  trap 'on_signal 129' HUP
}

# ---- rigs: a, b and c, and holding them

label() { echo "${1:-a}"; }                    # label <instance>: a (the first rig), b or c
instance_of() { [ "$1" = a ] || echo "$1"; }   # instance_of <label>: E2E_INSTANCE for it
rig_of() { echo "gitberth-e2e$(instance_of "$1" | sed 's/^./-&/')"; }
title_of() { if [ "$1" = a ]; then echo "the first rig (gitberth-e2e)"; else echo "rig $1 (gitberth-e2e-$1)"; fi; }

# rig_busy <rig>: true while something runs in it that an older run.sh started, which holds no lock: a scenario
# runner, or inner.sh at work.
rig_busy() {
  [ "$(docker container inspect -f '{{.State.Running}}' "$1" 2>/dev/null || true)" = true ] || return 1
  [ -z "$(docker exec "$1" docker ps -q --filter name=gbe2e-runner 2>/dev/null || true)" ] || return 0
  docker exec "$1" ps -o args 2>/dev/null | grep -q '[r]ig/inner\.sh'
}

busy() { echo "[e2e] $(title_of "$1") is running scenarios; try again when it's done" >&2; exit 1; }

# hold <label>...: this run's rigs, until it ends; refused when another run has one. The runs this one starts inherit
# the hold (E2E_LOCKED names the rigs held).
hold() {
  for l in "$@"; do
    r=$(rig_of "$l")
    case " ${E2E_LOCKED:-} " in *" $r "*) continue ;; esac
    if command -v flock >/dev/null 2>&1; then
      f="${E2E_LOCK_DIR:-/tmp}/gbe2e-lock-$r"
      case "$l" in
        a) exec 7>>"$f"; flock -n 7 || busy "$l" ;;
        b) exec 8>>"$f"; flock -n 8 || busy "$l" ;;
        c) exec 9>>"$f"; flock -n 9 || busy "$l" ;;
      esac
    fi
    ! rig_busy "$r" || busy "$l"
    E2E_LOCKED="${E2E_LOCKED:-} $r"
  done
  export E2E_LOCKED
}

# lanes_of <lanes>: the labels of the rigs <lanes> lanes use (one lane: E2E_INSTANCE's).
lanes_of() {
  case $1 in
    1) label "$INSTANCE" ;;
    2) echo "a b" ;;
    3) echo "a b c" ;;
  esac
}

# ---- one rig

engine() {
  # Its network is made again (and its engine with it: their volume stays) when it has another subnet than asked for.
  had=$(docker network inspect -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}' "$LAN" 2>/dev/null || true)
  if [ -n "$had" ] && [ "$had" != "$LAN_SUBNET" ]; then
    say "the rig's network $LAN had the subnet $had; making it again with $LAN_SUBNET (the engine's volume stays)"
    docker rm -f "$RIG" >/dev/null 2>&1 || true
    docker network rm "$LAN" >/dev/null
  fi
  docker network inspect "$LAN" >/dev/null 2>&1 \
    || docker network create --subnet "$LAN_SUBNET" --gateway "$LAN_GATEWAY" "$LAN" >/dev/null
  # Its engine is made again (its volume and images stay) when its image, dockerd's flags or its host name changed.
  if docker container inspect "$RIG" >/dev/null 2>&1; then
    had=$(docker container inspect -f '{{.Config.Image}}|{{index .Config.Labels "gbe2e.args"}}|{{.Config.Hostname}}' "$RIG")
    if [ "$had" != "$DIND_IMAGE|$DIND_ARGS|$RIG_HOSTNAME" ]; then
      say "the rig's engine had other settings ($had); starting it again with $DIND_IMAGE $DIND_ARGS as $RIG_HOSTNAME (its volume stays)"
      docker rm -f "$RIG" >/dev/null
    fi
  fi
  if ! docker container inspect "$RIG" >/dev/null 2>&1; then
    say "starting the rig's own Docker engine ($RIG, $RIG_IP on $LAN, UI on port $UI_PORT)"
    # The dind image hands arguments that start with - to dockerd: one word each, on purpose.
    # shellcheck disable=SC2086
    docker run -d --privileged --name "$RIG" --hostname "$RIG_HOSTNAME" --network "$LAN" --ip "$RIG_IP" -p "$UI_PORT:8080" \
      -v "$VOLUME:/var/lib/docker" --label gbe2e.rig=1 --label "gbe2e.args=$DIND_ARGS" "$DIND_IMAGE" $DIND_ARGS >/dev/null
  fi
  [ "$(docker container inspect -f '{{.State.Running}}' "$RIG")" = "true" ] || docker start "$RIG" >/dev/null
  i=0
  until docker exec "$RIG" docker info >/dev/null 2>&1; do
    i=$((i + 1))
    if [ "$i" -ge 60 ]; then docker logs --tail 40 "$RIG"; say "the rig's engine didn't start"; exit 1; fi
    sleep 2
  done
}

# Only what the rig builds and runs goes in: Gitberth's sources, the docs it embeds for its Help, and this folder (not an
# earlier run's report).
sync_tree() {
  (cd "$ROOT" && tar --exclude=bin --exclude=obj --exclude=__pycache__ --exclude=.pytest_cache --exclude=tests/e2e/out \
    -cf - Dockerfile .dockerignore LICENSE.md THIRD-PARTY-NOTICES.md docker-compose.yml docs src/Gitberth tests/e2e) \
    | docker exec -i "$RIG" sh -c 'rm -rf /e2e/src.new && mkdir -p /e2e/src.new && tar -xf - -C /e2e/src.new && rm -rf /e2e/src && mv /e2e/src.new /e2e/src'
}

# inner <command>: rig/inner.sh in the rig. The timings: E2E_TIMINGS=production passes an empty E2E_TEST_TIMINGS (the
# rig's compose file then gives Gitberth none); test passes E2E_TEST_TIMINGS only when it's set (else the rig's list).
inner() {
  set -- "$RIG" sh /e2e/src/tests/e2e/rig/inner.sh "$@"
  case "${E2E_TIMINGS:-test}" in
    production) set -- -e E2E_TEST_TIMINGS= "$@" ;;
    test) [ -z "${E2E_TEST_TIMINGS+set}" ] || set -- -e "E2E_TEST_TIMINGS=$E2E_TEST_TIMINGS" "$@" ;;
    *) refuse "E2E_TIMINGS is test or production, not $E2E_TIMINGS" ;;
  esac
  docker exec -e E2E_PUBLIC_HOST="$RIG_IP" -e E2E_KEEP="${E2E_KEEP:-}" -e E2E_TEMPLATES="${E2E_TEMPLATES:-}" \
    -e E2E_GITBERTH_IMAGE="${E2E_GITBERTH_IMAGE:-}" -e E2E_HIDE_RIG="${E2E_HIDE_RIG:-}" "$@"
}

# ---- the unit tests: on a copy of the tree, so a checkout never gets root's bin/ and obj/

unit_tests() {
  say "unit tests: dotnet test tests/Gitberth.Tests (mcr.microsoft.com/dotnet/sdk:10.0)"
  docker run --rm -v "$ROOT:/repo:ro" -v "${E2E_NUGET_VOLUME:-gbe2e-nuget}:/root/.nuget/packages" \
    mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'mkdir /src && tar -C /repo --exclude=./.git --exclude=./tests/e2e/out \
      --exclude=./src/Gitberth/bin --exclude=./src/Gitberth/obj --exclude=./tests/Gitberth.Tests/bin \
      --exclude=./tests/Gitberth.Tests/obj --exclude=./tests/Gitberth.UiTests/bin --exclude=./tests/Gitberth.UiTests/obj \
      -cf - . | tar -C /src -xf - && cd /src && dotnet test tests/Gitberth.Tests --nologo'
}

# ---- choosing: tests/e2e/impact.py

have_python() {
  command -v python3 >/dev/null 2>&1 && python3 -c 'import sys, tomllib; sys.exit(sys.version_info < (3, 11))' >/dev/null 2>&1
}

# selector <impact.py args>: on this host's Python when it has tomllib, else in a python image with the same paths.
selector() {
  if have_python; then python3 "$HERE/impact.py" "$@"; return; fi
  say "no Python 3.11 here: the selector runs in ${E2E_PYTHON_IMAGE:-python:3.13-alpine}"
  set -- "${E2E_PYTHON_IMAGE:-python:3.13-alpine}" python3 "$HERE/impact.py" "$@"
  [ -z "$BASE_TREE" ] || set -- -v "$BASE_TREE:$BASE_TREE:ro" "$@"
  # No -i: nothing goes in on stdin (a diff goes through $W), and a script fed to sh on stdin must keep the rest of it.
  docker run --rm --user "$(id -u):$(id -g)" -v "$ROOT:$ROOT:ro" -v "$W:$W" -w "$ROOT" "$@" </dev/null
}

# git_diff <ref>: what changed since the ref's merge base with HEAD, uncommitted and untracked files too.
git_diff() {
  mb=$(git -C "$ROOT" merge-base "$1" HEAD 2>/dev/null) || return 1
  git -C "$ROOT" -c core.quotepath=false diff -U3 -M --no-color --no-ext-diff --no-textconv --src-prefix=a/ \
    --dst-prefix=b/ "$mb"
  git -C "$ROOT" -c core.quotepath=false ls-files --others --exclude-standard | while IFS= read -r f; do
    (cd "$ROOT" && git --no-pager diff --no-color --no-index -- /dev/null "$f") || true
  done
}

TIER=""; SEL=""; TEMPLATES=""; BASE_REF=""; BASE_TREE=""; LANES=1; NO_UNIT=""; SMOKE=""; NO_REFINE=""; OUT=""; BUDGET=""

options() {
  while [ $# -gt 0 ]; do
    case $1 in
      --base|--base-tree|--lanes|--templates|--timings|--out|--budget)
        [ $# -ge 2 ] || refuse "$1 needs a value"
        case $1 in
          --base) BASE_REF=$2 ;;
          --base-tree) BASE_TREE=$2 ;;
          --lanes) LANES=$2 ;;
          --templates) TEMPLATES=$2 ;;
          --timings) E2E_TIMINGS=$2; export E2E_TIMINGS ;;
          --out) OUT=$2 ;;
          --budget) BUDGET=$2 ;;
        esac
        shift 2 ;;
      --no-unit) NO_UNIT=1; shift ;;
      --smoke) SMOKE=--smoke; shift ;;
      --no-smoke) SMOKE=--no-smoke; shift ;;
      --no-refine) NO_REFINE=1; shift ;;
      impact|smoke|wide|full|release|groups)
        [ -z "$TIER" ] || refuse "one tier at a time: $TIER or $1"
        TIER=$1; shift ;;
      -*) refuse "unknown option $1 (run.sh help)" ;;
      *) SEL="$SEL $1"; shift ;;
    esac
  done
  [ -n "$TIER" ] || TIER=impact
  case $LANES in 1|2|3) ;; *) refuse "--lanes is 1, 2 or 3" ;; esac
  [ -z "$SEL" ] || [ "$TIER" = groups ] || refuse "groups go with the groups tier: run.sh groups$SEL"
  case "${BASE_REF:-x}" in -*) refuse "--base takes a branch, tag or commit" ;; esac
  if [ "$LANES" -gt 1 ] && [ -n "$INSTANCE" ]; then refuse "E2E_INSTANCE picks the rig for one lane; --lanes $LANES uses the first rig and b"; fi
}

# make_plan: the plan in $W/plan.tsv and its report in $W/plan.txt.
make_plan() {
  set -- plan --tier "$TIER" --lanes "$LANES" --timings "${E2E_TIMINGS:-test}" --format tsv --out "$W/plan.tsv" \
    --report "$W/plan.txt"
  [ -z "$SEL" ] || set -- "$@" --groups "$SEL"
  [ -z "$TEMPLATES" ] || set -- "$@" --templates "$TEMPLATES"
  [ -z "$SMOKE" ] || set -- "$@" "$SMOKE"
  [ -z "$NO_REFINE" ] || set -- "$@" --no-refine
  [ -z "$BUDGET" ] || set -- "$@" --budget "$BUDGET"
  if [ "$TIER" = impact ] || { [ "$TIER" = wide ] && [ -n "$BASE_REF$BASE_TREE" ]; }; then
    if [ -n "$BASE_TREE" ]; then
      [ -d "$BASE_TREE" ] || refuse "no such folder: $BASE_TREE"
      BASE_TREE=$(cd "$BASE_TREE" && pwd)
      set -- "$@" --trees "$BASE_TREE" "$ROOT"
    else
      ref=${BASE_REF:-origin/main}
      git -C "$ROOT" rev-parse --git-dir >/dev/null 2>&1 \
        || refuse "this copy of the tree has no git: give --base-tree DIR (a copy of the base), or a tier (smoke, groups ...)"
      if have_python; then
        set -- "$@" --git "$ref"
      elif git_diff "$ref" >"$W/change.diff"; then
        set -- "$@" --diff "$W/change.diff"
      else
        set -- "$@" --tier full --note "git can't find a common ancestor of $ref and HEAD here, so every group runs (full)"
      fi
    fi
  fi
  selector "$@"
}

# ---- running a plan

prefix() { while IFS= read -r line || [ -n "$line" ]; do printf '[%s] %s\n' "$1" "$line"; done; }
minutes() { awk -v s="$1" 'BEGIN { printf "%.1f", s / 60 }'; }

# counts <report.txt>: "passed failed errors skipped" from its first line (SUMMARY: 14 passed, 1 failed in 480s).
counts() {
  sed -n '1s/^SUMMARY: //p' "$1" 2>/dev/null | awk '{
    p = f = e = s = 0
    n = split($0, w, /[ ,;]+/)
    for (i = 2; i <= n; i++) {
      if (w[i] == "passed") p = w[i - 1]; else if (w[i] == "failed") f = w[i - 1]
      else if (w[i] == "error" || w[i] == "errors") e = w[i - 1]; else if (w[i] == "skipped") s = w[i - 1]
    }
    print p, f, e, s
  }'
}

# run_lane <lane> <label>: the lane's runs, one after another, each a run.sh test of its own.
run_lane() {
  k=0
  grep -v '^#' "$PLAN" | while IFS="$TAB" read -r lane timings templates args; do
    [ "$lane" = "$1" ] || continue
    k=$((k + 1))
    name=$2
    [ "$k" = 1 ] || name="$2-$k"
    [ "$templates" != - ] || templates=""
    started=$(date +%s)
    rc=0
    set -f
    # The plan's pytest arguments, one word each (they never hold a space).
    # shellcheck disable=SC2086
    E2E_INSTANCE=$(instance_of "$2") E2E_TIMINGS=$timings E2E_TEMPLATES=$templates E2E_OUT_NAME=$name \
      sh "$SELF" test -v $args </dev/null || rc=$?
    set +f
    echo "$rc $(($(date +%s) - started)) $name" >>"$W/lane-$2"
  done
}

summary() {
  short=${TITLE%%; estimate*}
  passed=0; failed=0; errors=0; ok=$UNIT_OK; times=""
  echo "===== E2E SUMMARY ====="
  echo "tier $TITLE"
  echo "unit tests: $UNIT_STATE"
  for l in $LABELS; do
    if [ -f "$W/up-$l.failed" ]; then
      echo "lane $l: its rig didn't come up (see [$l] above)"; ok=0; continue
    fi
    if [ ! -f "$W/lane-$l" ]; then
      echo "lane $l: not run"; continue
    fi
    runs=0; secs=0; lp=0; lf=0; le=0; ls=0; details=""
    while read -r rc s name; do
      runs=$((runs + 1)); secs=$((secs + s))
      rep="$HERE/out/$name/report.txt"
      # Four numbers, one word each.
      # shellcheck disable=SC2046
      set -- $(counts "$rep") 0 0 0 0
      lp=$((lp + $1)); lf=$((lf + $2)); le=$((le + $3)); ls=$((ls + $4))
      if [ "$rc" != 0 ]; then
        ok=0
        fails=$(grep '^FAILED: ' "$rep" 2>/dev/null | sed 's/^FAILED: \([^ ]*\).*/\1/' || true)
        if [ -n "$fails" ]; then
          for f in $fails; do details="$details
  FAILED $f (lane $l; out/$name/report.txt)"; done
        else
          details="$details
  run $name ended with exit code $rc ($( [ -f "$rep" ] && echo "out/$name/report.txt" || echo "no report; see [$l] above"))"
        fi
      fi
    done <"$W/lane-$l"
    word=runs; [ "$runs" != 1 ] || word=run
    line="lane $l: $runs $word, $lp passed"
    [ "$lf" = 0 ] || line="$line, $lf failed"
    [ "$le" = 0 ] || line="$line, $le errors"
    [ "$ls" = 0 ] || line="$line, $ls skipped"
    echo "$line in $(minutes "$secs") min$details"
    passed=$((passed + lp)); failed=$((failed + lf)); errors=$((errors + le))
    times="$times${times:+, }lane $l $(minutes "$secs") min"
  done
  tests="$passed passed"
  [ "$failed" = 0 ] || tests="$tests, $failed failed"
  [ "$errors" = 0 ] || tests="$tests, $errors errors"
  echo "in short: E2E $short | unit ${UNIT_STATE%% (*} | ${times:-no rig run} | $tests"
  echo "===== END OF SUMMARY ====="
  [ "$ok" = 1 ]
}

# run_plan <plan.tsv> <with unit tests: 1 or empty>: the unit tests and the rigs' up side by side, the unit tests gate
# the scenarios, then the lanes side by side. Exit 0 only when everything passed.
run_plan() {
  PLAN=$1
  [ -f "$PLAN" ] || refuse "no plan file $PLAN"
  TITLE=$(sed -n 's/^# tier //p' "$PLAN" | head -n 1)
  lanes=$(sed -n 's/^# lanes \([1-3]\)$/\1/p' "$PLAN" | head -n 1)
  [ -n "$TITLE" ] && [ -n "$lanes" ] || refuse "$PLAN isn't a plan (impact.py plan --format tsv)"
  bad=$(grep -v '^#' "$PLAN" | grep -cvE "^[1-3]${TAB}(test|production)${TAB}(-|[a-z0-9-]+(,[a-z0-9-]+)*)${TAB}[^${TAB}]*\$" || true)
  [ "$bad" = 0 ] || refuse "$PLAN has $bad line(s) that aren't a lane, timings, templates and pytest's arguments"
  if [ "$lanes" -gt 1 ] && [ -n "$INSTANCE" ]; then refuse "E2E_INSTANCE picks the rig for one lane; this plan has $lanes"; fi
  workdir
  used=$(grep -v '^#' "$PLAN" | cut -f1 | sort -u | tr '\n' ' ')
  LABELS=""
  for lane in $used; do
    if [ "$lanes" = 1 ]; then LABELS="$LABELS $(label "$INSTANCE")"; else LABELS="$LABELS $(lanes_of 3 | cut -d' ' -f"$lane")"; fi
  done
  # shellcheck disable=SC2086
  hold $LABELS
  say "plan: $TITLE"
  UNIT_OK=1; UNIT_STATE="not run (--no-unit)"
  if [ -n "$2" ]; then
    ( started=$(date +%s); rc=0; unit_tests || rc=$?; echo "$rc $(($(date +%s) - started))" >"$W/unit" ) 2>&1 </dev/null \
      | prefix unit &
    unit_pid=$!
  fi
  ups=""
  for l in $LABELS; do
    ( rc=0; E2E_INSTANCE=$(instance_of "$l") sh "$SELF" up || rc=$?; [ "$rc" = 0 ] || : >"$W/up-$l.failed" ) 2>&1 </dev/null \
      | prefix "$l" &
    ups="$ups $!"
  done
  if [ -n "$2" ]; then
    wait "$unit_pid" || true
    rc=1; secs=0
    [ ! -f "$W/unit" ] || read -r rc secs <"$W/unit"
    if [ "$rc" = 0 ]; then UNIT_STATE="passed ($(minutes "$secs") min)"; else UNIT_STATE="failed ($(minutes "$secs") min; see [unit] above)"; UNIT_OK=0; fi
  fi
  for p in $ups; do wait "$p" || true; done
  if [ "$UNIT_OK" = 1 ]; then
    pids=""
    for lane in $used; do
      if [ "$lanes" = 1 ]; then l=$(label "$INSTANCE"); else l=$(lanes_of 3 | cut -d' ' -f"$lane"); fi
      [ ! -f "$W/up-$l.failed" ] || continue
      run_lane "$lane" "$l" 2>&1 | prefix "$l" &
      pids="$pids $!"
    done
    for p in $pids; do wait "$p" || true; done
  fi
  summary
}

# ---- commands

case "${1:-}" in
  ''|impact|smoke|wide|full|release|groups|--*)
    options "$@"
    workdir
    make_plan
    cat "$W/plan.txt"
    run_plan "$W/plan.tsv" "$([ -n "$NO_UNIT" ] || echo 1)"
    ;;
  plan)
    shift
    options "$@"
    workdir
    make_plan
    cat "$W/plan.txt"
    [ -z "$OUT" ] || cp "$W/plan.tsv" "$OUT"
    ;;
  run-plan)
    shift
    [ $# -ge 1 ] || refuse "run-plan takes a plan file (impact.py plan --format tsv)"
    file=$1
    shift
    options "$@"
    run_plan "$file" "$([ -n "$NO_UNIT" ] || echo 1)"
    ;;
  unit)
    unit_tests
    ;;
  busy)
    shift
    options "$@"
    for l in $(lanes_of "$LANES"); do
      r=$(rig_of "$l")
      ! rig_busy "$r" || busy "$l"
      case " ${E2E_LOCKED:-} " in *" $r "*) continue ;; esac
      if command -v flock >/dev/null 2>&1; then
        flock -n "${E2E_LOCK_DIR:-/tmp}/gbe2e-lock-$r" true || busy "$l"
      fi
    done
    say "free: $(for l in $(lanes_of "$LANES"); do printf '%s ' "$(title_of "$l")"; done)"
    ;;
  up)
    hold "$(label "$INSTANCE")"
    engine
    sync_tree
    inner up
    say "the rig is up: Gitberth's UI is on this host's port $UI_PORT (run.sh password)"
    ;;
  test)
    shift
    hold "$(label "$INSTANCE")"
    name=${E2E_OUT_NAME:-$(label "$INSTANCE")}
    case "$name" in ''|*[!A-Za-z0-9._-]*|.*) refuse "E2E_OUT_NAME is a plain folder name: $name" ;; esac
    rm -rf "${HERE:?}/out/$name"
    engine
    sync_tree
    inner up
    inner reset
    rc=0
    inner test "$@" || rc=$?
    # The run's junit.xml, report.txt and screenshots, whatever happened.
    mkdir -p "$HERE/out"
    docker cp "$RIG:/e2e/out/." "$HERE/out/$name" >/dev/null 2>&1 || true
    exit "$rc"
    ;;
  down)
    hold "$(label "$INSTANCE")"
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
  help|-h)
    usage
    ;;
  *)
    usage
    exit 2
    ;;
esac
