#!/bin/sh
set -eu

if [ ! -f /app/tools/discord-bot/package-lock.json ]; then
	echo "ERROR: /app/tools/discord-bot/package-lock.json is missing."
	exit 1
fi

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl gnupg libcap2
curl -fsSL https://deb.nodesource.com/setup_24.x | bash -
apt-get install -y --no-install-recommends nodejs

cd /app/tools/discord-bot
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
