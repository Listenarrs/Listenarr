#!/bin/sh
set -eu

# Usage: prepare-ffprobe.sh <app directory>
#
# ffprobe is restored from a NuGet package (Openur.FFprobeStatic). Restore leaves it executable
# by its owner at most, and the app runs as a different user than the one that owns /app, so the
# mode is set for everyone. A RID-specific publish puts it at <app>/ffprobe; a RID-agnostic one
# leaves it under runtimes/<rid>/native/.
#
# A change made in a later image layer copies the whole file into that layer, so the root
# Dockerfile runs this on the publish output in its build stage, and CI sets the mode on the
# Dockerfile.runtime input before the image build. finalize-app.sh runs it again in the final
# image for local builds; a binary that is already 755 is left untouched, so that adds nothing.
app_dir=${1:?usage: prepare-ffprobe.sh <app directory>}

if [ -d "$app_dir/runtimes" ]; then
	# A RID-agnostic publish carries ffprobe for every platform the package supports, around
	# 185 MB. Keep only this image's Linux build, plus the musl one on a musl system. The root
	# Dockerfile's build stage and final image share a libc, so the check holds for both.
	case "$(uname -m)" in
		x86_64) keep_arch=x64 ;;
		aarch64) keep_arch=arm64 ;;
		armv7l) keep_arch=arm ;;
		*) keep_arch="" ;;
	esac
	if [ -n "$keep_arch" ]; then
		keep_musl="$app_dir/runtimes/linux-$keep_arch/*"
		if ls /lib/ld-musl-* > /dev/null 2>&1; then
			keep_musl="$app_dir/runtimes/linux-musl-$keep_arch/*"
		fi
		find "$app_dir/runtimes" -type f -path '*/native/ffprobe*' \
			! -path "$app_dir/runtimes/linux-$keep_arch/*" \
			! -path "$keep_musl" \
			-delete
	fi
	find "$app_dir/runtimes" -type f -path '*/native/ffprobe' ! -perm 755 -exec chmod 755 {} +
fi
find "$app_dir" -maxdepth 1 -type f -name ffprobe ! -perm 755 -exec chmod 755 {} +
