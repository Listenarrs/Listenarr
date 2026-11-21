#!/usr/bin/env bash
# Copies the synthetic audiobook folders the browser E2E suite (fe/playwright/e2e) imports:
#
#   scripts/e2e/generate-library-fixtures.sh <output directory>
#
# <output directory>/import  the main Library Import root folder (one folder per book)
# <output directory>/second  another root folder with one book
# <output directory>/empty   a root folder without audio files
# <output directory>/library the destination of Move and Hardlink / Copy
#
# The six files in scripts/e2e/fixtures/ (one second of silence each, 1 to 6 KB) carry the tags
# ffprobe reads at scan time; copying them keeps those tags, so this needs no ffmpeg. How to
# regenerate them: scripts/e2e/fixtures/README.md.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixtures="$script_directory/fixtures"
output_directory=${1:?Pass the directory where the generated library should be written.}

copy_book() {
  local root=$1 relative_path=$2 fixture=$3
  mkdir -p "$output_directory/$root/$(dirname "$relative_path")"
  cp "$fixtures/$fixture" "$output_directory/$root/$relative_path"
}

# The tags each fixture carries (ffprobe's album / album_artist / SERIES / ASIN) are what the scan
# asserts on: the row title, .folder-meta, the ASIN match and the three-part grouping. See
# fe/playwright/e2e/library-import-scan.spec.ts.
copy_book import 'Edgar Rice Burroughs/A Princess of Mars/A Princess of Mars.m4b' B008DFUGCQ.m4b
copy_book import 'Edgar Rice Burroughs/Tanar of Pellucidar/Tanar of Pellucidar.m4b' B0C6B525PQ.m4b
copy_book import 'Edgar Rice Burroughs/Pellucidar/Pellucidar.m4b' no-tags.m4b
for part in 01 02 03; do
  copy_book import "H. Rider Haggard/Allan Quatermain/Allan Quatermain - Part $part.mp3" B0096QR7Z0-part.mp3
done
copy_book import 'Jules Verne/Twenty Thousand Leagues Under the Sea/Twenty Thousand Leagues Under the Sea.m4b' B01FKWL15A.m4b
copy_book second 'Edgar Rice Burroughs/Tarzan of the Apes/Tarzan of the Apes.m4b' B002V5B7TK.m4b

mkdir -p "$output_directory/empty" "$output_directory/library"
