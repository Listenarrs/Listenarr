# Listenarr Monorepo Dockerfile
# Builds both backend (.NET API) and frontend (Vue.js) into a single container

# Build gosu with a modern Go toolchain to avoid golang/stdlib CVEs present in
# the Debian-packaged version (compiled with Go 1.19.x). Use Go 1.26 (current
# stable) to pick up all 2026 stdlib security patches.
FROM golang:1.26.2-alpine AS gosu-builder
ARG GOSU_VERSION=1.19
RUN CGO_ENABLED=0 go install github.com/tianon/gosu@${GOSU_VERSION}

# Build whisper.cpp from source for local speech-to-text (ADR-0001 audio
# verification). Deliberately NOT the FFmpeg runtime-download pattern: upstream
# publishes no prebuilt Linux binaries (v1.8.6 ships only Windows zips and an
# Apple xcframework), and whisper.cpp is MIT, so baking it in at build time is
# both possible and simpler. Source build is arch-agnostic (amd64/arm64).
# GGML_NATIVE=OFF keeps the binary portable across CPUs of the same arch;
# GGML_OPENMP=OFF avoids a libgomp runtime dep the aspnet base image lacks.
# Instruction-set flags are pinned explicitly (x86-64-v3 baseline: AVX/AVX2/
# FMA/F16C) rather than left to ggml's defaults, so the binary's requirements
# are visible here. A build with these ON dies with SIGILL (exit 132) on hosts
# capped at SSE4.2 (e.g. QEMU's default or x86-64-v2 CPU models) — flip them
# OFF if the image must run on such a host. AVX-512 stays OFF.
FROM debian:trixie-slim AS whisper-builder
ARG WHISPER_CPP_VERSION=v1.8.6
ARG WHISPER_MODEL=base.en
RUN apt-get update \
	&& apt-get install -y --no-install-recommends git build-essential cmake ca-certificates curl \
	&& rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${WHISPER_CPP_VERSION} https://github.com/ggml-org/whisper.cpp /whisper \
	&& cmake -S /whisper -B /whisper/build \
		-DCMAKE_BUILD_TYPE=Release \
		-DBUILD_SHARED_LIBS=OFF \
		-DGGML_NATIVE=OFF \
		-DGGML_OPENMP=OFF \
		-DGGML_AVX=ON \
		-DGGML_AVX2=ON \
		-DGGML_AVX512=OFF \
		-DGGML_AVX_VNNI=OFF \
		-DGGML_FMA=ON \
		-DGGML_F16C=ON \
		-DGGML_BMI2=ON \
		-DWHISPER_BUILD_TESTS=OFF \
		-DWHISPER_BUILD_SERVER=OFF \
	&& cmake --build /whisper/build --config Release -j"$(nproc)" --target whisper-cli \
	&& /whisper/models/download-ggml-model.sh ${WHISPER_MODEL} \
	&& printf '%s\n' \
		"whisper.cpp ${WHISPER_CPP_VERSION} (MIT License) - https://github.com/ggml-org/whisper.cpp" \
		"ggml-${WHISPER_MODEL}.bin model from https://huggingface.co/ggerganov/whisper.cpp (MIT, derived from OpenAI Whisper weights)" \
		"See LICENSE-whisper.txt alongside this notice for the full license text." \
		> /whisper/NOTICE-whisper.txt

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

# Bundled whisper.cpp CLI + model for audio verification (ADR-0001).
# WhisperService resolves these paths via ToolsRootPath (/app/tools/whisper)
# unless overridden with LISTENARR_WHISPER_BIN / LISTENARR_WHISPER_MODEL.
ARG WHISPER_MODEL=base.en
COPY --from=whisper-builder /whisper/build/bin/whisper-cli /app/tools/whisper/whisper-cli
COPY --from=whisper-builder /whisper/models/ggml-${WHISPER_MODEL}.bin /app/tools/whisper/ggml-${WHISPER_MODEL}.bin
COPY --from=whisper-builder /whisper/LICENSE /app/tools/whisper/LICENSE-whisper.txt
COPY --from=whisper-builder /whisper/NOTICE-whisper.txt /app/tools/whisper/NOTICE-whisper.txt
RUN chmod +x /app/tools/whisper/whisper-cli

# Install Node.js only for the Discord bot runtime. npm is used for the install
# and then removed from the final filesystem; the bot only needs node.
RUN sh /tmp/listenarr-runtime/install-discord-bot-runtime.sh

RUN sh /tmp/listenarr-runtime/finalize-app.sh

# Copy entrypoint script for PUID/PGID/UMASK support
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN sh /tmp/listenarr-runtime/prepare-entrypoint.sh \
	&& rm -rf /tmp/listenarr-runtime

ENTRYPOINT ["/docker-entrypoint.sh"]
