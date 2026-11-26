import assert from 'node:assert/strict'
import { test } from 'node:test'
import { listContainers } from './listenarr-stack.mjs'

test('listContainers asks docker ps not to compute container sizes', () => {
  // `--format '{{json .}}'` makes docker ps compute every container's filesystem size, which takes
  // seconds on a daemon with many containers; --size=false skips it.
  let call
  const exec = (command, args) => {
    call = { command, args }
    return ''
  }
  listContainers(exec)
  assert.equal(call.command, 'docker')
  assert.deepEqual(call.args, ['ps', '--all', '--size=false', '--format', '{{json .}}'])
})

test('listContainers maps each docker ps line to a name, state and creation time', () => {
  const stack = 'listenarr-e2e-1-42-1700000000000'
  const created = '2026-11-20 10:00:00 +0000 UTC'
  const exec = () =>
    [
      JSON.stringify({ Names: stack, State: 'exited', CreatedAt: created, Size: '0B' }),
      JSON.stringify({ Names: 'other', State: 'running', CreatedAt: created }),
      '',
    ].join('\n')
  assert.deepEqual(listContainers(exec), [
    { name: stack, state: 'exited', createdAt: created },
    { name: 'other', state: 'running', createdAt: created },
  ])
})
