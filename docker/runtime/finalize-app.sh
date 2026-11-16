#!/bin/sh
set -eu

if [ -d /app/wwwroot ]; then
	find /app/wwwroot -type d -exec chmod 755 {} \;
	find /app/wwwroot -type f -exec chmod 644 {} \;
fi

# ffprobe is restored from a NuGet package (Openur.FFprobeStatic). Restore leaves it executable
# by its owner at most, and the app runs as a different user than the one that owns /app, so set
# the mode for everyone here. A RID-specific publish puts it at /app/ffprobe; a RID-agnostic one
# leaves it under runtimes/<rid>/native/.
find /app -maxdepth 1 -type f -name ffprobe -exec chmod 755 {} +
if [ -d /app/runtimes ]; then
	# A RID-agnostic publish (the root Dockerfile) carries ffprobe for every platform the
	# package supports, around 185 MB. Keep only this image's Linux builds.
	case "$(uname -m)" in
		x86_64) keep_arch=x64 ;;
		aarch64) keep_arch=arm64 ;;
		armv7l) keep_arch=arm ;;
		*) keep_arch="" ;;
	esac
	if [ -n "$keep_arch" ]; then
		find /app/runtimes -type f -path '*/native/ffprobe*' \
			! -path "/app/runtimes/linux-$keep_arch/*" \
			! -path "/app/runtimes/linux-musl-$keep_arch/*" \
			-delete
	fi
	find /app/runtimes -type f -path '*/native/ffprobe' -exec chmod 755 {} +
fi

mkdir -p /app/config/database
