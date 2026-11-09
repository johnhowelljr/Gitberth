#!/bin/sh
# Builds each sample project with the Dockerfile Gitberth generated for it, starts it, and checks that it answers.
#
#   check-on-docker.sh [--keep] <samples dir> <generated dir> [sample ...]
#   check-on-docker.sh --clean
#
# <generated dir> comes from running, on any machine with the .NET SDK:
#   GITBERTH_SAMPLES_OUT=<dir> dotnet test tests/Gitberth.Tests --filter Writes_each_samples_dockerfile_for_the_docker_check
#
# By default containers only listen on 127.0.0.1, and every image and container is removed afterwards.
# With --keep, each sample that answers stays running as sample-<name>, on this host's LAN address from port 9101 up,
# and an index of them all is served on port 9100. --clean removes those containers and their images again.
# Needs docker, curl and python3.
set -u

clean() {
  for c in $(docker ps -aq --filter label=gitberth.sample); do
    image=$(docker inspect -f '{{.Config.Image}}' "$c")
    docker rm -f "$c" > /dev/null 2>&1
    case "$image" in gitberth-sample-*) docker rmi -f "$image" > /dev/null 2>&1 ;; esac
  done
}

keep=""
case "${1:-}" in
  --clean) clean; echo "removed the samples"; exit 0 ;;
  --keep) keep=1; shift ;;
esac
samples=$1
generated=$2
shift 2
[ $# -gt 0 ] || set -- $(ls "$generated")
# Each run hands out the ports afresh, so the samples kept from the last run go first. Their images stay, as cache.
if [ -n "$keep" ]; then
  old=$(docker ps -aq --filter label=gitberth.sample)
  [ -n "$old" ] && docker rm -f $old > /dev/null 2>&1
fi

field() { python3 -c 'import json, sys; v = json.load(open(sys.argv[1])).get(sys.argv[2]); print("" if v is None else v)' "$1" "$2"; }
running() { [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = "true" ]; }

host_ip=$(hostname -I 2>/dev/null | awk '{print $1}')
rows=""
next_port=9101
passed=0
failed=""
for name in "$@"; do
  dir="$generated/$name"
  meta="$dir/meta.json"
  context="$samples/$name/$(field "$meta" context)"
  port=$(field "$meta" port)
  path=$(field "$meta" path)
  expect=$(field "$meta" expect)
  worker=$(field "$meta" worker)
  title=$(field "$meta" title)
  image="gitberth-sample-$name"
  started=$(date +%s)

  if ! docker build -t "$image" -f "$dir/Dockerfile" "$context" > "$dir/build.log" 2>&1; then
    echo "FAIL $name: the build failed"
    tail -30 "$dir/build.log" | sed 's/^/    /'
    failed="$failed $name"
    rows="$rows<tr><td>$name</td><td>$title</td><td class=fail>build failed</td><td></td></tr>"
    continue
  fi

  if [ -n "$keep" ]; then
    docker rm -f "sample-$name" > /dev/null 2>&1
    hostport=""
    [ -n "$port" ] && hostport=$next_port && next_port=$((next_port + 1))
    run=$(docker run -d --name "sample-$name" --label "gitberth.sample=$name" --restart unless-stopped --memory 1g \
      --env-file "$dir/env" ${port:+-p 0.0.0.0:$hostport:$port} "$image" 2>&1)
  else
    run=$(docker run -d --env-file "$dir/env" ${port:+-p 127.0.0.1::$port} "$image" 2>&1)
  fi
  if [ $? -ne 0 ]; then
    echo "FAIL $name: it didn't start: $run"
    failed="$failed $name"
    rows="$rows<tr><td>$name</td><td>$title</td><td class=fail>didn't start</td><td></td></tr>"
    docker rmi -f "$image" > /dev/null 2>&1
    continue
  fi
  cid=$run

  ok=""
  if [ -n "$port" ] && [ "$worker" != "True" ]; then
    local_port=$(docker port "$cid" "$port/tcp" 2>/dev/null | head -1 | sed 's/.*://')
    for _ in $(seq 1 120); do
      if body=$(curl -fsS --max-time 5 "http://127.0.0.1:$local_port$path" 2> /dev/null) && printf '%s' "$body" | grep -qF -- "$expect"; then
        ok=1
        break
      fi
      running "$cid" || break
      sleep 1
    done
  else
    sleep 10
    running "$cid" && ok=1
  fi

  took=$(( $(date +%s) - started ))
  if [ -n "$ok" ]; then
    echo "PASS $name (${took}s)${keep:+ ${hostport:+http://$host_ip:$hostport$path}}"
    passed=$((passed + 1))
    if [ -n "$keep" ]; then
      link=""
      [ -n "$hostport" ] && [ "$worker" != "True" ] && link="<a href=\"http://$host_ip:$hostport$path\">http://$host_ip:$hostport$path</a>"
      [ "$worker" = "True" ] && link="worker, no port"
      rows="$rows<tr><td>$name</td><td>$title</td><td class=pass>running</td><td>$link</td></tr>"
      continue
    fi
  else
    echo "FAIL $name: no answer containing \"$expect\" at $path"
    docker logs --tail 40 "$cid" 2>&1 | sed 's/^/    /'
    failed="$failed $name"
    rows="$rows<tr><td>$name</td><td>$title</td><td class=fail>no answer</td><td></td></tr>"
  fi
  docker rm -f "$cid" > /dev/null 2>&1
  docker rmi -f "$image" > /dev/null 2>&1
done

if [ -n "$keep" ]; then
  index=$(mktemp)
  cat > "$index" <<EOF
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Gitberth samples</title>
<style>
  body { font: 15px/1.5 system-ui, sans-serif; margin: 2rem auto; max-width: 960px; padding: 0 1rem; color: #1f2328; background: #fff; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; padding: .45rem .6rem; border-bottom: 1px solid #d0d7de; }
  .pass { color: #1a7f37; } .fail { color: #cf222e; }
  @media (prefers-color-scheme: dark) { body { color: #e6edf3; background: #0d1117; } th, td { border-color: #30363d; } a { color: #58a6ff; } }
</style></head><body>
<h1>Gitberth samples</h1>
<p>Each sample built with the Dockerfile Gitberth generates for it, running on $host_ip. Updated $(date -u '+%Y-%m-%d %H:%M UTC').</p>
<table><thead><tr><th>Sample</th><th>Detected as</th><th>State</th><th>Address</th></tr></thead><tbody>$rows</tbody></table>
</body></html>
EOF
  # mktemp makes the file readable by its owner only, and docker cp keeps that: nginx's worker couldn't read it.
  chmod 644 "$index"
  docker rm -f sample-index > /dev/null 2>&1
  docker run -d --name sample-index --label gitberth.sample=index --restart unless-stopped --memory 64m -p 0.0.0.0:9100:80 nginx:stable-alpine > /dev/null
  docker cp "$index" sample-index:/usr/share/nginx/html/index.html > /dev/null
  rm -f "$index"
  echo "index: http://$host_ip:9100/"
fi

echo "passed $passed${failed:+, failed:$failed}"
[ -z "$failed" ]
