import assert from 'node:assert/strict'
import test from 'node:test'
import {
  chooseRunner,
  workflowJobs,
  findFork,
  formatJobs,
  main,
  parseArgs,
  snapshotBranch
} from './run-tests.mjs'

const runner = (name, status, labels = []) => ({
  name,
  status,
  labels: [
    { name: 'self-hosted', type: 'read-only' },
    { name: 'Linux', type: 'read-only' },
    ...labels.map((label) => ({ name: label, type: 'custom' }))
  ]
})

test('parseArgs: defaults', () => {
  assert.deepEqual(parseArgs([]), {
    runner: '',
    prefer: [],
    fork: '',
    dir: '.',
    workflow: 'run-tests.yml',
    jobs: '',
    tag: '',
    ref: '',
    keep: false,
    wait: true,
    inputs: [],
    help: false
  })
})

test('parseArgs: every option', () => {
  const options = parseArgs([
    '--runner', 'box', '--prefer', 'a,b', '--fork', 'me/Listenarr', '--repo', '/src',
    '--workflow', 'other.yml', '--jobs', 'unit-tests,e2e-tests', '--tag', 't1', '--ref', 'feature/x',
    '--keep-branch', '--input', 'coverage=true', '--input', 'x=a=b'
  ])
  assert.equal(options.runner, 'box')
  assert.deepEqual(options.prefer, ['a', 'b'])
  assert.equal(options.fork, 'me/Listenarr')
  assert.equal(options.dir, '/src')
  assert.equal(options.workflow, 'other.yml')
  assert.equal(options.jobs, 'unit-tests,e2e-tests')
  assert.equal(options.tag, 't1')
  assert.equal(options.ref, 'feature/x')
  assert.equal(options.keep, true)
  assert.deepEqual(options.inputs, ['coverage=true', 'x=a=b'])
})

test('parseArgs: --no-wait keeps the snapshot branch', () => {
  const options = parseArgs(['--no-wait'])
  assert.equal(options.wait, false)
  assert.equal(options.keep, true)
})

test('parseArgs: preferred runners from LISTENARR_CI_RUNNERS', () => {
  assert.deepEqual(parseArgs([], { LISTENARR_CI_RUNNERS: 'a, b' }).prefer, ['a', 'b'])
  assert.deepEqual(parseArgs(['--prefer', 'c'], { LISTENARR_CI_RUNNERS: 'a' }).prefer, ['c'])
})

test('parseArgs: usage errors', () => {
  assert.throws(() => parseArgs(['--input', 'novalue']), { exitCode: 2 })
  assert.throws(() => parseArgs(['--bogus']), { exitCode: 2 })
  assert.throws(() => parseArgs(['--runner']), { exitCode: 2 })
  assert.equal(parseArgs(['--help']).help, true)
})

test('findFork: the GitHub remote owned by the signed-in user', () => {
  const remotes = [
    'origin\thttps://github.com/Listenarrs/Listenarr.git (fetch)',
    'origin\thttps://github.com/Listenarrs/Listenarr.git (push)',
    'mine\tgit@github.com:Someone/Listenarr.git (fetch)',
    'mine\tgit@github.com:Someone/Listenarr.git (push)'
  ].join('\n')
  assert.deepEqual(findFork(remotes, 'someone'), {
    fork: 'Someone/Listenarr',
    url: 'git@github.com:Someone/Listenarr.git'
  })
})

test('findFork: an explicit fork keeps its remote URL, or uses the HTTPS one', () => {
  const remotes = 'fork\tssh://git@github.com/me/Listenarr (push)'
  assert.deepEqual(findFork(remotes, 'other', 'me/Listenarr'), {
    fork: 'me/Listenarr',
    url: 'ssh://git@github.com/me/Listenarr'
  })
  assert.deepEqual(findFork('', 'other', 'me/Listenarr'), {
    fork: 'me/Listenarr',
    url: 'https://github.com/me/Listenarr.git'
  })
})

test('findFork: no fork of the user is a usage error', () => {
  const remotes = 'origin\thttps://github.com/Listenarrs/Listenarr.git (push)'
  assert.throws(() => findFork(remotes, 'me'), { exitCode: 2, message: /--fork/ })
})

