#!/bin/sh
set -eu

sed -i 's/\r$//' /docker-entrypoint.sh
chmod +x /docker-entrypoint.sh
