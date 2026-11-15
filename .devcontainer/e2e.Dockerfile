# Dedicated full-stack E2E host, built and started by scripts/e2e/run-e2e.mjs:
#
#   docker build --tag listenarr-e2e-app .
#   docker build --tag listenarr-e2e --build-arg LISTENARR_IMAGE=listenarr-e2e-app \
#     --file .devcontainer/e2e.Dockerfile .
ARG LISTENARR_IMAGE

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test-host-build
WORKDIR /src
COPY . .
RUN dotnet publish tests/Listenarr.TestHost/Listenarr.TestHost.csproj \
	--configuration Release \
	--output /out/testhost \
	/p:UseAppHost=false

FROM ${LISTENARR_IMAGE}

COPY --from=test-host-build /out/testhost/ /app/testhost/

# ffprobe reads the tags of the generated audiobook the write suite imports.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends ffmpeg \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*

# No background work that would change the seeded database between tests,
# except the scan the import test waits for. Files written to the mounted
# directories stay removable by the user running the suite.
ENV UMASK=000 \
	LISTENARR_DISABLE_HOSTED_SERVICES=true \
	LISTENARR_CONTENT_ROOT=/app \
	LISTENARR_ENABLE_UNMATCHED_SCAN_WORKER=true \
	LISTENARR_LOG_LEVEL=Warning

# Listenarr looks for ffprobe in its config directory, which is mounted at run
# time and restored before each write test: put it there on every start.
ENTRYPOINT ["/bin/sh", "-c", "mkdir -p /app/config/ffmpeg && cp /usr/bin/ffprobe /app/config/ffmpeg/ffprobe && chmod 755 /app/config/ffmpeg/ffprobe && exec /docker-entrypoint.sh dotnet /app/testhost/Listenarr.TestHost.dll"]
