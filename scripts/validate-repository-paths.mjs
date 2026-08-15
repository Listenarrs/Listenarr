import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const windowsReservedBasename =
  /^(?:CON|PRN|AUX|NUL|COM(?:[1-9]|[¹²³])|LPT(?:[1-9]|[¹²³]))$/iu
const windowsInvalidSegmentCharacters = /[<>:"\\|?*\u0000-\u001f]/u

export function isUnsafeRepositorySegment(segment) {
  if (
    segment.length === 0 ||
    windowsInvalidSegmentCharacters.test(segment) ||
    segment !== segment.replace(/[ .]+$/u, '')
  ) {
    return true
  }

  const basename = segment.split('.', 1)[0]
  return windowsReservedBasename.test(basename)
}

export function findUnsafeRepositoryPaths(paths) {
  return paths.filter((path) =>
    path.split('/').some(isUnsafeRepositorySegment)
  )
}

function listRepositoryPaths() {
  const output = execFileSync(
    'git',
    ['ls-files', '--cached', '--others', '--exclude-standard', '-z'],
    { encoding: 'buffer' }
  )

  return output
    .toString('utf8')
    .split('\0')
    .filter((path) => path.length > 0)
}

function main() {
  const unsafePaths = findUnsafeRepositoryPaths(listRepositoryPaths())
  if (unsafePaths.length === 0) {
    console.log('Repository paths are cross-platform safe.')
    return
  }

  console.error('Repository contains Windows-unsafe path names:')
  for (const path of unsafePaths) {
    console.error(`- ${path}`)
  }
  process.exitCode = 1
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main()
}
