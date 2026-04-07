# Listenarr Monorepo Dockerfile
# Builds both backend (.NET API) and frontend (Vue.js) into a single container

# Build gosu with a modern Go toolchain to avoid golang/stdlib CVEs present in
# the Debian-packaged version (compiled with Go 1.19.x). Use Go 1.26 (current
# stable) to pick up all 2026 stdlib security patches.
FROM golang:1.26-alpine AS gosu-builder
ARG GOSU_VERSION=1.19
RUN CGO_ENABLED=0 go install github.com/tianon/gosu@${GOSU_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 4545

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["listenarr.api/Listenarr.Api.csproj", "listenarr.api/"]
RUN dotnet restore "listenarr.api/Listenarr.Api.csproj"
COPY . .
WORKDIR "/src/listenarr.api"
# Ensure Node.js is available in the build image so MSBuild targets that run
# the frontend (npm/vite) can execute during `dotnet publish`.
# Use NodeSource to install Node 24 (Active LTS as of 2026; Node 20/22 are EOL).
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& node --version \
	&& npm --version \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*
RUN dotnet build "Listenarr.Api.csproj" -c Release -o /app/build \
	&& dotnet publish "Listenarr.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
# Install Node.js in the runtime image for Discord bot support.
# After installing Node.js, upgrade npm to its latest release and remove the
# apt-installed npm tree — scanners flag tar/minimatch/cross-spawn inside
# /usr/lib/node_modules/npm/node_modules which belong to the bundled npm that
# ships with the NodeSource package.  The upgraded npm lives in /usr/local and
# is the only copy left after the cleanup.  After upgrading npm, overwrite its
# bundled picomatch (4.0.3, CVE-2026-33671/33672) with the patched 4.0.4.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& npm install -g npm@11.12.1 --prefix /usr/local \
	&& rm -rf /usr/lib/node_modules/npm \
	&& rm -f /usr/bin/npm /usr/bin/npx \
	&& npm install --prefix /usr/local/lib/node_modules/npm --no-save --no-package-lock picomatch@4.0.4 \
	&& node --version \
	&& npm --version \
	&& rm -rf /var/lib/apt/lists/*

# Use the gosu binary built above instead of the apt package.
COPY --from=gosu-builder /go/bin/gosu /usr/local/bin/gosu
RUN chmod +x /usr/local/bin/gosu

RUN groupadd --system listenarr \
	&& useradd --system --gid listenarr --home-dir /nonexistent --shell /usr/sbin/nologin --no-create-home listenarr

COPY --from=build /app/publish .

# Ensure config directory exists
RUN mkdir -p /app/config/database

# Copy entrypoint script for PUID/PGID/UMASK support
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

ENTRYPOINT ["/docker-entrypoint.sh"]
