# E2E audiobook fixtures

The browser E2E suite imports these files as fake audiobooks: one second of silence in a real M4B
or MP3 container, with the tags the library scan reads through `ffprobe` (title, author, album,
series, ASIN). The E2E run needs no `ffmpeg`; the test host still uses `ffprobe` to read the tags.

`import-folders.json` is the one place the scanned folders are defined: for each root folder
(`import`, `second`, `empty`, `library`, mounted as `/e2e/<root>`), each book's folder and the fixture
each of its files is copied from. `scripts/e2e/import-folders.mjs` writes them before the run, and the
specs read the same file through `fe/playwright/e2e/support/import-folders.ts`, by book key
(`importBook('Princess')`). It is checked first: a missing fixture, an audio file here that no book
uses, two books in one folder or a path leaving its root fails with the book's name
(`npm run test:e2e-import-folders`).

Not to be confused with `scripts/e2e/seed-library.json`, the books seeded into Listenarr's database.

The tags are defined in `scripts/e2e/regenerate-fixtures.sh`. To change a book's tags or add an audio
file, edit that script and run it (needs `ffmpeg`), then list the file in `import-folders.json`:

```bash
npm run e2e:regenerate-fixtures
```
