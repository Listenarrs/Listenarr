import assert from 'node:assert/strict'
import { mkdir, mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { defaultManifestPath, readImportFolders, writeImportFolders } from './import-folders.mjs'

async function withFixtures(manifest, files, run) {
  const directory = await mkdtemp(join(tmpdir(), 'import-folders-test-'))
  try {
    for (const [name, content] of Object.entries(files)) await writeFile(join(directory, name), content)
    const manifestPath = join(directory, 'import-folders.json')
    await writeFile(manifestPath, JSON.stringify(manifest))
    await run({ directory, manifestPath })
  } finally {
    await rm(directory, { recursive: true, force: true })
  }
}

async function tree(directory, prefix = '') {
  const entries = await readdir(join(directory, prefix), { withFileTypes: true })
  const paths = []
  for (const entry of entries) {
    const path = prefix ? `${prefix}/${entry.name}` : entry.name
    paths.push(entry.isDirectory() ? `${path}/` : path)
    if (entry.isDirectory()) paths.push(...(await tree(directory, path)))
  }
  return paths.sort()
}

const manifest = {
  fixtures: { 'mars.m4b': { title: 'Mars', author: 'Burroughs', album: 'Mars' }, 'allan.mp3': {} },
  roots: {
    import: {
      Mars: { folder: 'Burroughs/Mars', files: [{ name: 'Mars.m4b', fixture: 'mars.m4b' }] },
      Allan: {
        folder: 'Haggard/Allan',
        files: [
          { name: 'Allan - Part 01.mp3', fixture: 'allan.mp3' },
          { name: 'Allan - Part 02.mp3', fixture: 'allan.mp3' },
        ],
      },
    },
    empty: {},
  },
}
const fixtures = { 'mars.m4b': 'mars audio', 'allan.mp3': 'allan audio', 'README.md': 'docs' }

test('writeImportFolders copies each fixture to its book folder, one folder per root', async () => {
  await withFixtures(manifest, fixtures, async ({ manifestPath }) => {
    const output = await mkdtemp(join(tmpdir(), 'import-folders-output-'))
    try {
      await writeImportFolders(output, { manifestPath })
      assert.deepEqual(await tree(output), [
        'empty/',
        'import/',
        'import/Burroughs/',
        'import/Burroughs/Mars/',
        'import/Burroughs/Mars/Mars.m4b',
        'import/Haggard/',
        'import/Haggard/Allan/',
        'import/Haggard/Allan/Allan - Part 01.mp3',
        'import/Haggard/Allan/Allan - Part 02.mp3',
      ])
      assert.equal(await readFile(join(output, 'import/Burroughs/Mars/Mars.m4b'), 'utf8'), 'mars audio')
      assert.equal(await readFile(join(output, 'import/Haggard/Allan/Allan - Part 02.mp3'), 'utf8'), 'allan audio')
    } finally {
      await rm(output, { recursive: true, force: true })
    }
  })
})

test('writeImportFolders leaves its folder readable by other users, as a mkdtemp folder is not', async () => {
  // The folder becomes the container's /e2e (copied with fs.cp, which keeps its mode); mkdtemp makes 0700.
  const output = await mkdtemp(join(tmpdir(), 'import-folders-mode-'))
  try {
    await writeImportFolders(output)
    assert.equal((await stat(output)).mode & 0o777, 0o755)
  } finally {
    await rm(output, { recursive: true, force: true })
  }
})

test('readImportFolders names a listed fixture whose file is missing', async () => {
  await withFixtures(manifest, { 'allan.mp3': 'allan audio' }, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /fixtures\.mars\.m4b: no file mars\.m4b/)
  })
})

test('readImportFolders names an audio file the fixtures do not list', async () => {
  await withFixtures(manifest, { ...fixtures, 'unlisted.m4b': 'x' }, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /unlisted\.m4b is not listed in fixtures/)
  })
})

test('readImportFolders names the book whose fixture is not listed', async () => {
  const unlisted = structuredClone(manifest)
  delete unlisted.fixtures['mars.m4b']
  await withFixtures(unlisted, { 'allan.mp3': 'allan audio' }, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /import\.Mars: fixture mars\.m4b is not listed in fixtures/)
  })
})

test('readImportFolders names a listed fixture no book uses', async () => {
  const unused = structuredClone(manifest)
  unused.fixtures['spare.m4b'] = {}
  await withFixtures(unused, { ...fixtures, 'spare.m4b': 'x' }, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /fixtures\.spare\.m4b is not used by any book/)
  })
})

test('readImportFolders names two books sharing a folder', async () => {
  const shared = structuredClone(manifest)
  shared.roots.import.Allan.folder = 'Burroughs/Mars'
  await withFixtures(shared, fixtures, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /import\.Allan: folder Burroughs\/Mars is already import\.Mars's/)
  })
})

test('readImportFolders refuses a path that leaves its root', async () => {
  const escaping = structuredClone(manifest)
  escaping.roots.import.Mars.folder = '../Mars'
  await withFixtures(escaping, fixtures, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /import\.Mars: folder \.\.\/Mars must stay inside its root/)
  })
  const nested = structuredClone(manifest)
  nested.roots.import.Mars.files[0].name = 'sub/Mars.m4b'
  await withFixtures(nested, fixtures, async ({ manifestPath }) => {
    await assert.rejects(readImportFolders(manifestPath), /import\.Mars: file sub\/Mars\.m4b must be a plain file name/)
  })
})

test('the checked-in manifest is valid: every fixture exists and is used', async () => {
  const folders = await readImportFolders(defaultManifestPath)
  assert.ok(Object.keys(folders.roots.import).length > 0)
})
