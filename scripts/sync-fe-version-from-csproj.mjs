import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)
const repoRoot = path.resolve(__dirname, '..')

const csprojPath = path.join(repoRoot, 'listenarr.api', 'Listenarr.Api.csproj')
const rootPackagePath = path.join(repoRoot, 'package.json')
const rootLockPath = path.join(repoRoot, 'package-lock.json')
const fePackagePath = path.join(repoRoot, 'fe', 'package.json')
const feLockPath = path.join(repoRoot, 'fe', 'package-lock.json')
const checkOnly = process.argv.includes('--check') || process.argv.includes('--verify')

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'))
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, 'utf8')
}

function readEnvVersion() {
  const value = process.env.NEW_VERSION?.trim()
  return value ? value : null
}

function readCsprojVersion(csprojText) {
  for (const tagName of ['Version', 'AssemblyVersion']) {
    const match = csprojText.match(new RegExp(`<${tagName}>([^<]+)</${tagName}>`, 'i'))
    const value = match?.[1]?.trim()

    if (value) {
      return value
    }
  }

  return null
}

function syncPackageVersion(packagePath, lockPath, targetVersion, label) {
  const pkg = readJson(packagePath)
  const lock = readJson(lockPath)
  let changed = false

  if (pkg.version !== targetVersion) {
    pkg.version = targetVersion
    changed = true
  }

  if (lock.version !== targetVersion) {
    lock.version = targetVersion
    changed = true
  }

  if (lock.packages && lock.packages[''] && lock.packages[''].version !== targetVersion) {
    lock.packages[''].version = targetVersion
    changed = true
  }

  if (changed) {
    if (checkOnly) {
      console.error(`[version-sync] ${label} package metadata is not synced to ${targetVersion}`)
    } else {
      writeJson(packagePath, pkg)
      writeJson(lockPath, lock)
      console.log(`[version-sync] Updated ${label} package versions to ${targetVersion}`)
    }
  } else {
    console.log(`[version-sync] ${label} package versions already ${targetVersion}`)
  }

  return changed
}

const envVersion = readEnvVersion()
const csprojText = envVersion ? null : fs.readFileSync(csprojPath, 'utf8')
const csprojVersion = envVersion ?? readCsprojVersion(csprojText)

if (!csprojVersion) {
  console.error(
    `[version-sync] Could not resolve version from NEW_VERSION or from <Version>/<AssemblyVersion> in ${csprojPath}`,
  )
  process.exit(1)
}

const rootChanged = syncPackageVersion(rootPackagePath, rootLockPath, csprojVersion, 'root')
const feChanged = syncPackageVersion(fePackagePath, feLockPath, csprojVersion, 'FE')

if (!rootChanged && !feChanged) {
  console.log(`[version-sync] No package metadata changes needed`)
}

if (checkOnly && (rootChanged || feChanged)) {
  console.error(`[version-sync] Run npm run version:sync and commit the package metadata updates.`)
  process.exit(1)
}
