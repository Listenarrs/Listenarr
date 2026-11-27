import assert from 'node:assert/strict'
import { test } from 'node:test'
import { imageTags, stackCount, stackPlan } from './e2e-plan.mjs'

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

test('stackCount runs the full suite on three stacks by default', () => {
  assert.equal(stackCount({ playwrightArguments: [] }), 3)
})

test('stackCount reads LISTENARR_E2E_STACKS for the full suite', () => {
  assert.equal(stackCount({ playwrightArguments: [], environmentStacks: '2' }), 2)
})

test('stackCount runs one stack when Playwright arguments select tests', () => {
  // A filter can select fewer tests than there are shards; an empty shard fails its coverage teardown.
  assert.equal(stackCount({ playwrightArguments: ['--grep', 'a link from Settings'] }), 1)
  assert.equal(stackCount({ playwrightArguments: ['library-import-scan.spec.ts'], environmentStacks: '3' }), 1)
})

test('stackCount keeps an explicit --stacks, with or without Playwright arguments', () => {
  assert.equal(stackCount({ stacks: '2', playwrightArguments: [] }), 2)
  assert.equal(stackCount({ stacks: '3', playwrightArguments: ['--repeat-each', '3'] }), 3)
})

test('stackCount refuses explicit shards for a coverage run given Playwright arguments', () => {
  assert.throws(
    () => stackCount({ stacks: '3', coverage: true, playwrightArguments: ['--grep', 'a link from Settings'] }),
    /--stacks=1/,
  )
  assert.equal(stackCount({ stacks: '1', coverage: true, playwrightArguments: ['--grep', 'x'] }), 1)
  assert.equal(stackCount({ stacks: '3', coverage: true, playwrightArguments: [] }), 3)
})

test('stackCount serves one stack only', () => {
  assert.equal(stackCount({ serve: true, playwrightArguments: [] }), 1)
  assert.throws(() => stackCount({ serve: true, stacks: '2', playwrightArguments: [] }), /--serve/)
})
