# E2E audiobook fixtures

The browser E2E suite imports these files as fake audiobooks: one second of silence in a real M4B
or MP3 container, with the tags the library scan reads through `ffprobe` (title, author, album,
series, ASIN). `../generate-library-fixtures.sh` copies them into place, so the E2E run needs no
`ffmpeg`; the test host still uses `ffprobe` to read the tags.

The books and their tags are defined in `../regenerate-fixtures.sh`. To change a book or add one,
edit that script and run it (needs `ffmpeg`):

```bash
npm run e2e:regenerate-fixtures
```
