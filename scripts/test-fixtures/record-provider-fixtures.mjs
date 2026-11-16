// Records the metadata provider fixtures the test stub lacks (tests/Listenarr.FakeProviders), from
// the real APIs. The stub answers 404 to a request without a fixture and writes the line
//   External API stub: no fixture for GET <url> -> record as <Provider>/<file>
// to stderr, and to the file named by LISTENARR_TEST_MISSING_FIXTURES when that variable is set. This
// script reads those lines, fetches each URL once and saves the answer as
// tests/Listenarr.FakeProviders/Fixtures/<Provider>/<file> (JSON indented, images as bytes).
//
//   LISTENARR_TEST_MISSING_FIXTURES=/tmp/missing.log dotnet test tests/Listenarr.Tests.csproj --filter <test>
//   node scripts/test-fixtures/record-provider-fixtures.mjs /tmp/missing.log
//   node scripts/test-fixtures/record-provider-fixtures.mjs --url <url> <Provider>/<file>
//
// Options: --force overwrites existing fixtures, --dry-run lists what would be recorded. Logs on stdin
// with "-". Review a recorded fixture before committing it: a test asserts on what it contains.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join, normalize, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')
const fixturesRoot = join(repositoryRoot, 'tests', 'Listenarr.FakeProviders', 'Fixtures')
const missingFixtureLine = /no fixture for GET (\S+) -> record as (\S+)/

function usage(message) {
  if (message) console.error(message)
  console.error(
    'usage: record-provider-fixtures.mjs [--force] [--dry-run] <stub log>... | - | --url <url> <Provider>/<file>',
  )
  process.exit(2)
}

function parseArguments(argv) {
  const options = { force: false, dryRun: false, logs: [], urls: [] }
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index]
    if (argument === '--force') options.force = true
    else if (argument === '--dry-run') options.dryRun = true
    else if (argument === '--url') {
      const [url, fixture] = argv.slice(index + 1, index + 3)
      if (!url || !fixture) usage('--url needs a URL and a <Provider>/<file>')
      options.urls.push({ url, fixture })
      index += 2
    } else if (argument === '-h' || argument === '--help') usage()
    else options.logs.push(argument)
  }
  if (options.logs.length === 0 && options.urls.length === 0) usage()
  return options
}

// The fixtures named in the stub's log lines, one per file (the first request naming it wins).
function readMissingFixtures(logs) {
  const fixtures = new Map()
  for (const log of logs) {
    const text = readFileSync(log === '-' ? 0 : log, 'utf8')
    for (const line of text.split(/\r?\n/)) {
      const match = missingFixtureLine.exec(line)
      if (match && !fixtures.has(match[2])) fixtures.set(match[2], match[1])
    }
  }
  return [...fixtures].map(([fixture, url]) => ({ url, fixture }))
}

// <Provider>/<file>, inside the fixtures folder.
function fixturePath(fixture) {
  const path = normalize(join(fixturesRoot, fixture))
  const inside = relative(fixturesRoot, path)
  if (inside.startsWith('..') || inside.split(/[\\/]/).length !== 2) {
    throw new Error(`not a <Provider>/<file> fixture name: ${fixture}`)
  }
  return path
}

async function record({ url, fixture }, { force, dryRun }) {
  const path = fixturePath(fixture)
  const shown = relative(repositoryRoot, path)
  if (existsSync(path) && !force) return console.log(`exists   ${shown}`)
  if (dryRun) return console.log(`would    ${shown} <- ${url}`)

  const response = await fetch(url, {
    headers: { 'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) listenarr-test-fixtures', Accept: '*/*' },
    redirect: 'follow',
    signal: AbortSignal.timeout(30_000),
  })
  if (!response.ok) {
    console.error(`failed   ${shown} <- ${url}: HTTP ${response.status}`)
    return false
  }

  let body = Buffer.from(await response.arrayBuffer())
  if (path.endsWith('.json')) body = `${JSON.stringify(JSON.parse(body.toString('utf8')), null, 2)}\n`

  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, body)
  console.log(`recorded ${shown} <- ${url}`)
  return true
}

const options = parseArguments(process.argv.slice(2))
const fixtures = [...readMissingFixtures(options.logs), ...options.urls]
if (fixtures.length === 0) console.log('No missing fixture in the log.')

let failures = 0
for (const fixture of fixtures) {
  try {
    if ((await record(fixture, options)) === false) failures++
  } catch (error) {
    console.error(`failed   ${fixture.fixture} <- ${fixture.url}: ${error.message}`)
    failures++
  }
}
process.exit(failures === 0 ? 0 : 1)