test('chooseRunner: github needs no runner', () => {
  assert.deepEqual(chooseRunner([], 'github', []), { name: 'github', label: '' })
})

test('chooseRunner: the first preferred runner that is online, by its custom label', () => {
  const runners = [runner('a', 'offline', ['ci-a']), runner('b', 'online', ['ci-b']), runner('c', 'online', ['ci-c'])]
  assert.deepEqual(chooseRunner(runners, '', ['a', 'c', 'b']), { name: 'c', label: 'ci-c' })
})

test('chooseRunner: without preferences, any online runner; its name when it has no custom label', () => {
  assert.deepEqual(chooseRunner([runner('a', 'offline'), runner('b', 'online')], '', []), { name: 'b', label: 'b' })
})

test('chooseRunner: nothing online exits 4', () => {
  assert.throws(() => chooseRunner([runner('a', 'offline')], '', []), { exitCode: 4, message: /--runner github/ })
  assert.throws(() => chooseRunner([], '', []), { exitCode: 4 })
})

test('chooseRunner: a runner named by its name or one of its labels', () => {
  const runners = [runner('a', 'online', ['ci-a']), runner('b', 'offline', ['ci-b'])]
  assert.deepEqual(chooseRunner(runners, 'a', []), { name: 'a', label: 'ci-a' })
  assert.deepEqual(chooseRunner(runners, 'ci-a', []), { name: 'a', label: 'ci-a' })
  assert.throws(() => chooseRunner(runners, 'b', []), { exitCode: 4, message: /not online/ })
  assert.throws(() => chooseRunner(runners, 'zz', []), { exitCode: 2, message: /no runner/ })
})

test('snapshotBranch: the current branch, made safe, plus the tag', () => {
  assert.equal(snapshotBranch('feature/a b', ''), 'ci/wip-feature-a-b')
  assert.equal(snapshotBranch('main', 'x1'), 'ci/wip-main-x1')
})

test('formatJobs: one line per job that ran, with minutes when known', () => {
  const lines = formatJobs([
    { name: 'unit-tests', conclusion: 'success', startedAt: '2026-01-01T10:00:00Z', completedAt: '2026-01-01T10:12:59Z' },
    { name: 'e2e-tests', conclusion: 'skipped' },
    { name: 'coverage', conclusion: '', status: 'in_progress', startedAt: '2026-01-01T10:00:00Z' },
    { name: 'e2e', conclusion: '', status: 'in_progress', startedAt: '2026-01-01T10:00:00Z', completedAt: '0001-01-01T00:00:00Z' }
  ])
  assert.deepEqual(lines, ['SUCCESS unit-tests (12 min)', 'IN_PROGRESS coverage', 'IN_PROGRESS e2e'])
})

test('workflowJobs: the job ids of a workflow file', () => {
  const workflow = [
    'name: Run Tests',
    'on:',
    '  workflow_dispatch:',
    '    inputs:',
    '      jobs:',
    '        type: string',
    'jobs:',
    '  unit-tests:',
    '    runs-on: x',
    '    steps:',
    '      - run: echo',
    '  # a comment',
    '  e2e-tests:  # trailing comment',
    '    runs-on: x',
    ''
  ].join('\n')
  assert.deepEqual(workflowJobs(workflow), ['unit-tests', 'e2e-tests'])
})

