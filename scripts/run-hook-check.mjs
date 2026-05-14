#!/usr/bin/env node
import { spawnSync } from 'node:child_process'

const checks = {
  'pre-commit': [
    {
      label: 'Running staged lint and format checks...',
      command: process.execPath,
      args: ['scripts/lint-staged.mjs'],
    },
    {
      label: 'Checking layering rules and async void...',
      command: process.execPath,
      args: ['scripts/check-architecture.mjs'],
    },
  ],
  'pre-push': [
    {
      label: 'Verifying version metadata...',
      command: process.execPath,
      args: ['scripts/sync-fe-version-from-csproj.mjs', '--check'],
    },
    {
      label: 'Checking backend format...',
      command: 'dotnet',
      args: [
        'format',
        'listenarr.slnx',
        '--no-restore',
        '--verify-no-changes',
        '--verbosity',
        'minimal',
      ],
    },
    {
      label: 'Running frontend type check...',
      command: process.execPath,
      args: ['node_modules/vue-tsc/bin/vue-tsc.js', '--build', 'tsconfig.app.json'],
      cwd: 'fe',
    },
    {
      label: 'Running frontend tests...',
      command: process.execPath,
      args: ['node_modules/vitest/vitest.mjs', 'run'],
      cwd: 'fe',
    },
  ],
}

const mode = process.argv[2]
const selectedChecks = checks[mode]

if (!selectedChecks) {
  console.error(`Unknown hook check "${mode}". Expected one of: ${Object.keys(checks).join(', ')}`)
  process.exit(1)
}

for (const check of selectedChecks) {
  console.log(check.label)

  const result = spawnSync(check.command, check.args, {
    cwd: check.cwd,
    stdio: 'inherit',
    shell: false,
  })

  if (result.error) {
    console.error(result.error.message)
    process.exit(1)
  }

  if (result.status !== 0) {
    process.exit(result.status ?? 1)
  }
}
