#!/bin/bash
set -e

PUID=${PUID:-0}
PGID=${PGID:-${GID:-${PUID}}}
UMASK=${UMASK:-${UMASK_SET:-022}}

umask "$UMASK"

if [ -n "${GID:-}" ] && [ -n "${PGID:-}" ] && [ "${GID}" != "${PGID}" ]; then
    echo "PGID=${PGID} takes precedence over GID=${GID}"
fi

if [ -n "${UMASK_SET:-}" ] && [ -n "${UMASK:-}" ] && [ "${UMASK_SET}" != "${UMASK}" ]; then
    echo "UMASK=${UMASK} takes precedence over UMASK_SET=${UMASK_SET}"
fi

# If running as root and PUID/PGID are set to non-root, remap the service account
# similarly to linuxserver.io's abc user handling.
if [ "$(id -u)" = "0" ] && { [ "$PUID" != "0" ] || [ "$PGID" != "0" ]; }; then
    echo "Starting Listenarr with UID=$PUID GID=$PGID UMASK=$UMASK"

    if [ "$PUID" != "0" ] && [ "$PGID" != "0" ]; then
        groupmod -o -g "$PGID" listenarr
        usermod -o -u "$PUID" listenarr
        chown -R "$PUID:$PGID" /app/config

        exec gosu listenarr dotnet Listenarr.Api.dll "$@"
    fi

    chown -R "$PUID:$PGID" /app/config
    exec gosu "$PUID:$PGID" dotnet Listenarr.Api.dll "$@"
fi

echo "Starting Listenarr as $(id)"
exec dotnet Listenarr.Api.dll "$@"
