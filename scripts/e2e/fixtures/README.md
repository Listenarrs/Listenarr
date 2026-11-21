# E2E audiobook fixtures

The browser E2E suite imports these files as fake audiobooks: one second of silence in a real
M4B or MP3 container, with the tags the library scan reads through `ffprobe`. They are copied into
place by `../generate-library-fixtures.sh`, so the E2E run needs no `ffmpeg`; the test host still
uses `ffprobe` to read the tags.

| File | Title | Author (`artist`, `album_artist`) | `album` / `SERIES` | `SERIES-PART` | `ASIN` |
| --- | --- | --- | --- | --- | --- |
| `B008DFUGCQ.m4b` | A Princess of Mars | Edgar Rice Burroughs | Barsoom | 1 | B008DFUGCQ |
| `B0C6B525PQ.m4b` | Tanar of Pellucidar | Edgar Rice Burroughs | album only: Tanar of Pellucidar | | |
| `no-tags.m4b` | no tags: the scan falls back to the folder name | | | | |
| `B0096QR7Z0-part.mp3` | Allan Quatermain | H. Rider Haggard | Allan Quatermain | 15 | |
| `B01FKWL15A.m4b` | Twenty Thousand Leagues Under the Sea | Jules Verne | Captain Nemo | 1 | B01FKWL15A |
| `B002V5B7TK.m4b` | Tarzan of the Apes | Edgar Rice Burroughs | Tarzan | 1 | B002V5B7TK |

The specs assert on these tags (row titles, authors, series, the ASIN match, the three-part
grouping of the MP3), so keep them when changing a file.

## Regenerating

Needs only `ffmpeg`. Run from this directory:

```bash
make() { # <file> [ffmpeg -metadata options...]
  local file=$1; shift
  local codec=aac; [ "${file##*.}" = mp3 ] && codec=libmp3lame
  ffmpeg -y -v error -f lavfi -i anullsrc=r=22050:cl=mono -t 1 -c:a "$codec" \
    -movflags use_metadata_tags "$@" "$file"
}
book() { # <file> <title> <author> <album> [<series part> [<asin>]]
  local file=$1 title=$2 author=$3 album=$4 part=${5:-} asin=${6:-}
  make "$file" -metadata title="$title" -metadata artist="$author" -metadata album_artist="$author" \
    -metadata album="$album" ${part:+-metadata SERIES="$album" -metadata SERIES-PART="$part"} \
    ${asin:+-metadata ASIN="$asin"}
}
book B008DFUGCQ.m4b 'A Princess of Mars' 'Edgar Rice Burroughs' Barsoom 1 B008DFUGCQ
book B0C6B525PQ.m4b 'Tanar of Pellucidar' 'Edgar Rice Burroughs' 'Tanar of Pellucidar'
make no-tags.m4b
book B0096QR7Z0-part.mp3 'Allan Quatermain' 'H. Rider Haggard' 'Allan Quatermain' 15
book B01FKWL15A.m4b 'Twenty Thousand Leagues Under the Sea' 'Jules Verne' 'Captain Nemo' 1 B01FKWL15A
book B002V5B7TK.m4b 'Tarzan of the Apes' 'Edgar Rice Burroughs' Tarzan 1 B002V5B7TK
```

The output is not byte-identical to the committed files (they also carry `composer` and `date`
tags, and the MP3 lasts 1.07 s), but every tag above comes out the same, which is what the specs
read.
