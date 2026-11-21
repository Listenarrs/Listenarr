import assert from 'node:assert/strict'
import { test } from 'node:test'
import { containersToRemove, imagesToPrune, imageTags, stackPlan } from './e2e-plan.mjs'

test('imageTags reuses a stable tag for a clean checkout', () => {
  assert.deepEqual(imageTags({ head: 'ad49a3446eecef', dirty: false, runId: '123-xyz' }), {
    appImage: 'listenarr-e2e-app:ad49a3446eec',
    hostImage: 'listenarr-e2e-host:ad49a3446eec',
    reusable: true,
  })
})

test('imageTags uses the run id, without reuse, for a dirty checkout', () => {
  assert.deepEqual(imageTags({ head: 'ad49a3446eecef', dirty: true, runId: '123-xyz' }), {
    appImage: 'listenarr-e2e-app:123-xyz',
    hostImage: 'listenarr-e2e-host:123-xyz',
    reusable: false,
  })
})

test('imageTags uses the run id when there is no commit', () => {
  assert.equal(imageTags({ head: '', dirty: false, runId: '123-xyz' }).reusable, false)
  assert.equal(imageTags({ head: '', dirty: false, runId: '123-xyz' }).appImage, 'listenarr-e2e-app:123-xyz')
})

test('stackPlan gives one unsharded stack by default', () => {
  const plan = stackPlan({ head: 'ad49a3446eecef', dirty: false, runId: '123-xyz' })
  assert.equal(plan.reusable, true)
  assert.deepEqual(plan.stacks, [
    {
      name: 'listenarr-e2e',
      shard: [],
      backendCoverageDirectory: null,
      frontendCoverageDirectory: null,
    },
  ])
})

test('stackPlan builds independent names, shards and coverage directories', () => {
  const plan = stackPlan({
    head: 'ad49a3446eecef',
    dirty: false,
    runId: '123-xyz',
    count: 3,
    coverage: true,
    coverageRoot: '/fe/test-results',
  })
  assert.deepEqual(
    plan.stacks.map((stack) => stack.name),
    ['listenarr-e2e-0', 'listenarr-e2e-1', 'listenarr-e2e-2'],
  )
  assert.deepEqual(
    plan.stacks.map((stack) => stack.shard),
    [['--shard', '1/3'], ['--shard', '2/3'], ['--shard', '3/3']],
  )
  assert.deepEqual(
    plan.stacks.map((stack) => stack.backendCoverageDirectory),
    [
      '/fe/test-results/e2e-coverage-backend/raw/0',
      '/fe/test-results/e2e-coverage-backend/raw/1',
      '/fe/test-results/e2e-coverage-backend/raw/2',
    ],
  )
  assert.deepEqual(
    plan.stacks.map((stack) => stack.frontendCoverageDirectory),
    [
      '/fe/test-results/e2e-coverage/frontend/0',
      '/fe/test-results/e2e-coverage/frontend/1',
      '/fe/test-results/e2e-coverage/frontend/2',
    ],
  )
})

test('stackPlan rejects a shard count below one', () => {
  assert.throws(() => stackPlan({ head: 'abc', dirty: false, runId: 'x', count: 0 }), /count/)
})

const image = (repository, tag, createdAt) => ({ repository, tag, createdAt })
const pair = (tag, createdAt) => [
  image('listenarr-e2e-app', tag, createdAt),
  image('listenarr-e2e-host', tag, createdAt),
]
const tags = ['aaaaaaaaaaaa', 'bbbbbbbbbbbb', 'cccccccccccc', 'dddddddddddd', 'eeeeeeeeeeee']

test('imagesToPrune removes the oldest HEAD-tagged pairs beyond the kept count', () => {
  const images = tags.flatMap((tag, index) => pair(tag, `2026-11-2${index} 10:00:00 +0000 UTC`))
  assert.deepEqual(imagesToPrune(images, { current: 'eeeeeeeeeeee', keep: 3 }), [
    'listenarr-e2e-app:bbbbbbbbbbbb',
    'listenarr-e2e-host:bbbbbbbbbbbb',
    'listenarr-e2e-app:aaaaaaaaaaaa',
    'listenarr-e2e-host:aaaaaaaaaaaa',
  ])
})

test('imagesToPrune always keeps the current tag, even when it is the oldest', () => {
  const images = tags.flatMap((tag, index) => pair(tag, `2026-11-2${index} 10:00:00 +0000 UTC`))
  assert.deepEqual(imagesToPrune(images, { current: 'aaaaaaaaaaaa', keep: 2 }), [
    'listenarr-e2e-app:dddddddddddd',
    'listenarr-e2e-host:dddddddddddd',
    'listenarr-e2e-app:cccccccccccc',
    'listenarr-e2e-host:cccccccccccc',
    'listenarr-e2e-app:bbbbbbbbbbbb',
    'listenarr-e2e-host:bbbbbbbbbbbb',
  ])
})

