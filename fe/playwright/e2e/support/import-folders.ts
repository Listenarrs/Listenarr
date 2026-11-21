import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * The audiobook folders the runner writes for the Library Import specs, read from the same manifest
 * (tests/Fixtures/Audiobooks/import-folders.json, written by scripts/e2e/import-folders.mjs), so the
 * specs name a book by its key instead of repeating its folder and file names.
 */
type Manifest = {
  roots: Record<
    string,
    Record<string, { folder: string; files: { name: string; fixture: string }[] }>
  >
}

export type ImportBook = {
  /** The root folder the book is in: `import`, `second`, ... (mounted as /e2e/<root>). */
  root: string
  /** The book's folder inside its root: `Edgar Rice Burroughs/A Princess of Mars`. */
  path: string
  /** The last part of that folder, which identifies the book's row: `A Princess of Mars`. */
  folderName: string
  /** The folder in the container: `/e2e/import/Edgar Rice Burroughs/A Princess of Mars`. */
  directory: string
  /** The file names in that folder. */
  fileNames: string[]
  /** The files in the container. */
  files: string[]
}

const manifest: Manifest = JSON.parse(
  readFileSync(
    fileURLToPath(
      new URL('../../../../tests/Fixtures/Audiobooks/import-folders.json', import.meta.url),
    ),
    'utf8',
  ),
)

const books = new Map<string, ImportBook>()
for (const [root, rootBooks] of Object.entries(manifest.roots)) {
  for (const [key, entry] of Object.entries(rootBooks)) {
    if (books.has(key)) throw new Error(`import-folders.json: book key ${key} is used in two roots`)
    const directory = `/e2e/${root}/${entry.folder}`
    const fileNames = entry.files.map((file) => file.name)
    books.set(key, {
      root,
      path: entry.folder,
      folderName: entry.folder.split('/').at(-1)!,
      directory,
      fileNames,
      files: fileNames.map((name) => `${directory}/${name}`),
    })
  }
}

/** The book of that key in import-folders.json; an unknown key fails the test with its name. */
export function importBook(key: string): ImportBook {
  const book = books.get(key)
  if (!book) throw new Error(`import-folders.json has no book ${key}`)
  return book
}

/** The folder name of each book, by key: `folder.Princess` is `A Princess of Mars`. */
export const folder: Readonly<Record<string, string>> = new Proxy(
  {},
  { get: (_, key) => importBook(String(key)).folderName },
)

/** The keys of the books in a root folder, in manifest order. */
export const rootBookKeys = (root: string) => Object.keys(manifest.roots[root] ?? {})
