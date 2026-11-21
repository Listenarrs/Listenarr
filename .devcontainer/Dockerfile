# Listenarr development environment: .NET SDK, Node.js, and what the Playwright
# suites need on the machine that runs them. Docker itself comes from the
# docker-in-docker feature declared in devcontainer.json.
#
# The SDK comes from the image the production Dockerfile builds with: the
# devcontainers .NET image lags behind the SDK global.json asks for.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-sdk

FROM mcr.microsoft.com/devcontainers/base:noble

COPY --from=dotnet-sdk /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet \
	DOTNET_NOLOGO=true \
	DOTNET_CLI_TELEMETRY_OPTOUT=true

# Same Node.js major as the production Dockerfile and CI (.node-version).
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_26.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& node --version \
	&& npm --version \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*

# - ffmpeg (ffprobe) for tests/Listenarr.BackendIntegrationTests, and git
# - the browser libraries: headless Playwright Chromium frontend integration tests
RUN apt-get update \
	&& apt-get install -y --no-install-recommends \
		ffmpeg \
		git \
		libasound2t64 \
		libgbm1 \
		libgtk-3-0t64 \
		libnotify4 \
		libnss3 \
		libxss1 \
		libxtst6 \
		xauth \
		xvfb \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*
