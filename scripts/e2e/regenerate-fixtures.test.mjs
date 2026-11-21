import assert from 'node:assert/strict'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { ffmpegArguments, regenerateFixtures } from './regenerate-fixtures.mjs'

const silence = ['-y', '-v', 'error', '-f', 'lavfi', '-i', 'anullsrc=r=22050:cl=mono', '-t', '1']

test('ffmpegArguments tags a series book with its part and ASIN, the album as the series', () => {
  assert.deepEqual(
    ffmpegArguments('/out/B008DFUGCQ.m4b', {
      title: 'A Princess of Mars',
      author: 'Edgar Rice Burroughs',
      album: 'Barsoom',
      seriesPart: '1',
      asin: 'B008DFUGCQ',
    }),
    [
      ...silence,
      '-c:a', 'aac', '-movflags', 'use_metadata_tags',
      '-metadata', 'title=A Princess of Mars',
      '-metadata', 'artist=Edgar Rice Burroughs',
      '-metadata', 'album_artist=Edgar Rice Burroughs',
      '-metadata', 'album=Barsoom',
      '-metadata', 'SERIES=Barsoom',
      '-metadata', 'SERIES-PART=1',
      '-metadata', 'ASIN=B008DFUGCQ',
      '/out/B008DFUGCQ.m4b',
    ],
  )
})

test('ffmpegArguments leaves out the series and ASIN a book does not have, and encodes MP3 as MP3', () => {
  assert.deepEqual(
    ffmpegArguments('/out/tanar.mp3', { title: 'Tanar', author: 'Burroughs', album: 'Tanar' }),
    [
      ...silence,
      '-c:a', 'libmp3lame', '-movflags', 'use_metadata_tags',
      '-metadata', 'title=Tanar',
      '-metadata', 'artist=Burroughs',
      '-metadata', 'album_artist=Burroughs',
      '-metadata', 'album=Tanar',
      '/out/tanar.mp3',
    ],
  )
})

test('ffmpegArguments writes no tags at all for an empty entry', () => {
  assert.deepEqual(ffmpegArguments('/out/no-tags.m4b', {}), [
    ...silence,
    '-c:a', 'aac', '-movflags', 'use_metadata_tags',
    '/out/no-tags.m4b',
  ])
})

test('regenerateFixtures runs ffmpeg once per fixture of the manifest, into the output directory', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'regenerate-fixtures-test-'))
  try {
    const manifestPath = join(directory, 'import-folders.json')
    await writeFile(
      manifestPath,
      JSON.stringify({
        fixtures: { 'a.m4b': { title: 'A', author: 'X', album: 'A' }, 'b.mp3': {} },
        roots: {},
      }),
    )
    const output = join(directory, 'out')
    const runs = []
    await regenerateFixtures({ manifestPath, output, run: async (args) => runs.push(args) })
    assert.deepEqual(
      runs.map((args) => args.at(-1)),
      [join(output, 'a.m4b'), join(output, 'b.mp3')],
    )
  } finally {
    await rm(directory, { recursive: true, force: true })
  }
})
