// Summarizes the backend coverage of the E2E suite for the library import code: reads the merged
// Cobertura report scripts/e2e/run.mjs writes, keeps the files matching LIBRARY_IMPORT_FILES, and
// prints one line per file plus the total (also written to summary.md and summary.json next to it).
// Reported, not gated: much of that code handles platforms, races and recovery a browser can't reach.
//
//   node scripts/e2e/backend-coverage.mjs <cobertura.xml> [output directory]
import { readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

/** The backend code Library Import runs: the scan, the root folder endpoints, the manual import. */
export const LIBRARY_IMPORT_FILES = [
  /listenarr\.api\/Features\/Library\/RootFoldersController\.cs$/,
  /listenarr\.api\/Features\/Downloads\/ManualImport[^/]*\.cs$/,
  /listenarr\.api\/Dtos\/ManualImport\/[^/]*\.cs$/,
  /listenarr\.infrastructure\/Library\/Scanning\/UnmatchedScan[^/]*\.cs$/,
]

function attribute(tag, name) {
  return new RegExp(`\\b${name}="([^"]*)"`).exec(tag)?.[1]
}

/** Lines per source file, a line covered when any class or method of the report hit it. */
function parseCobertura(xml) {
  const files = new Map()
  const classPattern = /<class\b[^>]*>[\s\S]*?<\/class>/g
  for (const [classXml] of xml.matchAll(classPattern)) {
    const filename = attribute(classXml.slice(0, classXml.indexOf('>') + 1), 'filename')
    if (!filename) continue
    const path = filename.replaceAll('\\', '/')
    const lines = files.get(path) ?? new Map()
    for (const [lineTag] of classXml.matchAll(/<line\b[^>]*\/?>/g)) {
      const number = Number(attribute(lineTag, 'number'))
      const hits = Number(attribute(lineTag, 'hits'))
      lines.set(number, Math.max(lines.get(number) ?? 0, hits))
    }
    files.set(path, lines)
  }
  return files
}

export async function summarizeBackendCoverage(coberturaPath, outputDirectory = dirname(coberturaPath)) {
  const files = parseCobertura(await readFile(coberturaPath, 'utf8'))
  const rows = []
  for (const [path, lines] of files) {
    const relativePath = path.replace(/^.*?(?=listenarr\.(api|application|domain|infrastructure)\/)/, '')
    if (!LIBRARY_IMPORT_FILES.some((pattern) => pattern.test(relativePath))) continue
    const covered = [...lines.values()].filter((hits) => hits > 0).length
    rows.push({ file: relativePath, covered, total: lines.size })
  }
  rows.sort((a, b) => a.file.localeCompare(b.file))
  const covered = rows.reduce((sum, row) => sum + row.covered, 0)
  const total = rows.reduce((sum, row) => sum + row.total, 0)
  const percent = (part, whole) => (whole === 0 ? '100.00' : ((100 * part) / whole).toFixed(2))
  const table = [
    '| File | Lines covered | Lines | % |',
    '| --- | ---: | ---: | ---: |',
    ...rows.map((row) => `| ${row.file} | ${row.covered} | ${row.total} | ${percent(row.covered, row.total)} |`),
    `| **Total** | **${covered}** | **${total}** | **${percent(covered, total)}** |`,
  ].join('\n')
  const summary = `# Backend coverage of the E2E suite: library import code\n\n${table}\n`
  await writeFile(join(outputDirectory, 'summary.md'), summary)
  await writeFile(
    join(outputDirectory, 'summary.json'),
    JSON.stringify({ covered, total, percent: Number(percent(covered, total)), files: rows }, null, 2),
  )
  console.log(`\n${summary}`)
  return { covered, total }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [coberturaPath, outputDirectory] = process.argv.slice(2)
  if (!coberturaPath) throw new Error('Pass the merged Cobertura report.')
  await summarizeBackendCoverage(coberturaPath, outputDirectory)
}
