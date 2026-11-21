# Listenarr Monorepo Dockerfile
# Builds both backend (.NET API) and frontend (Vue.js) into a single container

# Build gosu with a modern Go toolchain to avoid golang/stdlib CVEs present in
# the Debian-packaged version (compiled with Go 1.19.x). Use Go 1.26 (current
# stable) to pick up all 2026 stdlib security patches.
FROM golang:1.26.2-alpine AS gosu-builder
ARG GOSU_VERSION=1.19
RUN CGO_ENABLED=0 go install github.com/tianon/gosu@${GOSU_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 4545
ENV ASPNETCORE_URLS=http://*:4545
ENV DOCKER_ENV=true

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Ensure Node.js is available in the build image so MSBuild targets that run
# the frontend (npm/vite) can execute during `dotnet publish`.
# Use the same Node.js major as CI and the Discord bot runtime. Install it before COPY . . so a
# source-only change does not repeat the apt and NodeSource work.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_26.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& node --version \
	&& npm --version \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["listenarr.api/Listenarr.Api.csproj", "listenarr.api/"]
COPY ["listenarr.domain/Listenarr.Domain.csproj", "listenarr.domain/"]
COPY ["listenarr.application/Listenarr.Application.csproj", "listenarr.application/"]
COPY ["listenarr.infrastructure/Listenarr.Infrastructure.csproj", "listenarr.infrastructure/"]
RUN dotnet restore "listenarr.api/Listenarr.Api.csproj"
COPY . .
WORKDIR "/src/listenarr.api"
RUN dotnet build "Listenarr.Api.csproj" -c Release -o /app/build \
	&& dotnet publish "Listenarr.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false
# Trim and set the mode of the packaged ffprobe here, so the final image's COPY carries only
# this platform's binaries, already executable, and no later layer has to copy them again.
RUN sh /src/docker/runtime/prepare-ffprobe.sh /app/publish

FROM base AS final
WORKDIR /app
COPY docker/runtime/ /tmp/listenarr-runtime/

# Use the gosu binary built above instead of the apt package.
COPY --from=gosu-builder /go/bin/gosu /usr/local/bin/gosu
RUN chmod +x /usr/local/bin/gosu

RUN sh /tmp/listenarr-runtime/create-listenarr-user.sh

# Install Node.js and the Discord bot's npm dependencies from a directory holding only the lockfile,
# before the published app is copied in. A source-only change invalidates the publish COPY below but
# leaves this (much slower) install cached. The published app still supplies the bot's source files.
COPY ["tools/discord-bot/package.json", "tools/discord-bot/package-lock.json", "/opt/discord-bot/"]
RUN sh /tmp/listenarr-runtime/install-discord-bot-runtime.sh /opt/discord-bot

COPY --from=build /app/publish .
RUN test -f /app/tools/discord-bot/package-lock.json \
	&& mkdir -p /app/tools/discord-bot/node_modules \
	&& cp -a /opt/discord-bot/node_modules/. /app/tools/discord-bot/node_modules/ \
	&& rm -rf /opt/discord-bot

RUN sh /tmp/listenarr-runtime/finalize-app.sh

# Copy entrypoint script for PUID/PGID/UMASK support
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN sh /tmp/listenarr-runtime/prepare-entrypoint.sh \
	&& rm -rf /tmp/listenarr-runtime

ENTRYPOINT ["/docker-entrypoint.sh"]
