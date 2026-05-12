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

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["listenarr.api/Listenarr.Api.csproj", "listenarr.api/"]
COPY ["listenarr.domain/Listenarr.Domain.csproj", "listenarr.domain/"]
COPY ["listenarr.application/Listenarr.Application.csproj", "listenarr.application/"]
COPY ["listenarr.infrastructure/Listenarr.Infrastructure.csproj", "listenarr.infrastructure/"]
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
# Upgrade npm, remove the apt-installed npm tree, then patch vulnerable
# transitive deps bundled inside npm 11: node-gyp → tinyglobby → picomatch@4.0.3
# (CVE-2026-33671), brace-expansion 2.x and 5.x (CVE-2026-33750).
# npm pack downloads the fixed tarball; we extract it over each vulnerable copy
# in npm's node_modules tree and clean the download cache afterwards.
# libcap2 (CVE-2026-4878): upgraded via apt to pull the patched Ubuntu security release.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg libcap2 \
	&& curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& npm install -g npm@11.12.1 --prefix /usr/local \
	&& rm -rf /usr/lib/node_modules/npm \
	&& rm -f /usr/bin/npm /usr/bin/npx \
	&& /usr/local/bin/npm pack picomatch@4.0.4 --pack-destination /tmp \
	&& find /usr/local/lib/node_modules/npm/node_modules -type d -name "picomatch" \
	       -exec sh -c 'tar xzf /tmp/picomatch-4.0.4.tgz -C "$1" --strip-components=1' _ {} \; \
	&& /usr/local/bin/npm pack brace-expansion@2.0.3 --pack-destination /tmp \
	&& find /usr/local/lib/node_modules/npm/node_modules -type d -name "brace-expansion" \
	       -exec sh -c 'ver=$(node -e "process.stdout.write(require('"'"'$1/package.json'"'"').version)" 2>/dev/null); [ "${ver%%.*}" = "2" ] && tar xzf /tmp/brace-expansion-2.0.3.tgz -C "$1" --strip-components=1 || true' _ {} \; \
	&& /usr/local/bin/npm pack brace-expansion@5.0.5 --pack-destination /tmp \
	&& find /usr/local/lib/node_modules/npm/node_modules -type d -name "brace-expansion" \
	       -exec sh -c 'ver=$(node -e "process.stdout.write(require('"'"'$1/package.json'"'"').version)" 2>/dev/null); [ "${ver%%.*}" = "5" ] && tar xzf /tmp/brace-expansion-5.0.5.tgz -C "$1" --strip-components=1 || true' _ {} \; \
	&& rm -f /tmp/picomatch-4.0.4.tgz /tmp/brace-expansion-2.0.3.tgz /tmp/brace-expansion-5.0.5.tgz \
	&& rm -rf /root/.npm \
	&& node --version \
	&& /usr/local/bin/npm --version \
	&& rm -rf /var/lib/apt/lists/*

# Use the gosu binary built above instead of the apt package.
COPY --from=gosu-builder /go/bin/gosu /usr/local/bin/gosu
RUN chmod +x /usr/local/bin/gosu

RUN groupadd --system listenarr \
	&& useradd --system --gid listenarr --home-dir /nonexistent --shell /usr/sbin/nologin --no-create-home listenarr

COPY --from=build /app/publish .

# Normalize frontend asset permissions so the non-root runtime user can read static files.
RUN find /app/wwwroot -type d -exec chmod 755 {} \; \
	&& find /app/wwwroot -type f -exec chmod 644 {} \;

# Ensure config directory exists
RUN mkdir -p /app/config/database

# Copy entrypoint script for PUID/PGID/UMASK support
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN sed -i 's/\r$//' /docker-entrypoint.sh \
	&& chmod +x /docker-entrypoint.sh

ENTRYPOINT ["/docker-entrypoint.sh"]
