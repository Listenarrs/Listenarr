#!/usr/bin/env bash
set -euo pipefail

generator_repo=https://github.com/m4bard/listenarr-testdata.git
generator_commit=bd59f6f9a891a70b3cccb644b9ae0fc3f50a5ecd
output_directory=${1:?Pass the directory where the generated audiobook should be written.}
temporary_directory=$(mktemp -d)
trap 'rm -rf "$temporary_directory"' EXIT

git clone --quiet "$generator_repo" "$temporary_directory/generator"
git -C "$temporary_directory/generator" checkout --quiet --detach "$generator_commit"
python3 -m venv "$temporary_directory/generator/.venv"
"$temporary_directory/generator/.venv/bin/pip" install --quiet -e "$temporary_directory/generator"
"$temporary_directory/generator/.venv/bin/python" \
  "$temporary_directory/generator/tools/generate_library.py" \
  --scenario happy-path \
  --layout author-title \
  --tag-state correct-with-asin \
  --structure single \
  --ffmpeg-source system \
  --seed 1 \
  --out "$output_directory" \
  --only-asin B008DFUGCQ >/dev/null