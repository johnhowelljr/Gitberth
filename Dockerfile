# The build stage runs on the builder's own platform: the output is plain .NET, the same for Intel and ARM, so an ARM
# image doesn't have to compile under emulation. Only the final stage is per platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore with the full source present: .NET 10 only restores the package that ships blazor.web.js
# when it can see the .razor files, so a csproj-only restore step produces a UI that never starts.
COPY src/Gitberth/ src/Gitberth/
# The docs, which the build embeds for the built-in Help (/help works without internet).
COPY docs/ docs/
# Set by the release workflow: the tag it builds (v1.2.3, which overrides Version in the csproj) and the commit, which
# Settings › System shows. A build from a folder leaves both empty.
ARG GITBERTH_VERSION=
ARG GITBERTH_COMMIT=
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    version="${GITBERTH_VERSION#v}" \
    && case "$version" in [0-9]*.[0-9]*.[0-9]*) ;; *) version= ;; esac \
    && dotnet publish src/Gitberth/Gitberth.csproj -c Release -o /app -p:UseAppHost=false \
       ${version:+-p:Version=$version} ${GITBERTH_COMMIT:+-p:SourceRevisionId=$GITBERTH_COMMIT} \
    && test -f /app/Gitberth.dll \
    && test -f /app/wwwroot/_framework/blazor.web.js \
    && test -f /app/wwwroot/_content/MudBlazor/MudBlazor.min.css

# Static docker CLI (official image) + the buildx plugin (installed the way the buildx docs describe).
# Gitberth drives the host's Docker Engine through the mounted socket; the CLI negotiates the API version.
# Pinned to Docker 29's CLI: `network connect --gw-priority` needs a CLI from Docker 28 on.
FROM docker:29-cli AS docker-cli

# Railpack (Railway's builder, MIT), for the projects Gitberth doesn't build itself: its CLI plans a build (railpack
# prepare). It's the binary of Railpack's own frontend image, pinned by digest, so the plan and the frontend that builds
# it (RailpackRelease.Frontend) always come from the same release.
FROM ghcr.io/railwayapp/railpack-frontend:v0.43.0@sha256:355ef61fae6f2551103cd8b10c710cb07167dd353d561f15c69b8d7ac1c676cc AS railpack

# mise, which railpack prepare runs to look up tool versions: the release Railpack 0.43.0 asks for (core/mise/version.txt),
# checked against mise's SHASUMS256.txt, and put where prepare looks for it, so prepare never downloads it.
FROM scratch AS mise-amd64
ADD --checksum=sha256:0f95d528ad0b8c54f7022be2ff22a36be149123ff9b27bdaa216d349a9f59c17 \
    https://github.com/jdx/mise/releases/download/v2026.10.14/mise-v2026.10.14-linux-x64-musl.tar.gz /mise.tar.gz
FROM scratch AS mise-arm64
ADD --checksum=sha256:8f7fa64c3d08ba515aac501f18f6f43c00c381793185380fd1a92c411c00cdde \
    https://github.com/jdx/mise/releases/download/v2026.10.14/mise-v2026.10.14-linux-arm64-musl.tar.gz /mise.tar.gz
FROM mise-${TARGETARCH} AS mise

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
# Pinned (Docker Hub's tag has no v), by its index's digest too: Railpack's builds go through its remote driver and --load.
COPY --from=docker/buildx-bin:0.39.1@sha256:65035f720fc63f266620394a62edcde2318b3611ceb50b77af984578d011a0d6 /buildx /usr/libexec/docker/cli-plugins/docker-buildx
# The Compose plugin, pinned: Gitberth deploys apps that come as a Compose file (a repo's compose.yaml, a pasted one)
# with `docker compose` itself, so they don't depend on what the host has installed.
COPY --from=docker/compose-bin:v5.7.1 /docker-compose /usr/libexec/docker/cli-plugins/docker-compose
# Railpack's CLI and its mise (RailpackRelease). CI=1 keeps mise from asking its site for a newer mise.
COPY --from=railpack /railpack /usr/local/bin/railpack
RUN --mount=type=bind,from=mise,target=/mnt/mise \
    mkdir -p /usr/local/lib/railpack \
    && tar -xzf /mnt/mise/mise.tar.gz -C /tmp mise/bin/mise \
    && mv /tmp/mise/bin/mise /usr/local/lib/railpack/mise && rm -rf /tmp/mise \
    && CI=1 /usr/local/lib/railpack/mise --version | grep -qF 2026.10.14 \
    && railpack --version | grep -qF 0.43.0
WORKDIR /app
COPY --from=build /app .
# The license travels with every copy of the image, forks' included, and so do the notices of what it ships of others'.
COPY LICENSE.md THIRD-PARTY-NOTICES.md ./
LABEL org.opencontainers.image.licenses="PolyForm-Noncommercial-1.0.0"
ENV GITBERTH_DATA=/data \
    ASPNETCORE_HTTP_PORTS=8080
VOLUME /data
EXPOSE 8080
# Runs as root: it needs the Docker socket, which is root-equivalent on the host anyway.
ENTRYPOINT ["dotnet", "Gitberth.dll"]
