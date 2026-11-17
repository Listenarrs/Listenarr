import assert from 'node:assert/strict'
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { convertVideos, ffmpegGifArgs, findVideos } from './webm-to-gif.mjs'

test('ffmpegGifArgs reads the input, applies a palette filter, and writes the output last', () => {
  const args = ffmpegGifArgs('/out/test/video.webm', '/out/test/video.gif')
  const i = args.indexOf('-i')
  assert.ok(i !== -1, 'has an -i flag')
  assert.equal(args[i + 1], '/out/test/video.webm')
  assert.equal(args.at(-1), '/out/test/video.gif')
  assert.ok(
    args.some((a) => a.includes('palettegen') && a.includes('paletteuse')),
    'uses a two-pass palette filtergraph',
  )
})

test('findVideos returns [] for a directory that does not exist', async () => {
  assert.deepEqual(await findVideos('/no/such/dir/at/all'), [])
})

test('findVideos finds every video.webm recursively and ignores other files', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'webm-'))
  await mkdir(join(dir, 'failing-test'), { recursive: true })
  await mkdir(join(dir, 'other-test'), { recursive: true })
  await writeFile(join(dir, 'failing-test', 'video.webm'), 'x')
  await writeFile(join(dir, 'failing-test', 'trace.zip'), 'x')
  await writeFile(join(dir, 'other-test', 'video.webm'), 'x')
  await writeFile(join(dir, 'other-test', 'test-failed-1.png'), 'x')

  const found = (await findVideos(dir)).sort()
  assert.deepEqual(found, [
    join(dir, 'failing-test', 'video.webm'),
    join(dir, 'other-test', 'video.webm'),
  ])
})

test('convertVideos on a directory with no videos does nothing and does not throw', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'webm-'))
  assert.deepEqual(await convertVideos(dir), [])
})

test('convertVideos skips a retained video when ffmpeg is missing', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'webm-'))
  await mkdir(join(dir, 'failing-test'), { recursive: true })
  await writeFile(join(dir, 'failing-test', 'video.webm'), 'x')
  const emptyPath = await mkdtemp(join(tmpdir(), 'no-ffmpeg-'))
  const originalPath = process.env.PATH
  process.env.PATH = emptyPath
  try {
    assert.deepEqual(await convertVideos(dir), [])
  } finally {
    process.env.PATH = originalPath
  }
})
