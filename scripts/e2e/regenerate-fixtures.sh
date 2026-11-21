#!/usr/bin/env bash
# Regenerates the audiobook files the browser E2E suite imports (tests/Fixtures/Audiobooks/): one second of
# silence each, in a real M4B or MP3 container, with the tags the library scan reads through ffprobe.
# The specs assert on these tags (row titles, authors, series, the ASIN match, the three-part grouping
# of the MP3), so keep them when changing a book. Needs ffmpeg; the E2E run itself only copies the
# committed files into the folders tests/Fixtures/Audiobooks/import-folders.json lists
# (scripts/e2e/import-folders.mjs).
#
#   scripts/e2e/regenerate-fixtures.sh [output directory, default tests/Fixtures/Audiobooks]
#   npm run e2e:regenerate-fixtures
set -euo pipefail

output=${1:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)/tests/Fixtures/Audiobooks}
command -v ffmpeg >/dev/null || { echo "ffmpeg is required" >&2; exit 1; }
mkdir -p "$output"

# silence <file> [ffmpeg -metadata options...]
silence() {
  local file=$1 codec=aac
  shift
  [ "${file##*.}" = mp3 ] && codec=libmp3lame
  # use_metadata_tags keeps the custom MP4 tags (SERIES, SERIES-PART, ASIN).
  ffmpeg -y -v error -f lavfi -i anullsrc=r=22050:cl=mono -t 1 -c:a "$codec" \
    -movflags use_metadata_tags "$@" "$output/$file"
}

# book <file> <title> <author> <album> [<series part> [<asin>]]: with a part, the album is the series.
book() {
  local file=$1 title=$2 author=$3 album=$4 part=${5:-} asin=${6:-}
  local tags=(-metadata "title=$title" -metadata "artist=$author" -metadata "album_artist=$author" -metadata "album=$album")
  [ -n "$part" ] && tags+=(-metadata "SERIES=$album" -metadata "SERIES-PART=$part")
  [ -n "$asin" ] && tags+=(-metadata "ASIN=$asin")
  silence "$file" "${tags[@]}"
}

book B008DFUGCQ.m4b 'A Princess of Mars' 'Edgar Rice Burroughs' Barsoom 1 B008DFUGCQ
book B0C6B525PQ.m4b 'Tanar of Pellucidar' 'Edgar Rice Burroughs' 'Tanar of Pellucidar'
# No tags at all: the scan falls back to the folder name (Pellucidar).
silence no-tags.m4b
# Copied to the three parts of one book.
book B0096QR7Z0-part.mp3 'Allan Quatermain' 'H. Rider Haggard' 'Allan Quatermain' 15
book B01FKWL15A.m4b 'Twenty Thousand Leagues Under the Sea' 'Jules Verne' 'Captain Nemo' 1 B01FKWL15A
book B002V5B7TK.m4b 'Tarzan of the Apes' 'Edgar Rice Burroughs' Tarzan 1 B002V5B7TK

echo "Regenerated the E2E audiobook fixtures in $output"
