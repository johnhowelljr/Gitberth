#!/bin/sh
# Gitberth's installer: Docker if it's missing, then Gitberth, started and answering, in one go.
#
#   curl -fsSL https://raw.githubusercontent.com/johnhowelljr/Gitberth/main/install.sh | sh
#
# Run it as root, or with sudo:  curl -fsSL .../install.sh | sudo sh
# Run it again at any time: it finds the Gitberth that's there, backs up its data, and updates it to the newest release,
# keeping its settings. Nothing is asked. Settings go just before `sh` (after sudo), like  ... | sudo GITBERTH_PORT=8090 sh
#
#   GITBERTH_PORT            the port Gitberth's page is on                           8080
#   GITBERTH_ADMIN_PASSWORD  the admin password for its first start (10+ characters)  made up, and shown at the end
#   GITBERTH_PUBLIC_HOST     this machine's LAN address, for the addresses it shows   detected
#   GITBERTH_TZ              its time zone, like Europe/London                         this machine's
#   GITBERTH_DIR             where its compose file and .env go                       /opt/gitberth, or where it is now
#   GITBERTH_IMAGE           the image to run                                         ghcr.io/johnhowelljr/gitberth:latest
#   GITBERTH_START=no        set everything up but don't start it (for restoring a backup onto a new machine)
#
# Nothing is changed until every check has passed. Everything happens in main() at the bottom, which runs only once the
# whole script has arrived, so a cut-off download does nothing.

REPO="johnhowelljr/Gitberth"
# Where the compose file comes from; GITBERTH_RAW points it elsewhere, to try a branch or a fork.
RAW="${GITBERTH_RAW:-https://raw.githubusercontent.com/$REPO/main}"
DOCS="https://github.com/$REPO/blob/main/docs"
VOLUME="gitberth_gitberth-data"
# docker-compose.yml as it was published up to v1.0.0: an untouched copy of it is replaced with the current one.
OLD_COMPOSE="cbf5096b1fd6dee851dfcbf1376231000a8184104331f810a7523ef8ce46b63c"

if [ -t 1 ]; then B=$(printf '\033[1m'); R=$(printf '\033[31m'); Y=$(printf '\033[33m'); G=$(printf '\033[32m'); N=$(printf '\033[0m')
else B=; R=; Y=; G=; N=; fi

say() { printf '%s==>%s %s\n' "$B" "$N" "$*"; }
note() { printf '    %s\n' "$*"; }
warn() { printf '%s  ! %s%s\n' "$Y" "$*" "$N"; }
# fail "why" ["what to do" ...]: says so, puts back anything it moved aside, and stops.
fail() {
    put_back_stray
    # The files a check was made with, not yet in place: nothing changed, so they go.
    if [ -n "${dir:-}" ]; then rm -f "$dir/.env.new" "$dir/.env.new.tmp" "$dir/docker-compose.yml.new"; fi
    printf '\n%sGitberth isn'"'"'t running:%s %s\n' "$R" "$N" "$1" >&2
    shift
    for line in "$@"; do printf '  %s\n' "$line" >&2; done
    printf '\n  The install guide: %s/getting-started/install.md\n' "$DOCS" >&2
    exit 1
}

# A URL's body on stdout, with curl or wget.
fetch() {
    if command -v curl > /dev/null 2>&1; then curl -fsSL --retry 3 "$1"
    elif command -v wget > /dev/null 2>&1; then wget -qO- "$1"
    else return 1; fi
}

# Whether a page answers, within 5 seconds.
probe() {
    if command -v curl > /dev/null 2>&1; then curl -fsS -o /dev/null --max-time 5 "$1" 2> /dev/null
    else wget -q -O /dev/null -T 5 "$1" 2> /dev/null; fi
}

docker_up() { docker info > /dev/null 2>&1; }
dockerd_running() { pidof dockerd > /dev/null 2>&1 || pgrep -x dockerd > /dev/null 2>&1; }

# Starts Docker's daemon whichever way this machine starts services, and waits for it.
start_docker() {
    if command -v systemctl > /dev/null 2>&1 && [ -d /run/systemd/system ]; then
        systemctl enable --now docker > /dev/null 2>&1 || true
    elif command -v rc-service > /dev/null 2>&1; then
        rc-update add docker default > /dev/null 2>&1 || true
        rc-service docker start > /dev/null 2>&1 || true
    elif command -v service > /dev/null 2>&1; then
        service docker start > /dev/null 2>&1 || true
    fi
    i=0
    while [ "$i" -lt 30 ]; do docker_up && return 0; i=$((i + 1)); sleep 1; done
    # A daemon that's still starting (its service returned early, on a slow disk say): wait for it, never start a second.
    if dockerd_running; then
        i=0
        while [ "$i" -lt 120 ]; do docker_up && return 0; i=$((i + 1)); sleep 1; done
        return 1
    fi
    # Nothing could start it: the daemon itself, in the background, as a last resort.
    if command -v dockerd > /dev/null 2>&1; then
        nohup dockerd > /var/log/gitberth-dockerd.log 2>&1 &
        daemon=$!
        i=0
        while [ "$i" -lt 60 ]; do
            if docker_up; then
                if kill -0 "$daemon" 2> /dev/null; then
                    warn "Docker was started by hand: nothing on this machine starts it after a reboot. Its log: /var/log/gitberth-dockerd.log"
                fi
                return 0
            fi
            i=$((i + 1)); sleep 1
        done
    fi
    return 1
}

