import fs from 'node:fs'
import path from 'node:path'

const root = process.cwd()
const srcRoot = path.join(root, 'src')
const failures = []

function walk(dir) {
  if (!fs.existsSync(dir)) return []

  const entries = fs.readdirSync(dir, { withFileTypes: true })
  return entries.flatMap((entry) => {
    const fullPath = path.join(dir, entry.name)
    if (entry.isDirectory()) return walk(fullPath)
    return fullPath
  })
}

function rel(file) {
  return path.relative(root, file).replaceAll(path.sep, '/')
}

const files = walk(srcRoot)
const testFilePattern = /\.(spec|test)\.(ts|tsx|js|jsx)$/
const sharedTestBuckets = new Set(['app', 'framework', 'smoke'])
const vitestConfigPattern = /^vitest(?:\..+)?\.config\.ts$/
const allowedSetupFile = 'src/test/setup/signalr.ts'
const allowedSetupFiles = new Set([allowedSetupFile])

if (fs.existsSync(path.join(srcRoot, '__tests__'))) {
  failures.push('src/__tests__ must not exist; colocate tests in src/**/test/.')
}

if (fs.existsSync(path.join(srcRoot, 'test', 'setup.ts'))) {
  failures.push('src/test/setup.ts must not exist; use explicit setup files under src/test/setup/.')
}

const setupRoot = path.join(srcRoot, 'test', 'setup')
for (const setupFile of walk(setupRoot)) {
  const relative = rel(setupFile)
  if (!allowedSetupFiles.has(relative)) {
    failures.push(`${relative} is not an allowed Vitest setup file.`)
  }
}

for (const file of files) {
  const relative = rel(file)

  if (testFilePattern.test(file)) {
    if (!relative.includes('/test/')) {
      failures.push(`${relative} is outside a colocated test/ folder.`)
    }

    if (relative.startsWith('src/test/')) {
      const bucket = relative.split('/')[2]
      if (!sharedTestBuckets.has(bucket)) {
        failures.push(
          `${relative} is under src/test; only app, framework, and smoke specs may live there.`,
        )
      }
    }
  }

  if (/\.test\.(ts|tsx|js|jsx)$/.test(file)) {
    failures.push(`${relative} uses .test.*; use .spec.ts for frontend tests.`)
  }

  if (/\.(spec|test)\.(tsx|js|jsx)$/.test(file)) {
    failures.push(`${relative} is not a .spec.ts test file.`)
  }
}

const vitestConfigFiles = fs
  .readdirSync(root, { withFileTypes: true })
  .filter((entry) => entry.isFile() && vitestConfigPattern.test(entry.name))
  .map((entry) => entry.name)

for (const configName of vitestConfigFiles) {
  const configPath = path.join(root, configName)
  const content = fs.readFileSync(configPath, 'utf8')
  if (
    content.includes('setupFiles') &&
    !/setupFiles:\s*\[\s*['"]src\/test\/setup\/signalr\.ts['"]\s*\]/.test(content)
  ) {
    failures.push(`${configName} may only configure setupFiles for ${allowedSetupFile}.`)
  }
}

if (failures.length > 0) {
  console.error('Frontend test structure check failed:')
  for (const failure of failures) {
    console.error(`- ${failure}`)
  }
  process.exit(1)
}

console.log('Frontend test structure check passed.')
