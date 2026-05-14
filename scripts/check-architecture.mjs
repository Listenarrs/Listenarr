#!/usr/bin/env node
import { spawnSync } from 'node:child_process'

const checks = [
  {
    patterns: ['listenarr.infrastructure', 'Listenarr.Infrastructure'],
    paths: [
      'listenarr.api/**/*.cs',
      ':(exclude)listenarr.api/Program.cs',
      ':(exclude)listenarr.api/GlobalUsings.cs',
    ],
    message: 'Layering violation: listenarr.api references listenarr.infrastructure.',
  },
  {
    patterns: ['listenarr.infrastructure', 'Listenarr.Infrastructure'],
    paths: ['listenarr.application/**/*.cs'],
    message: 'Layering violation: listenarr.application references listenarr.infrastructure.',
  },
  {
    patterns: ['async void'],
    paths: [
      'listenarr.api/**/*.cs',
      'listenarr.application/**/*.cs',
      'listenarr.infrastructure/**/*.cs',
      'listenarr.domain/**/*.cs',
    ],
    message: 'async void found in production code - use async Task instead.',
  },
]

function gitGrep(pattern, paths) {
  const result = spawnSync('git', ['grep', '-n', '--no-color', pattern, '--', ...paths], {
    encoding: 'utf8',
  })

  if (result.error) {
    console.error(result.error.message)
    process.exit(1)
  }

  if (result.status === 0) {
    return result.stdout
  }

  if (result.status === 1) {
    return ''
  }

  if (result.stdout) process.stdout.write(result.stdout)
  if (result.stderr) process.stderr.write(result.stderr)
  process.exit(result.status ?? 1)
}

let violations = 0

for (const check of checks) {
  const matches = new Set()

  for (const pattern of check.patterns) {
    const output = gitGrep(pattern, check.paths)
    for (const line of output.split(/\r?\n/)) {
      if (line) matches.add(line)
    }
  }

  if (matches.size > 0) {
    for (const line of matches) {
      console.log(line)
    }
    console.error(check.message)
    violations += 1
  }
}

if (violations > 0) {
  console.error(`${violations} enforcement check(s) failed.`)
  process.exit(1)
}
