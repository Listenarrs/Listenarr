// The audiobook folders the browser E2E suite scans and imports, built from one manifest:
// tests/Fixtures/Audiobooks/import-folders.json lists the checked-in pre-tagged fixtures and their tags
// (`fixtures`, what regenerate-fixtures.mjs writes) and, per root folder (import, second, empty,
// library), each book's folder and the fixture each of its files is copied from (`roots`).
// The specs read the same manifest (fe/playwright/e2e/support/import-folders.ts), so a book is named
// in one place. The manifest is checked before anything is written: a listed fixture without its file,
// an audio file or a book's fixture not listed, a listed fixture no book uses, two books in one folder
// or a path leaving its root fails with the fixture's or the book's name.
import { chmod, copyFile, mkdir, readdir, readFile } from 'node:fs/promises'
import { dirname, isAbsolute, join, normalize } from 'node:path'
import { fileURLToPath } from 'node:url'

export const defaultManifestPath = fileURLToPath(
  new URL('../../tests/Fixtures/Audiobooks/import-folders.json', import.meta.url),
)

const notAudio = new Set(['import-folders.json', 'README.md'])

const leavesRoot = (path) =>
  isAbsolute(path) || normalize(path).split(/[\\/]/).includes('..') || path.includes('\\')

/** Reads and checks the manifest; the fixtures are the files next to it. */
export async function readImportFolders(manifestPath = defaultManifestPath) {
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
  const fixtures = new Set(
    (await readdir(dirname(manifestPath))).filter((name) => !notAudio.has(name)),
  )
  for (const fixture of Object.keys(manifest.fixtures)) {
    if (!fixtures.has(fixture)) throw new Error(`fixtures.${fixture}: no file ${fixture} next to ${manifestPath}`)
  }
  for (const fixture of fixtures) {
    if (!(fixture in manifest.fixtures)) throw new Error(`${fixture} is not listed in fixtures of ${manifestPath}`)
  }
  const used = new Set()
  for (const [root, books] of Object.entries(manifest.roots)) {
    const owners = new Map()
    for (const [key, book] of Object.entries(books)) {
      const id = `${root}.${key}`
      if (leavesRoot(book.folder)) throw new Error(`${id}: folder ${book.folder} must stay inside its root`)
      const folder = normalize(book.folder)
      if (owners.has(folder)) throw new Error(`${id}: folder ${book.folder} is already ${owners.get(folder)}'s`)
      owners.set(folder, id)
      for (const file of book.files) {
        if (file.name.includes('/') || file.name.includes('\\') || file.name === '..') {
          throw new Error(`${id}: file ${file.name} must be a plain file name`)
        }
        if (!(file.fixture in manifest.fixtures)) {
          throw new Error(`${id}: fixture ${file.fixture} is not listed in fixtures`)
        }
        used.add(file.fixture)
      }
    }
  }
  for (const fixture of Object.keys(manifest.fixtures)) {
    if (!used.has(fixture)) throw new Error(`fixtures.${fixture} is not used by any book`)
  }
  return manifest
}

/**
 * Writes <outputDirectory>/<root>/<book folder>/<file> for every book, and every root, even empty.
 * The output folder is made 0755: it is copied as the container's /e2e, and a mkdtemp folder is 0700.
 */
export async function writeImportFolders(outputDirectory, { manifestPath = defaultManifestPath } = {}) {
  const manifest = await readImportFolders(manifestPath)
  await chmod(outputDirectory, 0o755)
  for (const [root, books] of Object.entries(manifest.roots)) {
    await mkdir(join(outputDirectory, root), { recursive: true })
    for (const book of Object.values(books)) {
      const folder = join(outputDirectory, root, book.folder)
      await mkdir(folder, { recursive: true })
      for (const file of book.files) {
        await copyFile(join(dirname(manifestPath), file.fixture), join(folder, file.name))
      }
    }
  }
}
