#!/usr/bin/env node
// Regenerates the audiobook files the browser E2E suite imports (tests/Fixtures/Audiobooks/): one
// second of silence each, in a real M4B or MP3 container, with the tags the library scan reads through
// ffprobe. The files and their tags are the `fixtures` of tests/Fixtures/Audiobooks/import-folders.json
// (title, author, album, and optionally seriesPart, with the album as the series, and asin; an empty
// entry writes no tags). The specs assert on these tags (row titles, authors, series, the ASIN match,
// the three-part grouping of the MP3), so keep them when changing a book. Needs ffmpeg; the E2E run
// itself only copies the committed files (import-folders.mjs).
//
//   node scripts/e2e/regenerate-fixtures.mjs [output directory, default tests/Fixtures/Audiobooks]
//   npm run e2e:regenerate-fixtures
import { execFile } from 'node:child_process'
import { mkdir, readFile } from 'node:fs/promises'
import { dirname, extname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { defaultManifestPath } from './import-folders.mjs'

/** The ffmpeg arguments that write `output` as one second of silence with those tags. */
export function ffmpegArguments(output, { title, author, album, seriesPart, asin } = {}) {
  const tags = []
  const tag = (name, value) => tags.push('-metadata', `${name}=${value}`)
  if (title !== undefined) tag('title', title)
  if (author !== undefined) {
    tag('artist', author)
    tag('album_artist', author)
  }
  if (album !== undefined) tag('album', album)
  if (seriesPart !== undefined) {
    tag('SERIES', album)
    tag('SERIES-PART', seriesPart)
  }
  if (asin !== undefined) tag('ASIN', asin)
  const codec = extname(output).toLowerCase() === '.mp3' ? 'libmp3lame' : 'aac'
  // use_metadata_tags keeps the custom MP4 tags (SERIES, SERIES-PART, ASIN).
  return [
    '-y', '-v', 'error', '-f', 'lavfi', '-i', 'anullsrc=r=22050:cl=mono', '-t', '1',
    '-c:a', codec, '-movflags', 'use_metadata_tags',
    ...tags,
    output,
  ]
}

const runFfmpeg = async (args) => {
  await promisify(execFile)('ffmpeg', args)
}

/** Writes every fixture of the manifest into `output` (default: next to the manifest). */
export async function regenerateFixtures({
  manifestPath = defaultManifestPath,
  output = dirname(manifestPath),
  run = runFfmpeg,
} = {}) {
  const { fixtures } = JSON.parse(await readFile(manifestPath, 'utf8'))
  await mkdir(output, { recursive: true })
  for (const [file, tags] of Object.entries(fixtures)) await run(ffmpegArguments(join(output, file), tags))
  return Object.keys(fixtures)
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const output = process.argv[2] ? resolve(process.argv[2]) : undefined
  const written = await regenerateFixtures({ output })
  console.log(`Regenerated ${written.length} E2E audiobook fixtures in ${output ?? dirname(defaultManifestPath)}`)
}