# Waits while another program installs packages (a fresh Ubuntu's unattended upgrades, say), up to 10 minutes.
wait_for_apt() {
    command -v fuser > /dev/null 2>&1 || return 0
    i=0
    while fuser /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock /var/lib/apt/lists/lock > /dev/null 2>&1; do
        [ "$i" -eq 0 ] && note "Waiting for another package install on this machine to finish..."
        i=$((i + 1)); [ "$i" -ge 120 ] && return 0; sleep 5
    done
}

# Docker's compose plugin from its GitHub release, checked against the release's checksum before it's put in place.
install_compose_binary() {
    url="https://github.com/docker/compose/releases/latest/download/docker-compose-linux-$(uname -m)"
    bin="$(mktemp)"; sums="$(mktemp)"
    if fetch "$url" > "$bin" && fetch "$url.sha256" > "$sums"; then
        expected="$(awk '{print $1; exit}' "$sums")"
        actual="$(sha256sum "$bin" | awk '{print $1}')"
        if [ -n "$expected" ] && [ "$expected" = "$actual" ]; then
            chmod +x "$bin"
            if "$bin" version > /dev/null 2>&1; then
                mkdir -p /usr/local/lib/docker/cli-plugins
                mv "$bin" /usr/local/lib/docker/cli-plugins/docker-compose
                rm -f "$sums"
                note "Docker Compose was installed from its GitHub release in /usr/local/lib/docker/cli-plugins."
                return 0
            fi
        fi
    fi
    rm -f "$bin" "$sums"
    return 1
}

