import { spawnSync } from 'node:child_process'
import path from 'node:path'

const localBin = (packageName, binPath) => path.join('node_modules', packageName, binPath)

const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: false,
    ...options,
  })

  if (result.error) {
    console.error(`Failed to run "${command}": ${result.error.message}`)
    process.exit(1)
  }

  if (result.status !== 0) {
    process.exit(result.status ?? 1)
  }
}

run('node', ['scripts/validate-repository-paths.mjs'])

const stagedResult = spawnSync('git', ['diff', '--cached', '--name-only', '--diff-filter=ACMR'], {
  encoding: 'utf8',
  shell: false,
})

if (stagedResult.error) {
  console.error(stagedResult.error.message)
  process.exit(1)
}

if (stagedResult.status !== 0) {
  process.stderr.write(stagedResult.stderr)
  process.exit(stagedResult.status ?? 1)
}

const stagedFiles = stagedResult.stdout
  .split(/\r?\n/)
  .map((file) => file.trim())
  .filter(Boolean)

const backendFiles = stagedFiles.filter((file) => file.endsWith('.cs'))
const frontendLintFiles = stagedFiles
  .filter((file) => /^fe\/.+\.(?:js|jsx|mjs|cjs|ts|tsx|mts|cts|vue)$/.test(file))
  .map((file) => file.slice('fe/'.length))
const frontendVueFiles = stagedFiles
  .filter((file) => /^fe\/.+\.vue$/.test(file))
  .map((file) => file.slice('fe/'.length))
const frontendFormatFiles = stagedFiles
  .filter((file) => /^fe\/src\/.+\.(?:js|jsx|mjs|cjs|ts|tsx|mts|cts|vue|css|scss|sass|less|styl)$/.test(file))
  .map((file) => file.slice('fe/'.length))

if (backendFiles.length === 0 && frontendLintFiles.length === 0 && frontendFormatFiles.length === 0) {
  console.log('No staged lintable files found.')
  process.exit(0)
}

if (backendFiles.length > 0) {
  console.log('Checking staged C# formatting...')
  run('dotnet', [
    'format',
    'listenarr.slnx',
    '--no-restore',
    '--verify-no-changes',
    '--verbosity',
    'minimal',
    '--include',
    ...backendFiles,
  ])
}

if (frontendLintFiles.length > 0) {
  console.log('Checking staged frontend lint rules...')
  run('node', [localBin('eslint', 'bin/eslint.js'), ...frontendLintFiles], { cwd: 'fe' })
}

if (frontendVueFiles.length > 0) {
  console.log('Checking staged Vue template handlers...')
  run('node', ['scripts/check-vue-template-handlers.mjs', ...frontendVueFiles], { cwd: 'fe' })
}

if (frontendFormatFiles.length > 0) {
  console.log('Checking staged frontend formatting...')
  run(
    'node',
    [localBin('prettier', 'bin/prettier.cjs'), '--check', ...frontendFormatFiles],
    { cwd: 'fe' },
  )
}
