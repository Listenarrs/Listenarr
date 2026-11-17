#!/bin/sh
# Entry point of the E2E test host image (scripts/e2e/e2e-host.Dockerfile). It supervises
# tests/Listenarr.FakeProviders rather than replacing itself with it, so the suites can restart the app
# without restarting the container: a container restart recreates its network interface, and the
# host's network change makes Chromium abort the page loads in flight (net::ERR_NETWORK_CHANGED).
#
#   docker kill --signal USR1 <container>  stops the app; /run/listenarr-host.stopped appears once it exited
#   docker kill --signal USR2 <container>  starts it again
#   docker stop <container>                stops the app, then the container
#
# Each start puts ffprobe where Listenarr looks for it (its config directory, which the suites
# restore while the app is stopped) and runs the host through the production entry point's user
# remapping. With LISTENARR_COVERAGE_DIR set, the host runs under dotnet-coverage, which writes one
# Cobertura report per app lifetime into that directory when the app stops.
stopped_marker=/run/listenarr-host.stopped
app_pid=

start_app() {
  [ -n "$app_pid" ] && return
  mkdir -p /app/config/ffmpeg
  cp /usr/bin/ffprobe /app/config/ffmpeg/ffprobe
  chmod 755 /app/config/ffmpeg/ffprobe
  if [ -n "${LISTENARR_COVERAGE_DIR:-}" ]; then
    mkdir -p "$LISTENARR_COVERAGE_DIR"
    /testhost-entrypoint.sh /opt/dotnet-coverage/dotnet-coverage collect \
      --settings /opt/dotnet-coverage/listenarr.coverage.xml \
      --output-format cobertura \
      --output "$LISTENARR_COVERAGE_DIR/testhost-$(date +%s%N).cobertura.xml" \
      -- dotnet /app/integration-host/Listenarr.FakeProviders.dll &
  else
    /testhost-entrypoint.sh dotnet /app/integration-host/Listenarr.FakeProviders.dll &
  fi
  app_pid=$!
  rm -f "$stopped_marker"
}

stop_app() {
  if [ -n "$app_pid" ]; then
    kill -TERM "$app_pid" 2>/dev/null
    wait "$app_pid" 2>/dev/null
    app_pid=
  fi
  touch "$stopped_marker"
}

trap 'stop_app' USR1
trap 'start_app' USR2
trap 'stop_app; exit 0' TERM INT

start_app
# Signals are handled between two sleeps.
while :; do
  sleep 1 &
  wait $! 2>/dev/null
  # The app exited by itself (a crash): say so, the suite's readiness wait reports it.
  if [ -n "$app_pid" ] && ! kill -0 "$app_pid" 2>/dev/null; then
    wait "$app_pid" 2>/dev/null
    echo "Listenarr test host exited" >&2
    app_pid=
    touch "$stopped_marker"
  fi
done