# The value of a key in a .env file as Compose reads it: the last line that sets it, without a CR, its quotes, or an
# inline comment after an unquoted value.
env_get() {
    [ -f "$1" ] || return 0
    awk -v k="$2" '
        { sub(/\r$/, "") }
        $0 ~ "^[ \t]*(export[ \t]+)?" k "[ \t]*=" {
            v = $0; sub(/^[^=]*=[ \t]*/, "", v)
            if (v ~ /^'\''/) { sub(/^'\''/, "", v); sub(/'\''.*$/, "", v) }
            else if (v ~ /^"/) { sub(/^"/, "", v); sub(/".*$/, "", v) }
            else { sub(/[ \t]+#.*$/, "", v); sub(/[ \t]+$/, "", v) }
            val = v
        }
        END { printf "%s", val }' "$1"
}

# Sets a key in a .env file (single-quoted), replacing every line that set it, keeping the file's mode.
env_set() {
    awk -v k="$2" -v line="$2='$3'" '
        { sub(/\r$/, "") }
        $0 ~ "^[ \t]*(export[ \t]+)?" k "[ \t]*=" { if (!done) { print line; done = 1 }; next }
        { print }
        END { if (!done) print line }' "$1" > "$1.tmp"
    cat "$1.tmp" > "$1"
    rm -f "$1.tmp"
}

# docker compose for Gitberth's project, with its files named, so nothing from the environment or elsewhere joins in.
dc() {
    if [ -f "$dir/docker-compose.override.yml" ]; then
        docker compose -p gitberth --project-directory "$dir" --env-file "$envfile" -f "$composefile" -f "$dir/docker-compose.override.yml" "$@"
    else
        docker compose -p gitberth --project-directory "$dir" --env-file "$envfile" -f "$composefile" "$@"
    fi
}

# A container's environment variable, as the container has it.
container_env() {
    docker container inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$1" 2> /dev/null | sed -n "s/^$2=//p" | head -n 1
}

# Who holds a host port: a running container (by name) other than the ones given, or "this machine" for anything else.
port_holder() {
    p="$1"; shift
    docker ps --format '{{.ID}} {{.Names}} {{.Ports}}' | while read -r id name ports; do
        for skip in "$@"; do
            if [ -n "$skip" ]; then case "$id" in "$skip"*) continue 2 ;; esac; fi
        done
        case " $ports" in *":$p->"*) echo "$name"; break ;; esac
    done
}
port_in_use_on_host() { command -v ss > /dev/null 2>&1 && ss -Hltn "sport = :$1" 2> /dev/null | grep -q .; }

# The first port from 8090 to 8099 that nothing holds, for the advice when the one asked for is taken.
free_port() {
    p=8090
    while [ "$p" -le 8099 ]; do
        if [ -z "$(port_holder "$p")" ] && ! port_in_use_on_host "$p"; then echo "$p"; return; fi
        p=$((p + 1))
    done
    echo 8090
}

# The installer's command again, with the settings given plus the one in $1, for advice (never the password itself).
again() {
    cmd="curl -fsSL $RAW/install.sh |"
    if [ "$(id -u)" -ne 0 ]; then cmd="$cmd sudo"; fi
    if [ -n "$want_dir" ]; then cmd="$cmd GITBERTH_DIR=$want_dir"; fi
    if [ -n "$want_image" ]; then cmd="$cmd GITBERTH_IMAGE=$want_image"; fi
    if [ -n "$want_host" ]; then cmd="$cmd GITBERTH_PUBLIC_HOST=$want_host"; fi
    if [ -n "$want_tz" ]; then cmd="$cmd GITBERTH_TZ=$want_tz"; fi
    if [ -n "$want_password" ]; then cmd="$cmd GITBERTH_ADMIN_PASSWORD=..."; fi
    if [ -n "$1" ]; then cmd="$cmd $1"; fi
    printf '%s sh' "$cmd"
}

# This machine's LAN address: the one its default route leaves from.
detect_host() {
    found=""
    if command -v ip > /dev/null 2>&1; then
        found="$(ip -4 route get 1.1.1.1 2> /dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
    fi
    if [ -z "$found" ]; then found="$(hostname -I 2> /dev/null | awk '{print $1}')"; fi
    printf '%s' "$found"
}

# Whether Gitberth's data volume holds a Gitberth already (its database), not just an empty volume a failed start left.
has_data() {
    docker volume inspect "$VOLUME" > /dev/null 2>&1 || return 1
    mount="$(docker volume inspect -f '{{.Mountpoint}}' "$VOLUME" 2> /dev/null)"
    if [ -n "$mount" ] && [ -d "$mount" ]; then [ -s "$mount/gitberth.db" ]; return; fi
    # The volume's folder isn't readable from here: assume it's in use.
    return 0
}

# A label of a container, from its labels as JSON (which works for a container without any).
label_of() {
    docker container inspect -f '{{json .Config.Labels}}' "$1" 2> /dev/null | sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p"
}

# A Gitberth container the installer didn't make, moved aside (stopped, renamed, no restart policy) to make room. If the
# install then fails, it's put back as it was.
stray=""; stray_aside=""; stray_policy=""
put_back_stray() {
    [ -n "$stray_aside" ] || return 0
    docker rm -f gitberth > /dev/null 2>&1 || true
    docker rename "$stray_aside" gitberth > /dev/null 2>&1 || true
    docker update --restart "${stray_policy:-no}" gitberth > /dev/null 2>&1 || true
    docker start gitberth > /dev/null 2>&1 || true
    printf '  (The gitberth container that was there before is back as it was.)\n' >&2
    stray_aside=""
}

main() {
    set -eu
    # The settings as given, read once and then cleared: Compose would otherwise take them from the environment over
    # .env, and the next plain `docker compose up -d` would undo them.
    want_dir="${GITBERTH_DIR:-}"; want_port="${GITBERTH_PORT:-}"; want_image="${GITBERTH_IMAGE:-}"
    want_password="${GITBERTH_ADMIN_PASSWORD:-}"; want_host="${GITBERTH_PUBLIC_HOST:-}"; want_tz="${GITBERTH_TZ:-}"
    start="${GITBERTH_START:-yes}"; use_published="${GITBERTH_USE_PUBLISHED:-}"; on_pve="${GITBERTH_ON_PROXMOX_HOST:-}"
    unset GITBERTH_DIR GITBERTH_PORT GITBERTH_IMAGE GITBERTH_ADMIN_PASSWORD GITBERTH_PUBLIC_HOST GITBERTH_TZ TZ \
        GITBERTH_DATA COMPOSE_FILE COMPOSE_PROJECT_NAME COMPOSE_PROFILES COMPOSE_ENV_FILES COMPOSE_PATH_SEPARATOR

    printf '%sGitberth installer%s\n\n' "$B" "$N"

    # ---- This machine ----
    [ "$(id -u)" -eq 0 ] || fail "the installer needs root." "Run it like this (your settings go after sudo):" "    $(again "")"
    case "$(uname -m)" in
        x86_64 | amd64 | aarch64 | arm64) ;;
        *) fail "Gitberth's image is for Intel/AMD (x86_64) and ARM (aarch64) machines; this one is $(uname -m)." ;;
    esac
    if command -v pveversion > /dev/null 2>&1 && [ "$on_pve" != 1 ]; then
        fail "this is a Proxmox host." \
            "Docker on the Proxmox host changes its firewall, and the VMs and LXCs on its bridges can lose their network." \
            "Install Gitberth in an LXC or a VM instead; the install guide shows how."
    fi
    virt=""
    if command -v systemd-detect-virt > /dev/null 2>&1; then virt="$(systemd-detect-virt -c 2> /dev/null || true)"; fi
    if [ -z "$virt" ] || [ "$virt" = none ]; then
        if grep -qa 'container=lxc' /proc/1/environ 2> /dev/null; then virt=lxc; fi
    fi
    if ! command -v curl > /dev/null 2>&1 && ! command -v wget > /dev/null 2>&1; then
        if command -v apt-get > /dev/null 2>&1; then
            wait_for_apt
            if ! { apt-get update -qq > /dev/null 2>&1 && apt-get install -y -qq curl ca-certificates > /dev/null 2>&1; }; then
                fail "curl couldn't be installed."
            fi
        else fail "neither curl nor wget is installed." "Install curl, then run this again."; fi
    fi
    case "${DOCKER_HOST:-}" in
        '' | unix:///var/run/docker.sock | unix:///run/docker.sock) ;;
        *) fail "DOCKER_HOST points at $DOCKER_HOST." "The installer sets Gitberth up on this machine's own Docker: run it with DOCKER_HOST unset." ;;
    esac
    [ -n "${DOCKER_HOST:-}" ] || export DOCKER_CONTEXT=default

    # ---- Docker ----
    if ! command -v docker > /dev/null 2>&1; then
        say "Installing Docker"
        if [ -f /etc/alpine-release ]; then
            apk add --no-cache docker docker-cli-compose > /dev/null || fail "Docker couldn't be installed with apk."
        else
            installer="$(mktemp)"
            fetch https://get.docker.com > "$installer" || fail "Docker's installer couldn't be downloaded from get.docker.com." \
                "Check that this machine reaches the internet:  curl -sI https://get.docker.com"
            if command -v apt-get > /dev/null 2>&1; then wait_for_apt; fi
            if ! sh "$installer" > /var/log/gitberth-docker-install.log 2>&1; then
                rm -f "$installer"
                if grep -q 'Could not get lock' /var/log/gitberth-docker-install.log; then
                    fail "another program was installing packages, so Docker couldn't be installed." "Wait a few minutes, then run this again."
                fi
                fail "Docker's installer failed." "What it said is in /var/log/gitberth-docker-install.log (the last lines:)" \
                    "$(tail -n 5 /var/log/gitberth-docker-install.log)"
            fi
            rm -f "$installer"
        fi
    fi
    docker_up || start_docker || fail "Docker is installed but isn't running, and couldn't be started." \
        "See why with:  systemctl status docker   or   journalctl -u docker"
    if docker version 2> /dev/null | grep -qi podman; then
        fail "this machine's docker command is Podman's." "Gitberth needs Docker Engine: https://docs.docker.com/engine/install/"
    fi
    version="$(docker version --format '{{.Server.Version}}' 2> /dev/null || echo 0)"
    major="${version%%.*}"
    case "$major" in '' | *[!0-9]*) major=0 ;; esac
    [ "$major" -ge 25 ] || fail "Docker $version is too old: Gitberth needs Docker 25 or later." \
        "Update Docker from Docker's own packages (https://docs.docker.com/engine/install/), then run this again."
    note "Docker $version"

    if ! docker compose version > /dev/null 2>&1; then
        say "Installing Docker Compose"
        if command -v apt-get > /dev/null 2>&1 && { wait_for_apt; apt-get install -y -qq docker-compose-plugin > /dev/null 2>&1; }; then :
        elif [ -f /etc/alpine-release ] && apk add --no-cache docker-cli-compose > /dev/null 2>&1; then :
        else install_compose_binary || true; fi
        docker compose version > /dev/null 2>&1 || fail "Docker Compose couldn't be installed." \
            "Install Docker's compose plugin (https://docs.docker.com/compose/install/linux/), then run this again."
    fi
    compose_version="$(docker compose version --short 2> /dev/null | sed 's/^v//')"
    compose_major="${compose_version%%.*}"; compose_minor="${compose_version#*.}"; compose_minor="${compose_minor%%.*}"
    case "$compose_major$compose_minor" in '' | *[!0-9]*) compose_major=0; compose_minor=0 ;; esac
    if [ "$compose_major" -lt 2 ] || { [ "$compose_major" -eq 2 ] && [ "$compose_minor" -lt 20 ]; }; then
        fail "Docker Compose $compose_version is too old: Gitberth needs 2.20 or later." \
            "Update Docker's compose plugin (https://docs.docker.com/compose/install/linux/), then run this again."
    fi

    # ---- The Gitberth that's here already ----
    existing="$(docker ps -aq --filter label=com.docker.compose.project=gitberth --filter label=com.docker.compose.service=gitberth | head -n 1)"
    existing_dir=""; existing_running=no
    if [ -n "$existing" ]; then
        if [ "$(docker container inspect -f '{{.State.Running}}' "$existing" 2> /dev/null)" = true ]; then existing_running=yes; fi
        existing_dir="$(label_of "$existing" com.docker.compose.project.working_dir)"
        files="$(label_of "$existing" com.docker.compose.project.config_files)"
        # Made with other compose files than the installer's (built from source, say): updated the way it was made.
        own=yes
        old_ifs="$IFS"; IFS=,
        for f in $files; do
            case "$f" in "$existing_dir/docker-compose.yml" | "$existing_dir/docker-compose.override.yml") ;; *) own=no ;; esac
        done
        IFS="$old_ifs"
        if [ "$own" = no ] && [ "$use_published" != 1 ]; then
            flags=""
            old_ifs="$IFS"; IFS=,
            for f in $files; do flags="$flags -f ${f#"$existing_dir"/}"; done
            IFS="$old_ifs"
            fail "this Gitberth was set up with its own compose files, not the installer's:" "    $files" "" \
                "Update it the way it was made:" \
                "    cd $existing_dir && git pull && docker compose$flags up -d --build    (git pull if it's built from source)" \
                "Or switch it to the published image (its data is backed up first):" \
                "    $(again GITBERTH_USE_PUBLISHED=1)"
        fi
        if [ -n "$want_dir" ]; then
            case "$want_dir" in /*) ;; *) want_dir="$(pwd -P)/$want_dir" ;; esac
            if [ "${want_dir%/}" != "$existing_dir" ]; then
                fail "Gitberth is installed from $existing_dir already, and one Docker host runs one Gitberth." \
                    "To update it, run this again without GITBERTH_DIR (or with GITBERTH_DIR=$existing_dir)."
            fi
        fi
    fi
    # Any other Gitberth container: one in another Compose project, one run by hand, or a second one.
    for id in $(docker ps -aq); do
        if [ "$id" = "$existing" ]; then continue; fi
        info="$(docker container inspect -f '{{.Name}}|{{.State.Running}}|{{.Config.Image}}' "$id" 2> /dev/null)" || continue
        name="${info%%|*}"; name="${name#/}"; rest="${info#*|}"
        running="${rest%%|*}"; image_of="${rest#*|}"
        case "$name" in gitberth-before-install-*) continue ;; esac
        case "$image_of" in
            *johnhowelljr/gitberth* | gitberth:*) ;;
            *) if [ "$name" != gitberth ]; then continue; fi ;;
        esac
        # Gitberth's own short-lived helpers run its image too.
        if [ -n "$(label_of "$id" gitberth.helper)" ]; then continue; fi
        project="$(label_of "$id" com.docker.compose.project)"
        project_dir="$(label_of "$id" com.docker.compose.project.working_dir)"
        if [ -n "$project" ] && [ "$project" != gitberth ]; then
            fail "Gitberth already runs as $name, in the Compose project $project ($project_dir)." \
                "Update it there:  cd $project_dir && docker compose pull && docker compose up -d"
        fi
        if [ "$name" = gitberth ]; then
            case "$image_of" in *johnhowelljr/gitberth* | gitberth:*) ;; *)
                fail "there's already a container called gitberth, running $image_of." \
                    "Rename or remove it (docker rename gitberth gitberth-old), then run this again." ;;
            esac
            if [ "$(docker container inspect -f '{{.HostConfig.AutoRemove}}' "$id")" = true ]; then
                fail "a gitberth container was started by hand with --rm: stopping it would delete it, and its data with it." \
                    "Copy its data out first:" \
                    "    docker run --rm --volumes-from gitberth -v \"\$PWD\":/backup alpine tar czf /backup/gitberth-data.tgz -C /data ." \
                    "then stop it (docker stop gitberth) and run this again."
            fi
            stray="$id"
        elif [ "$running" = true ]; then
            fail "another Gitberth runs here already, as the container $name." \
                "Two would manage the same containers. Stop it (docker stop $name), or keep it and skip this install."
        fi
    done

    # ---- The folder ----
    dir="${want_dir:-${existing_dir:-/opt/gitberth}}"
    case "$dir" in /*) ;; *) dir="$(pwd -P)/$dir" ;; esac
    mkdir -p "$dir" 2> /dev/null || fail "the folder $dir couldn't be made." "Choose another one with GITBERTH_DIR."
    cd "$dir" || fail "the folder $dir couldn't be opened."
    dir="$(pwd -P)"
    for other in compose.yaml compose.yml docker-compose.yaml; do
        if [ -f "$other" ]; then fail "$dir holds another Compose project ($other)." "Give Gitberth a folder of its own with GITBERTH_DIR."; fi
    done
    if [ -f .env ]; then
        for key in COMPOSE_PROJECT_NAME COMPOSE_FILE COMPOSE_PROFILES; do
            if [ -n "$(env_get .env "$key")" ]; then
                fail "$dir/.env sets $key." "That changes which containers Compose manages. Take the line out, then run this again."
            fi
        done
    fi
    umask 077
    # The compose file: kept as it is, or the current one when there's none or it's an untouched published copy.
    composefile="$dir/docker-compose.yml"
    new_compose=no
    if [ ! -f docker-compose.yml ] || [ "$(sha256sum docker-compose.yml | awk '{print $1}')" = "$OLD_COMPOSE" ]; then
        fetch "$RAW/docker-compose.yml" > docker-compose.yml.new 2> /dev/null || fail "Gitberth's compose file couldn't be downloaded from GitHub."
        chmod 644 docker-compose.yml.new
        composefile="$dir/docker-compose.yml.new"
        new_compose=yes
    fi
    # The settings: the .env there is kept, with any setting given here written into it; a new one is made otherwise.
    envfile="$dir/.env.new"
    : > "$envfile"
    data=no
    if has_data; then data=yes; fi

    if [ -n "$want_port" ]; then
        case "$want_port" in 0* | *[!0-9]*) fail "GITBERTH_PORT has to be a port number from 1 to 65535, like 8090." ;; esac
        [ "$want_port" -le 65535 ] || fail "GITBERTH_PORT has to be a port number from 1 to 65535, like 8090."
    fi
    if [ -n "$want_image" ]; then
        case "$want_image" in *[!A-Za-z0-9._/:@-]* | [!A-Za-z0-9]*) fail "GITBERTH_IMAGE isn't an image name: $want_image" ;; esac
    fi
    if [ -n "$want_host" ]; then
        case "$want_host" in *[!A-Za-z0-9.:-]*) fail "GITBERTH_PUBLIC_HOST has to be an address or a name, like 192.168.1.50." ;; esac
    fi
    if [ -n "$want_tz" ]; then
        case "$want_tz" in /* | :* | *[!A-Za-z0-9/_+-]*) fail "GITBERTH_TZ has to be a time zone name, like Europe/London." ;; esac
        if [ -d /usr/share/zoneinfo ] && [ ! -f "/usr/share/zoneinfo/$want_tz" ]; then fail "GITBERTH_TZ: there's no time zone called $want_tz."; fi
    fi
    if [ -n "$want_password" ]; then
        case "$want_password" in
            *"'"* | *\\* | " "* | *" " | "	"* | *"	")
                fail "GITBERTH_ADMIN_PASSWORD can't have a single quote ('), a backslash, or spaces at either end." ;;
        esac
        [ "${#want_password}" -ge 10 ] || fail "GITBERTH_ADMIN_PASSWORD needs at least 10 characters."
        nl='
'
        case "$want_password" in *"$nl"*) fail "GITBERTH_ADMIN_PASSWORD can't span lines." ;; esac
    fi
    if [ -n "$want_port" ] || [ -n "$want_image" ]; then
        if [ "$new_compose" = no ] && ! grep -q 'GITBERTH_PORT\|GITBERTH_IMAGE' docker-compose.yml; then
            fail "$dir/docker-compose.yml sets the port and image itself, so GITBERTH_PORT and GITBERTH_IMAGE can't." \
                "Change its lines to  image: \${GITBERTH_IMAGE:-ghcr.io/johnhowelljr/gitberth:latest}  and  - \"\${GITBERTH_PORT:-8080}:8080\"" \
                "or edit them to what you want, then run this again without those settings."
        fi
    fi

    saved=""; made_env=""
    if [ -f .env ]; then
        cat .env > "$envfile"
        for pair in "GITBERTH_PORT=$want_port" "GITBERTH_IMAGE=$want_image" "GITBERTH_PUBLIC_HOST=$want_host" "TZ=$want_tz"; do
            key="${pair%%=*}"; value="${pair#*=}"
            if [ -n "$value" ] && [ "$(env_get "$envfile" "$key")" != "$value" ]; then
                env_set "$envfile" "$key" "$value"
                saved="$saved $key=$value"
            fi
        done
        if [ -n "$want_password" ]; then
            if [ "$data" = yes ]; then
                warn "GITBERTH_ADMIN_PASSWORD only counts on Gitberth's first start, and this one has its data already, so its password stays."
                note "Forgot it? $DOCS/guides/troubleshooting.md#forgot-the-admin-password"
            else
                env_set "$envfile" GITBERTH_ADMIN_PASSWORD "$want_password"
            fi
        fi
    else
        # A password only for a Gitberth that starts afresh: one that has data, or takes it over from the container run by
        # hand, keeps the password it has.
        password=""
        if [ "$data" = no ] && [ -z "$stray" ]; then
            password="${want_password:-$(LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 20)}"
        elif [ -n "$want_password" ]; then
            warn "GITBERTH_ADMIN_PASSWORD only counts on Gitberth's first start, and Gitberth has its data already, so its password stays."
        fi
        host="$want_host"
        if [ -z "$host" ]; then host="$(detect_host)"; fi
        tz="$want_tz"
        if [ -z "$tz" ] && command -v timedatectl > /dev/null 2>&1; then tz="$(timedatectl show -p Timezone --value 2> /dev/null || true)"; fi
        if [ -z "$tz" ] && [ -f /etc/timezone ]; then tz="$(head -n 1 /etc/timezone)"; fi
        if [ -z "$tz" ] && [ -L /etc/localtime ]; then tz="$(readlink /etc/localtime | sed 's|.*/zoneinfo/||')"; fi
        case "$tz" in '' | /* | :* | *[!A-Za-z0-9/_+-]*) tz=UTC ;; esac
        {
            printf '# Gitberth'"'"'s settings, made by its installer on %s. See .env.example in its repository.\n' "$(date -u +%Y-%m-%d)"
            if [ -n "$password" ]; then
                printf '# The admin password counts on the first start only: change it in Gitberth (Settings, Admin password) after that.\n'
            else
                printf '# Gitberth had its data already when this file was made, so it keeps the password it has.\n'
            fi
            printf "GITBERTH_ADMIN_PASSWORD='%s'\n" "$password"
            printf "GITBERTH_PUBLIC_HOST='%s'\n" "$host"
            printf "TZ='%s'\n" "$tz"
            [ -n "$want_port" ] && printf "GITBERTH_PORT='%s'\n" "$want_port"
            [ -n "$want_image" ] && printf "GITBERTH_IMAGE='%s'\n" "$want_image"
            :
        } > "$envfile"
        made_env="LAN address ${host:-not found}, time zone $tz"
    fi

    # What Compose makes of the files, as it'll run them: their mistakes show here, before anything changes.
    if ! services="$(dc config --services 2>&1)"; then
        fail "Docker Compose can't read $dir/docker-compose.yml with its settings." "$services"
    fi
    printf '%s\n' "$services" | grep -qx gitberth || fail "$dir/docker-compose.yml has no gitberth service: it isn't Gitberth's." \
        "Give Gitberth a folder of its own with GITBERTH_DIR."
    image="$(dc config --images 2> /dev/null | head -n 1)"
    [ -n "$image" ] || image="ghcr.io/johnhowelljr/gitberth:latest"
    port="$(dc config --format json gitberth 2> /dev/null | awk '
        /"target": *8080[,}]?[ \t]*$/ { t = 1 }
        t && /"published"/ { gsub(/[^0-9]/, ""); print; exit }
        /}/ { t = 0 }')"
    [ -n "$port" ] || port="${want_port:-8080}"

    # ---- Checks: the port, the image, and whether this machine can run containers at all ----
    holder="$(port_holder "$port" "$existing" "$stray")"
    if [ -n "$holder" ]; then
        fail "port $port is taken by the container $holder." "Run this again with another port:" "    $(again "GITBERTH_PORT=$(free_port)")"
    fi
    held_by_gitberth=no
    for id in $existing $stray; do
        if docker port "$id" 8080/tcp 2> /dev/null | grep -q ":$port\$"; then held_by_gitberth=yes; fi
    done
    if [ "$held_by_gitberth" = no ] && port_in_use_on_host "$port"; then
        fail "port $port is already in use on this machine." "Run this again with another port:" "    $(again "GITBERTH_PORT=$(free_port)")"
    fi

    say "Pulling $image"
    if ! out="$(docker pull -q "$image" 2>&1)"; then
        if docker image inspect "$image" > /dev/null 2>&1; then
            warn "The image couldn't be pulled, so the copy already on this machine is used:"
            note "$(printf '%s' "$out" | tail -n 1)"
        else
            fail "the image $image couldn't be pulled." "$out" "Check that this machine reaches ghcr.io:  curl -sI https://ghcr.io"
        fi
    fi
    if ! out="$(docker run --rm --network none --entrypoint true "$image" 2>&1)"; then
        case "$out" in
            *ip_unprivileged_port_start*)
                fail "Docker can't start containers in this LXC: the Proxmox host needs an update." "$out" "" \
                    "This is the late-2025 runc change that older Proxmox LXC packages don't allow. On the Proxmox host:" \
                    "    apt update && apt full-upgrade      (lxc-pve 6.0.5-2 or later, Proxmox VE 9.1+ or 8.4.16+)" \
                    "then restart this LXC and run the installer again. Nesting isn't the problem." ;;
        esac
        if [ "$virt" = lxc ]; then
            fail "Docker can't start containers in this LXC." "$out" "" \
                "Docker needs nesting and keyctl turned on for the LXC. On the Proxmox host, run (with your LXC's ID):" \
                "    pct set <id> --features nesting=1,keyctl=1" \
                "then restart the LXC and run this installer again. If they're on already, update the Proxmox host" \
                "(apt update && apt full-upgrade) and restart the LXC."
        fi
        fail "Docker can't start containers on this machine." "$out"
    fi
    if [ "$start" = no ] && [ -n "$existing" ]; then
        fail "GITBERTH_START=no is for setting up a new machine, and Gitberth is here already." "Run this again without it to update it."
    fi
    # The time zone, against the ones Gitberth's image knows: one given has to be right; a detected one falls back to UTC.
    tz_now="$(env_get "$envfile" TZ)"
    if [ -n "$tz_now" ] && ! docker run --rm --network none --entrypoint test "$image" -f "/usr/share/zoneinfo/$tz_now" > /dev/null 2>&1; then
        if [ -n "$want_tz" ]; then fail "GITBERTH_TZ: there's no time zone called $want_tz."; fi
        if [ -n "$made_env" ]; then
            env_set "$envfile" TZ UTC
            made_env="LAN address ${host:-not found}, time zone UTC (this machine's, $tz_now, isn't one Gitberth knows)"
        fi
    fi

    # ---- Every check passed: the changes ----
    if [ "$new_compose" = yes ]; then
        if [ -f docker-compose.yml ]; then note "Updating docker-compose.yml to the current one (it takes the port and image from .env)"; fi
        mv docker-compose.yml.new docker-compose.yml
        composefile="$dir/docker-compose.yml"
    fi
    if [ -f .env ] && cmp -s .env "$envfile"; then rm -f "$envfile"
    else
        if [ -f .env ]; then cat "$envfile" > .env; rm -f "$envfile"; else mv "$envfile" .env; fi
    fi
    envfile="$dir/.env"
    if [ -n "$saved" ]; then note "Saved in .env:$saved"; fi
    if [ -n "$made_env" ]; then note "Made $dir/.env: $made_env"; fi

    # A Gitberth run by hand gives way: stopped, renamed, kept from starting again, and its data copied to the new one.
    if [ -n "$stray" ]; then
        stray_policy="$(docker container inspect -f '{{.HostConfig.RestartPolicy.Name}}' "$stray")"
        stray_aside="gitberth-before-install-$(date +%Y%m%d%H%M%S)"
        docker stop gitberth > /dev/null 2>&1 || true
        docker rename gitberth "$stray_aside"
        docker update --restart no "$stray_aside" > /dev/null
        note "The gitberth container that was here is stopped and renamed $stray_aside."
        if [ "$data" = no ]; then
            dc create gitberth > /dev/null 2>&1 || fail "Gitberth's container couldn't be created."
            docker run --rm --volumes-from "$stray_aside:ro" -v "$VOLUME:/to" --entrypoint sh "$image" -c 'cp -a /data/. /to/' \
                || fail "the old container's data couldn't be copied over."
            data=yes
            note "Its data was copied over, so Gitberth carries on with its settings, apps and password."
        fi
    fi

    # An update: a copy of Gitberth's data first, in case the new version is one to go back from.
    backup=""
    if [ -n "$existing" ] && [ "$data" = yes ]; then
        running_image="$(docker container inspect -f '{{.Image}}' "$existing" 2> /dev/null || true)"
        new_image="$(docker image inspect -f '{{.Id}}' "$image" 2> /dev/null || true)"
        if [ -n "$new_image" ] && [ "$running_image" != "$new_image" ]; then
            say "Backing up Gitberth's data"
            mkdir -p backups
            backup="backups/gitberth-data-$(date +%Y%m%d-%H%M%S).tgz"
            dc stop gitberth > /dev/null 2>&1 || true
            docker run --rm -v "$VOLUME:/data:ro" -v "$dir/backups:/backup" --entrypoint sh "$image" \
                -c "umask 077 && tar czf /backup/${backup#backups/} -C /data ." \
                || { dc start gitberth > /dev/null 2>&1 || true; fail "Gitberth's data couldn't be backed up, so it wasn't updated."; }
            # The five newest are kept. The names are the installer's own, so ls is safe here.
            # shellcheck disable=SC2012
            ls -1t backups/gitberth-data-*.tgz 2> /dev/null | tail -n +6 | while read -r old; do rm -f "$old"; done
            note "$dir/$backup"
        fi
    fi

    first=yes
    if [ "$data" = yes ]; then first=no; fi
    if [ "$start" = no ]; then
        dc create gitberth > /dev/null 2>&1 || fail "Gitberth's container couldn't be created."
        printf '\n%sGitberth is set up, not started%s (GITBERTH_START=no).\n' "$G" "$N"
        printf '  Its data volume is %s. Start it with:  cd %s && docker compose up -d\n\n' "$VOLUME" "$dir"
        return 0
    fi

    # ---- Start it, and wait until its page answers ----
    say "Starting Gitberth"
    if ! out="$(dc up -d gitberth 2>&1)"; then
        case "$out" in
            *"address already in use"* | *"port is already allocated"*)
                fail "port $port is already in use." "Run this again with another port:" "    $(again "GITBERTH_PORT=$(free_port)")" ;;
        esac
        fail "Docker Compose couldn't start it." "$out"
    fi
    container="$(docker ps -aq --filter label=com.docker.compose.project=gitberth --filter label=com.docker.compose.service=gitberth --no-trunc | head -n 1)"
    bound="$(dc port gitberth 8080 2> /dev/null | head -n 1)"
    probe_host="${bound%:*}"
    case "$probe_host" in '' | 0.0.0.0 | '[::]' | '::') probe_host=127.0.0.1 ;; esac
    if [ -n "$bound" ] && [ -n "${bound##*:}" ]; then port="${bound##*:}"; fi
    i=0
    until probe "http://$probe_host:$port/login"; do
        i=$((i + 1))
        state="$(docker container inspect -f '{{.State.Status}}' "$container" 2> /dev/null || echo missing)"
        if [ "$i" -ge 90 ] || [ "$state" = exited ] || [ "$state" = dead ] || [ "$state" = missing ]; then
            printf '\n' >&2
            dc ps -a >&2 || true
            printf '\n%sIts log:%s\n' "$B" "$N" >&2
            dc logs --tail 60 gitberth >&2 || true
            fail "Gitberth started, but its page didn't answer on port $port (the container is $state)." \
                "Its log is above. If it's a problem with this machine, fix it and run this again."
        fi
        sleep 2
    done
    if docker logs "$container" 2>&1 | grep -q "Docker isn't reachable"; then
        fail "Gitberth started, but it can't reach Docker through /var/run/docker.sock." \
            "Check that the socket is there (ls -l /var/run/docker.sock) and that docker-compose.yml still mounts it."
    fi
    stray_aside_done="$stray_aside"; stray_aside=""

    # ---- Done ----
    shown_version="$(docker image inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$image" 2> /dev/null || true)"
    host="$(container_env "$container" GITBERTH_PUBLIC_HOST)"
    [ -n "$host" ] || host="$(detect_host)"
    [ "$probe_host" = 127.0.0.1 ] || host="$probe_host"
    case "$host" in *:*) url_host="[$host]" ;; *) url_host="$host" ;; esac
    # Up to date: the same container, running before as now. Updated: a running one replaced. Running: started here.
    what="is running"
    if [ "$existing_running" = yes ]; then
        case "$container" in "$existing"*) what="was already up to date" ;; *) what="is updated" ;; esac
    fi
    case "$shown_version" in [0-9]*) ;; *) shown_version="" ;; esac
    printf '\n%sGitberth%s %s.%s\n\n' "$G" "${shown_version:+ $shown_version}" "$what" "$N"
    printf '  Open      http://%s:%s\n' "${url_host:-<this machine>}" "$port"
    printf '  Sign in   admin\n'
    if [ "$first" = yes ]; then
        password="$(container_env "$container" GITBERTH_ADMIN_PASSWORD | sed 's/^[ \t]*//; s/[ \t]*$//')"
        if [ -z "$password" ]; then
            password="$(docker logs "$container" 2>&1 | sed -n 's/.*with this generated password: \([^ ]*\) (change.*/\1/p' | tail -n 1)"
        fi
        printf '  Password  %s\n' "${password:-see  docker compose logs gitberth}"
        printf '            (change it in Settings once you'"'"'re in; it'"'"'s in %s/.env until then)\n' "$dir"
    else
        printf '  Password  your existing one (Gitberth had its data already)\n'
        printf '            Forgot it? %s/guides/troubleshooting.md#forgot-the-admin-password\n' "$DOCS"
    fi
    if [ -n "$host" ] && [ "$probe_host" = 127.0.0.1 ] && ! probe "http://$url_host:$port/login"; then
        printf '\n'
        warn "This machine couldn't open http://$url_host:$port itself. If other devices can't either, set this machine's"
        warn "LAN address with GITBERTH_PUBLIC_HOST, or check its firewall."
    fi
    if [ -n "$backup" ]; then printf '\n  Gitberth'"'"'s data from before the update: %s/%s\n' "$dir" "$backup"; fi
    if [ -n "$stray_aside_done" ]; then
        printf '\n  The old gitberth container is kept, stopped, as %s.\n' "$stray_aside_done"
        printf '  Remove it, with its data, once you'"'"'re happy:  docker rm -v %s\n' "$stray_aside_done"
    fi
    printf '\n  To update it later, run this installer again.\n'
    printf '  Docs: %s/README.md\n\n' "$DOCS"
}

main "$@" < /dev/null