// A fake of git and gh: answers the calls main() makes and records them.
function fakeEnvironment ({ dirty = true, online = true, refCommit = 'abc123def456', conclusion = 0, skipped = false } = {}) {
  const calls = []
  const output = []
  const ok = (stdout = '') => ({ code: 0, stdout, stderr: '' })
  const answer = (cmd, args) => {
    const line = [cmd, ...args].join(' ')
    if (line === 'git rev-parse --show-toplevel') return ok('/src\n')
    if (line === 'git remote -v') return ok('fork\tgit@github.com:me/Listenarr.git (push)\n')
    if (line === 'gh api user --jq .login') return ok('me\n')
    if (line.startsWith('gh api repos/me/Listenarr/actions/runners')) {
      return ok(JSON.stringify({ runners: [runner('box', online ? 'online' : 'offline', ['ci-box'])] }))
    }
    if (line === 'git rev-parse --abbrev-ref HEAD') return ok('feature/x\n')
    if (line.startsWith('git rev-parse --path-format=absolute')) return ok('/src/.git/index\n')
    if (line === 'git add -A') return ok()
    if (line === 'git write-tree') return ok(dirty ? 'tree2\n' : 'tree1\n')
    if (line === 'git rev-parse HEAD^{tree}') return ok('tree1\n')
    if (line === 'git rev-parse HEAD') return ok('head000000000\n')
    if (line.startsWith('git commit-tree')) return ok('snap000000000\n')
    if (line.startsWith('git ls-remote')) return ok(refCommit ? `${refCommit}\trefs/heads/feature/x\n` : '')
    if (line.startsWith('gh run list')) {
      const sha = dirty ? 'snap000000000' : 'head000000000'
      return ok(JSON.stringify([{ databaseId: 42, headSha: args.includes('feature/x') ? refCommit : sha, createdAt: '2999-01-01T00:00:00Z' }]))
    }
    if (line.startsWith('gh run watch')) return { code: conclusion, stdout: '', stderr: '' }
    if (line.startsWith('gh run view') && args.includes('--log-failed')) return ok('job\tstep\t2026-01-01T00:00:00.0000000Z boom\n')
    if (line.startsWith('gh run view')) {
      const result = skipped ? 'skipped' : conclusion ? 'failure' : 'success'
      return ok(JSON.stringify({ conclusion: result, jobs: [{ name: 'unit-tests', conclusion: result }] }))
    }
    return ok()
  }
  const deps = {
    run: async (cmd, args, options = {}) => {
      calls.push({ cmd, args, env: options.env })
      return answer(cmd, args)
    },
    log: (text) => output.push(text),
    error: (text) => output.push(text),
    sleep: async () => {},
    env: {},
    tempFile: () => '/tmp/index-copy',
    readFile: (file) => {
      calls.push({ cmd: 'read', args: [file] })
      return 'jobs:\n  unit-tests:\n    x: 1\n  e2e-tests:\n    x: 1\n'
    },
    copyFile: () => {},
    removeFile: () => {}
  }
  const called = (prefix) => calls.filter((call) => [call.cmd, ...call.args].join(' ').startsWith(prefix))
  return { deps, calls, output, called }
}

test('main: snapshot of a dirty tree, on the online runner, then the branch is deleted', async () => {
  const fake = fakeEnvironment()
  const code = await main(['--jobs', 'unit-tests', '--input', 'coverage=true'], fake.deps)
  assert.equal(code, 0)
  const [add] = fake.called('git add -A')
  assert.equal(add.env.GIT_INDEX_FILE, '/tmp/index-copy')
  assert.deepEqual(fake.called('git push -q -f')[0].args, ['push', '-q', '-f', 'git@github.com:me/Listenarr.git', 'snap000000000:refs/heads/ci/wip-feature-x'])
  const [dispatch] = fake.called('gh workflow run')
  assert.deepEqual(dispatch.args, ['workflow', 'run', 'run-tests.yml', '-R', 'me/Listenarr', '--ref', 'ci/wip-feature-x', '-f', 'runner=ci-box', '-f', 'jobs=unit-tests', '-f', 'coverage=true'])
  assert.equal(fake.called('gh run watch').length, 1)
  assert.deepEqual(fake.called('git push -q git@github.com:me/Listenarr.git :refs/heads/ci/wip-feature-x').length, 1)
  assert.ok(fake.output.includes('SUCCESS unit-tests'))
  assert.ok(fake.output.includes('CI RUN: PASS'))
  assert.ok(fake.output.includes('run: https://github.com/me/Listenarr/actions/runs/42'))
})

test('main: a clean tree runs HEAD itself', async () => {
  const fake = fakeEnvironment({ dirty: false })
  assert.equal(await main([], fake.deps), 0)
  assert.equal(fake.called('git commit-tree').length, 0)
  assert.equal(fake.called('git push -q -f')[0].args.at(-1), 'head000000000:refs/heads/ci/wip-feature-x')
})

test('main: a failed run exits 1 and prints the failed steps', async () => {
  const fake = fakeEnvironment({ conclusion: 1 })
  assert.equal(await main([], fake.deps), 1)
  assert.ok(fake.output.includes('FAILURE unit-tests'))
  assert.ok(fake.output.some((line) => line.includes('boom')))
  assert.ok(fake.output.includes('CI RUN: FAIL'))
})

