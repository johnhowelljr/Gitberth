# The build stage runs on the builder's own platform: the output is plain .NET, the same for Intel and ARM, so an ARM
# image doesn't have to compile under emulation. Only the final stage is per platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore with the full source present: .NET 10 only restores the package that ships blazor.web.js
# when it can see the .razor files, so a csproj-only restore step produces a UI that never starts.
COPY src/Gitberth/ src/Gitberth/
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker/buildx-bin /buildx /usr/libexec/docker/cli-plugins/docker-buildx
WORKDIR /app
COPY --from=build /app .
# The license travels with every copy of the image, forks' included.
COPY LICENSE.md .
LABEL org.opencontainers.image.licenses="PolyForm-Noncommercial-1.0.0"
ENV GITBERTH_DATA=/data \
    ASPNETCORE_HTTP_PORTS=8080
VOLUME /data
EXPOSE 8080
# Runs as root: it needs the Docker socket, which is root-equivalent on the host anyway.
ENTRYPOINT ["dotnet", "Gitberth.dll"]
