# Dedicated test host for the browser E2E suite, which runs Listenarr in Docker, built and started by
# scripts/lib/listenarr-stack.mjs:
#
#   docker build --tag listenarr-e2e-app .
#   docker build --tag listenarr-e2e-host --build-arg LISTENARR_IMAGE=listenarr-e2e-app \
#     --file scripts/e2e/e2e-host.Dockerfile .
#
# It runs tests/Listenarr.FakeProviders (the API with fixture-backed metadata providers) instead of the
# production entry point. With LISTENARR_COVERAGE_DIR set, the host runs under dotnet-coverage and
# writes one Cobertura report per process lifetime there when it stops.
ARG LISTENARR_IMAGE=listenarr-e2e-app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test-host-build
ARG DOTNET_COVERAGE_VERSION=18.12.0
WORKDIR /src
# The coverage tool and the NuGet restore depend only on the project files. Install and restore
# before COPY . . so a source-only change reuses both layers.
RUN dotnet tool install dotnet-coverage --version "${DOTNET_COVERAGE_VERSION}" --tool-path /out/dotnet-coverage
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["listenarr.api/Listenarr.Api.csproj", "listenarr.api/"]
COPY ["listenarr.domain/Listenarr.Domain.csproj", "listenarr.domain/"]
COPY ["listenarr.application/Listenarr.Application.csproj", "listenarr.application/"]
COPY ["listenarr.infrastructure/Listenarr.Infrastructure.csproj", "listenarr.infrastructure/"]
COPY ["tests/Listenarr.FakeProviders/Listenarr.FakeProviders.csproj", "tests/Listenarr.FakeProviders/"]
RUN dotnet restore "tests/Listenarr.FakeProviders/Listenarr.FakeProviders.csproj"
COPY . .
RUN dotnet publish tests/Listenarr.FakeProviders/Listenarr.FakeProviders.csproj \
	--configuration Release \
	--output /out/integration-host \
	/p:UseAppHost=false \
	--no-restore
RUN sh docker/runtime/prepare-ffprobe.sh /out/integration-host

FROM ${LISTENARR_IMAGE}

# ffprobe reads the tags of the generated audiobooks the suites import; the
# dotnet-coverage profiler needs libxml2. Install before copying the test host output so a change to
# the entrypoint or the coverage filter does not repeat the apt work.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends ffmpeg libxml2 \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*

# Not under a path containing "testhost": Microsoft code coverage excludes those modules by default.
COPY --from=test-host-build /out/integration-host/ /app/integration-host/
COPY --from=test-host-build /out/dotnet-coverage/ /opt/dotnet-coverage/
COPY scripts/e2e/e2e-host-coverage.xml /opt/dotnet-coverage/listenarr.coverage.xml

# The production entry point (user remapping, umask) with the command it starts taken from its
# arguments rather than the production Listenarr.Api.dll.
RUN sed 's#exec \(gosu [^ ]* \)\{0,1\}dotnet Listenarr.Api.dll "\$@"#exec \1"$@"#' /docker-entrypoint.sh > /testhost-entrypoint.sh \
	&& chmod 755 /testhost-entrypoint.sh \
	&& if grep -q 'Listenarr.Api.dll' /testhost-entrypoint.sh; then echo 'testhost-entrypoint.sh still starts Listenarr.Api.dll' >&2; exit 1; fi

# No background work that would change the seeded database between tests,
# except the scan the import tests wait for. Files written to the mounted
# directories stay removable by the user running the suite. dotnet-coverage
# targets an older runtime than the image's.
ENV UMASK=000 \
	LISTENARR_DISABLE_HOSTED_SERVICES=true \
	LISTENARR_CONTENT_ROOT=/app \
	LISTENARR_LOG_LEVEL=Warning \
	DOTNET_ROLL_FORWARD=Major

COPY scripts/e2e/e2e-host-entrypoint.sh /e2e-host-entrypoint.sh
RUN chmod 755 /e2e-host-entrypoint.sh

ENTRYPOINT ["/e2e-host-entrypoint.sh"]
