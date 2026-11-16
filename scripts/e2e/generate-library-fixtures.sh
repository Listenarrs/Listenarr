#!/usr/bin/env bash
# Generates the synthetic audiobook folders the browser E2E suite (fe/playwright/e2e) imports, with the
# generator created by m4bard (https://github.com/m4bard/listenarr-testdata), pinned below:
#
#   scripts/e2e/generate-library-fixtures.sh <output directory>
#
# <output directory>/import  the main Library Import root folder (one folder per book, see BOOKS)
# <output directory>/second  another root folder with one book
# <output directory>/empty   a root folder without audio files
# <output directory>/library the destination of Move and Hardlink / Copy
set -euo pipefail

generator_repo=https://github.com/m4bard/listenarr-testdata.git
generator_commit=bd59f6f9a891a70b3cccb644b9ae0fc3f50a5ecd
output_directory=${1:?Pass the directory where the generated library should be written.}
temporary_directory=$(mktemp -d)
trap 'rm -rf "$temporary_directory"' EXIT

git clone --quiet "$generator_repo" "$temporary_directory/generator"
git -C "$temporary_directory/generator" checkout --quiet --detach "$generator_commit"
python3 -m venv "$temporary_directory/generator/.venv"
"$temporary_directory/generator/.venv/bin/pip" install --quiet -e "$temporary_directory/generator"

# generate <root folder> <ASIN> <tag state> <file structure> [layout]
generate() {
  local root=$1 asin=$2 tag_state=$3 structure=$4 layout=${5:-author-title}
  local out="$temporary_directory/out-$asin"
  "$temporary_directory/generator/.venv/bin/python" \
    "$temporary_directory/generator/tools/generate_library.py" \
    --scenario happy-path \
    --layout "$layout" \
    --tag-state "$tag_state" \
    --structure "$structure" \
    --ffmpeg-source system \
    --seed 1 \
    --out "$out" \
    --only-asin "$asin" >/dev/null
  mkdir -p "$output_directory/$root"
  cp -a "$out"/. "$output_directory/$root"/
  rm -f "$output_directory/$root/manifest.json"
}

# BOOKS: what each folder exercises, with the Audible fixture it needs
# (tests/Listenarr.FakeProviders/Fixtures/Audible).
# A Princess of Mars: tagged ASIN, matched by ASIN (B008DFUGCQ.json).
generate import B008DFUGCQ correct-with-asin single
# The scan reads a book's title from its album tag, which the generator sets to the series for books in
# one: the two books matched by title and author are books whose album tag is their title. Audible is
# searched for the author's catalogue, then filtered by title (search-author-*.json).
# Tanar of Pellucidar: title and author tags (search-author-edgar-rice-burroughs.json).
generate import B0C6B525PQ correct-no-asin single
# Allan Quatermain: three MP3 parts grouped into one row (search-author-h-rider-haggard.json).
generate import B0096QR7Z0 correct-no-asin multi-part
# Pellucidar: no tags, searched by its folder name, which Audible doesn't know.
generate import B002V1OVFQ no-tags single
# Twenty Thousand Leagues Under the Sea: tagged ASIN of a book already in the seeded library (B01FKWL15A.json).
generate import B01FKWL15A correct-with-asin single
# Tarzan of the Apes: the other root folder's only book (B002V5B7TK.json).
generate second B002V5B7TK correct-with-asin single

mkdir -p "$output_directory/empty" "$output_directory/library"