test('main: --ref runs the pushed branch, no snapshot pushed or deleted', async () => {
  const fake = fakeEnvironment()
  assert.equal(await main(['--ref', 'feature/x', '--runner', 'github'], fake.deps), 0)
  assert.equal(fake.called('git push').length, 0)
  assert.equal(fake.called('gh api repos/me/Listenarr/actions/runners').length, 0)
  const [dispatch] = fake.called('gh workflow run')
  assert.deepEqual(dispatch.args, ['workflow', 'run', 'run-tests.yml', '-R', 'me/Listenarr', '--ref', 'feature/x'])
})

test('main: --ref of a branch that is not pushed exits 2', async () => {
  const fake = fakeEnvironment({ refCommit: '' })
  assert.equal(await main(['--ref', 'feature/x', '--runner', 'github'], fake.deps), 2)
  assert.ok(fake.output.some((line) => /push it first/.test(line)))
  assert.equal(fake.called('gh workflow run').length, 0)
})

test('main: --no-wait leaves the run going and keeps the snapshot branch', async () => {
  const fake = fakeEnvironment()
  assert.equal(await main(['--no-wait', '--tag', 't1'], fake.deps), 0)
  assert.equal(fake.called('gh run watch').length, 0)
  assert.equal(fake.called('git push -q git@github.com:me/Listenarr.git :refs').length, 0)
  assert.equal(fake.called('git push -q -f')[0].args.at(-1), 'snap000000000:refs/heads/ci/wip-feature-x-t1')
})

test('main: --keep-branch keeps the snapshot branch after the run', async () => {
  const fake = fakeEnvironment()
  assert.equal(await main(['--keep-branch'], fake.deps), 0)
  assert.equal(fake.called('gh run watch').length, 1)
  assert.equal(fake.called('git push -q git@github.com:me/Listenarr.git :refs').length, 0)
})

test('main: no runner online exits 4 before pushing anything', async () => {
  const fake = fakeEnvironment({ online: false })
  assert.equal(await main([], fake.deps), 4)
  assert.equal(fake.called('git push').length, 0)
})

test('main: --jobs naming no job of the workflow exits 2 before pushing anything', async () => {
  const fake = fakeEnvironment()
  assert.equal(await main(['--jobs', 'unit-tests,unit-test'], fake.deps), 2)
  assert.ok(fake.output.some((line) => /unit-test\b.*unit-tests, e2e-tests/.test(line)))
  assert.deepEqual(fake.called('read')[0].args, ['/src/.github/workflows/run-tests.yml'])
  assert.equal(fake.called('git push').length, 0)
})

test('main: a run where no job ran says so and exits 1', async () => {
  const fake = fakeEnvironment({ skipped: true })
  assert.equal(await main([], fake.deps), 1)
  assert.ok(fake.output.includes('CI RUN: NO JOB RAN'))
  assert.equal(fake.called('gh run view').filter((call) => call.args.includes('--log-failed')).length, 0)
})

test('main: Ctrl+C while waiting cancels the run, deletes the snapshot branch and exits 130', async () => {
  const fake = fakeEnvironment()
  let interrupt
  fake.deps.onInterrupt = (handler) => { interrupt = handler }
  const run = fake.deps.run
  fake.deps.run = async (cmd, args, options) => {
    if (args[1] === 'watch') await interrupt()
    return run(cmd, args, options)
  }
  assert.equal(await main([], fake.deps), 130)
  assert.deepEqual(fake.called('gh run cancel')[0].args, ['run', 'cancel', '42', '-R', 'me/Listenarr'])
  assert.ok(fake.output.includes('CI RUN: CANCELLED'))
  assert.equal(fake.called('git push -q git@github.com:me/Listenarr.git :refs').length, 1)
  assert.equal(fake.called('gh run view').length, 0)
})

test('main: usage errors exit 2, --help exits 0', async () => {
  assert.equal(await main(['--bogus'], fakeEnvironment().deps), 2)
  const fake = fakeEnvironment()
  assert.equal(await main(['--help'], fake.deps), 0)
  assert.ok(fake.output.some((line) => line.includes('Usage')))
})
