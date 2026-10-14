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
COPY docker/runtime/ /tmp/listenarr-runtime/

# Use the gosu binary built above instead of the apt package.
COPY --from=gosu-builder /go/bin/gosu /usr/local/bin/gosu
RUN chmod +x /usr/local/bin/gosu

RUN sh /tmp/listenarr-runtime/create-listenarr-user.sh

COPY --from=build /app/publish .

# Install Node.js only for the Discord bot runtime. npm is used for the install
# and then removed from the final filesystem; the bot only needs node.
RUN sh /tmp/listenarr-runtime/install-discord-bot-runtime.sh

RUN sh /tmp/listenarr-runtime/finalize-app.sh

# Copy entrypoint script for PUID/PGID/UMASK support
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN sh /tmp/listenarr-runtime/prepare-entrypoint.sh \
	&& rm -rf /tmp/listenarr-runtime

# Report container health from the application's own readiness probe. The runtime
# image ships no HTTP client - curl and gnupg are purged after the Discord bot
# install and wget was never present - so the request is issued with the node
# binary that the bot runtime already puts in the image.
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 \
	CMD ["node", "-e", "require('http').get('http://127.0.0.1:4545/api/v1/system/ready', res => process.exit(res.statusCode === 200 ? 0 : 1)).on('error', () => process.exit(1))"]

ENTRYPOINT ["/docker-entrypoint.sh"]
