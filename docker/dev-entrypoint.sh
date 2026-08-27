#!/bin/sh
# Dev entrypoint. Lives in a file rather than compose's `command:` so the nested
# quoting concurrently needs is not mangled by YAML folding.
set -e

echo "[dev] installing root dependencies"
npm install --no-audit --no-fund --silent

echo "[dev] installing frontend dependencies"
(cd /src/fe && npm install --no-audit --no-fund --silent)

echo "[dev] ffprobe: $(command -v ffprobe || echo 'MISSING')"
echo "[dev] starting API + web"

exec npx concurrently --names API,WEB --prefix-colors blue,green \
  "cd /src/listenarr.api && dotnet watch run --no-launch-profile --urls http://0.0.0.0:4545" \
  "cd /src/fe && npm run dev -- --host 0.0.0.0 --port 5173"
