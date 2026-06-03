#!/bin/sh
set -eu

if [ -d /app/wwwroot ]; then
	find /app/wwwroot -type d -exec chmod 755 {} \;
	find /app/wwwroot -type f -exec chmod 644 {} \;
fi

mkdir -p /app/config/database
