#!/bin/sh
set -eu

# Usage: install-discord-bot-runtime.sh [app directory]
#
# The default is the published bot in /app/tools/discord-bot. The root Dockerfile passes a staging
# directory holding only package.json and package-lock.json, so the npm install is cached on the
# lockfile instead of being repeated whenever the published app changes.
app_dir=${1:-/app/tools/discord-bot}

if [ ! -f "$app_dir/package-lock.json" ]; then
	echo "ERROR: $app_dir/package-lock.json is missing."
	exit 1
fi

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl gnupg libcap2
curl -fsSL https://deb.nodesource.com/setup_26.x | bash -
apt-get install -y --no-install-recommends nodejs

cd "$app_dir"
npm ci --omit=dev --no-audit --no-fund
find node_modules -type f -name "*.map" -delete
npm cache clean --force
node --version

apt-get purge -y --auto-remove curl gnupg
rm -rf /usr/lib/node_modules/npm \
	/usr/bin/npm \
	/usr/bin/npx \
	/usr/bin/corepack \
	/usr/include/node \
	/root/.npm \
	/usr/share/doc \
	/usr/share/man \
	/usr/share/info \
	/var/lib/apt/lists/*
find /tmp -mindepth 1 -maxdepth 1 ! -name listenarr-runtime -exec rm -rf {} +