test('imagesToPrune removes nothing when no more than the kept count exist', () => {
  const images = tags.slice(0, 3).flatMap((tag, index) => pair(tag, `2026-11-2${index} 10:00:00 +0000 UTC`))
  assert.deepEqual(imagesToPrune(images, { current: 'cccccccccccc', keep: 3 }), [])
})

test('imagesToPrune never touches run-specific or other tags', () => {
  const images = [
    ...pair('aaaaaaaaaaaa', '2026-11-20 10:00:00 +0000 UTC'),
    ...pair('bbbbbbbbbbbb', '2026-11-21 10:00:00 +0000 UTC'),
    image('listenarr-e2e-app', '1732183200000-ab12cd', '2026-11-01 10:00:00 +0000 UTC'),
    image('listenarr-e2e-app', 'latest', '2026-11-01 10:00:00 +0000 UTC'),
    image('listenarr-e2e-app', 'ABCDEFABCDEF', '2026-11-01 10:00:00 +0000 UTC'),
    image('listenarr-other', 'cccccccccccc', '2026-11-01 10:00:00 +0000 UTC'),
  ]
  assert.deepEqual(imagesToPrune(images, { current: 'bbbbbbbbbbbb', keep: 1 }), [
    'listenarr-e2e-app:aaaaaaaaaaaa',
    'listenarr-e2e-host:aaaaaaaaaaaa',
  ])
})

test('imagesToPrune is disabled by a zero or invalid kept count', () => {
  const images = tags.flatMap((tag, index) => pair(tag, `2026-11-2${index} 10:00:00 +0000 UTC`))
  for (const keep of [0, -1, 1.5, Number.NaN]) {
    assert.deepEqual(imagesToPrune(images, { current: 'eeeeeeeeeeee', keep }), [], `keep=${keep}`)
  }
})

test('imagesToPrune removes the half of an old pair that is left', () => {
  const images = [
    image('listenarr-e2e-host', 'aaaaaaaaaaaa', '2026-11-19 10:00:00 +0000 UTC'),
    ...pair('bbbbbbbbbbbb', '2026-11-21 10:00:00 +0000 UTC'),
  ]
  assert.deepEqual(imagesToPrune(images, { current: 'bbbbbbbbbbbb', keep: 1 }), ['listenarr-e2e-host:aaaaaaaaaaaa'])
})

const now = Date.parse('2026-11-21T20:00:00Z')
const container = (name, state, createdAt = '2026-11-20 20:41:03 +0000 UTC') => ({ name, state, createdAt })

test('containersToRemove removes old stopped stack containers, sharded or single', () => {
  const containers = [
    container('listenarr-e2e-2-1562042-mwp7zdpr', 'exited'),
    container('listenarr-e2e-1562042-mwp7zdpr', 'exited'),
    container('listenarr-e2e-0-1562042-mwp7zdpr', 'created'),
    container('listenarr-e2e-1-1562042-mwp7zdpr', 'dead'),
  ]
  assert.deepEqual(containersToRemove(containers, { now }), [
    'listenarr-e2e-2-1562042-mwp7zdpr',
    'listenarr-e2e-1562042-mwp7zdpr',
    'listenarr-e2e-0-1562042-mwp7zdpr',
    'listenarr-e2e-1-1562042-mwp7zdpr',
  ])
})

test('containersToRemove keeps running, restarting and paused stack containers, even old ones', () => {
  const containers = ['running', 'restarting', 'paused'].map((state) =>
    container(`listenarr-e2e-0-42-${state}`, state),
  )
  assert.deepEqual(containersToRemove(containers, { now }), [])
})

test('containersToRemove keeps a stopped stack container younger than the threshold', () => {
  const containers = [container('listenarr-e2e-0-42-abc', 'exited', '2026-11-21 19:00:00 +0000 UTC')]
  assert.deepEqual(containersToRemove(containers, { now }), [])
  assert.deepEqual(containersToRemove(containers, { now, minAgeMs: 30 * 60_000 }), ['listenarr-e2e-0-42-abc'])
})

test('containersToRemove keeps containers that are not E2E stacks', () => {
  const containers = [
    container('vsc-listenarr-cffefa42ec2b-uid', 'exited'),
    container('listenarr-e2e-app', 'exited'),
    container('my-listenarr-e2e-1-2-x', 'exited'),
    container('listenarr-e2e-1-2-x-extra', 'exited'),
    container('listenarr-e2e-abc-2-x', 'exited'),
  ]
  assert.deepEqual(containersToRemove(containers, { now }), [])
})

test('containersToRemove keeps a container whose creation date cannot be read', () => {
  assert.deepEqual(containersToRemove([container('listenarr-e2e-0-42-abc', 'exited', 'yesterday')], { now }), [])
})

test('containersToRemove returns nothing for no containers', () => {
  assert.deepEqual(containersToRemove([], { now }), [])
})
