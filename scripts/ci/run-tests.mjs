#!/usr/bin/env node
// Runs the "Run Tests" workflow on your own fork of Listenarr, on your own self-hosted runner or on
// GitHub's, for the working tree as it is now or for a branch already pushed. See USAGE below and
// "Running CI on your fork" in CONTRIBUTING.md. Needs git, and the GitHub CLI (gh) signed in.
import { spawn } from 'node:child_process'
import { copyFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

export const USAGE = `Usage: node scripts/ci/run-tests.mjs [options]

Pushes a snapshot of the working tree (uncommitted and untracked files included, HEAD and the
index untouched) to a throwaway branch ci/wip-<branch> of your fork, runs the workflow there,
waits, prints one line per job and the failed steps' logs, then deletes the branch.

  --runner <name|label|github>  where the Linux jobs run: one of your fork's self-hosted runners,
                    by name or label, or "github" for GitHub-hosted runners. Default: the first
                    online runner of --prefer, else any online self-hosted runner of the fork.
  --prefer <a,b>    runner names in order of preference (default: $LISTENARR_CI_RUNNERS)
  --fork <owner/repo>  default: the GitHub remote of this checkout owned by the gh user
  --jobs <a,b>      only these jobs (default: all), e.g. --jobs unit-tests,e2e-tests
  --input <name=value>  another workflow input, repeatable, e.g. --input coverage=true
  --ref <branch>    run this branch as already pushed to the fork, no snapshot: the run stays in
                    the branch's Actions history (the working tree is ignored; push first)
  --tag <t>         suffix of the snapshot branch, to run several snapshots at once
  --keep-branch     keep the snapshot branch after the run
  --no-wait         print the run's URL and return (keeps the snapshot branch)
  --workflow <file> default: run-tests.yml
  --repo <path>     the checkout, default: the current directory

Exit: 0 the run passed, 1 it failed, 2 usage, 4 no runner online.`

class ExitError extends Error {
  constructor (exitCode, message) {
    super(message)
    this.exitCode = exitCode
  }
}

export function parseArgs (argv, env = {}) {
  const options = {
    runner: '',
    prefer: splitList(env.LISTENARR_CI_RUNNERS),
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
  }
  const valued = { '--runner': 'runner', '--fork': 'fork', '--repo': 'dir', '--workflow': 'workflow', '--jobs': 'jobs', '--tag': 'tag', '--ref': 'ref' }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    const takeValue = () => {
      const value = argv[++i]
      if (value === undefined || value.startsWith('--')) throw new ExitError(2, `${arg} takes a value`)
      return value
    }
    if (valued[arg]) options[valued[arg]] = takeValue()
    else if (arg === '--prefer') options.prefer = splitList(takeValue())
    else if (arg === '--input') {
      const value = takeValue()
      if (!/^[^=]+=/.test(value)) throw new ExitError(2, '--input takes name=value')
      options.inputs.push(value)
    } else if (arg === '--keep-branch') options.keep = true
    else if (arg === '--no-wait') { options.wait = false; options.keep = true } else if (arg === '-h' || arg === '--help') options.help = true
    else throw new ExitError(2, `unknown option ${arg} (--help for the usage)`)
  }
  return options
}

function splitList (value) {
  return (value ?? '').split(',').map((item) => item.trim()).filter(Boolean)
}

// The fork to run on and the URL to push to, from `git remote -v`.
export function findFork (remotes, login, fork = '') {
  const pushRemotes = remotes.split('\n')
    .map((line) => line.match(/^\S+\s+(\S+)\s+\(push\)$/))
    .filter(Boolean)
    .map(([, url]) => ({ url, slug: url.match(/github\.com[:/]([^/]+\/[^/]+?)(?:\.git)?\/?$/)?.[1] }))
    .filter((remote) => remote.slug)
  if (fork) {
    const remote = pushRemotes.find((candidate) => candidate.slug.toLowerCase() === fork.toLowerCase())
    return { fork, url: remote?.url ?? `https://github.com/${fork}.git` }
  }
  const own = pushRemotes.find((remote) => remote.slug.split('/')[0].toLowerCase() === login.toLowerCase())
  if (!own) throw new ExitError(2, `no remote of this checkout is a GitHub repository of ${login}: add your fork as a remote, or pass --fork <owner/repo>`)
  return { fork: own.slug, url: own.url }
}

// The runner label the workflow's "runner" input gets ('' for GitHub-hosted runners).
export function chooseRunner (runners, wanted, prefer) {
  if (wanted === 'github') return { name: 'github', label: '' }
  const labelOf = (runner) => runner.labels.find((label) => label.type === 'custom')?.name ?? runner.name
  const online = runners.filter((runner) => runner.status === 'online')
  if (wanted) {
    const runner = runners.find((candidate) => candidate.name === wanted) ??
      runners.find((candidate) => candidate.labels.some((label) => label.type === 'custom' && label.name === wanted))
    if (!runner) throw new ExitError(2, `no runner or runner label "${wanted}" on the fork (or --runner github)`)
    if (runner.status !== 'online') throw new ExitError(4, `runner ${runner.name} is not online`)
    return { name: runner.name, label: wanted === runner.name ? labelOf(runner) : wanted }
  }
  const ordered = prefer.length ? prefer.map((name) => online.find((runner) => runner.name === name)).filter(Boolean) : online
  if (!ordered.length) throw new ExitError(4, 'no self-hosted runner of the fork is online (use --runner github for GitHub-hosted runners)')
  return { name: ordered[0].name, label: labelOf(ordered[0]) }
}

export function snapshotBranch (current, tag) {
  return `ci/wip-${current.replace(/[^A-Za-z0-9._-]/g, '-')}${tag ? `-${tag}` : ''}`
}

export function formatJobs (jobs) {
  return jobs.filter((job) => job.conclusion !== 'skipped').map((job) => {
    const start = Date.parse(job.startedAt ?? '')
    const end = Date.parse(job.completedAt ?? '')
    const minutes = Number.isNaN(start) || Number.isNaN(end) ? '' : ` (${Math.floor((end - start) / 60000)} min)`
    return `${(job.conclusion || job.status).toUpperCase()} ${job.name}${minutes}`
  })
}

export async function main (argv, deps = defaultDeps()) {
  try {
    return await runTests(parseArgs(argv, deps.env), deps)
  } catch (error) {
    deps.error(error.message)
    return error.exitCode ?? 1
  }
}

async function runTests (options, deps) {
  if (options.help) {
    deps.log(USAGE)
    return 0
  }
  const sh = async (cmd, args, extra = {}) => {
    const result = await deps.run(cmd, args, extra)
    if (result.code !== 0) throw new ExitError(1, `${cmd} ${args.join(' ')} failed:\n${result.stderr.trim()}`)
    return result.stdout.trim()
  }
  const top = await sh('git', ['rev-parse', '--show-toplevel'], { cwd: options.dir })
  const git = (args, extra = {}) => sh('git', args, { cwd: top, ...extra })
  const login = options.fork ? '' : await sh('gh', ['api', 'user', '--jq', '.login'])
  const { fork, url } = findFork(await git(['remote', '-v']), login, options.fork)

  const runners = options.runner === 'github'
    ? []
    : JSON.parse(await sh('gh', ['api', `repos/${fork}/actions/runners?per_page=100`])).runners
  const runner = chooseRunner(runners, options.runner, options.prefer)

  let branch, commit
  let cleanup = async () => {}
  if (options.ref) {
    branch = options.ref
    commit = (await git(['ls-remote', url, `refs/heads/${branch}`])).split('\t')[0]
    if (!commit) throw new ExitError(2, `branch ${branch} is not on ${fork} (push it first)`)
    deps.log(`branch ${fork}@${branch} at ${commit.slice(0, 9)}, runner: ${runner.name}`)
  } else {
    // A commit of the working tree on top of HEAD, written through a copy of the index.
    branch = snapshotBranch(await git(['rev-parse', '--abbrev-ref', 'HEAD']), options.tag)
    const index = deps.tempFile()
    deps.copyFile(await git(['rev-parse', '--path-format=absolute', '--git-path', 'index']), index)
    try {
      const env = { GIT_INDEX_FILE: index }
      await git(['add', '-A'], { env })
      const tree = await git(['write-tree'], { env })
      commit = tree === await git(['rev-parse', 'HEAD^{tree}'])
        ? await git(['rev-parse', 'HEAD'])
        : await git(['commit-tree', tree, '-p', 'HEAD', '-m', 'CI snapshot of the working tree'])
    } finally {
      deps.removeFile(index)
    }
    await git(['push', '-q', '-f', url, `${commit}:refs/heads/${branch}`])
    cleanup = async () => {
      if (!options.keep) await deps.run('git', ['push', '-q', url, `:refs/heads/${branch}`], { cwd: top })
    }
    deps.log(`snapshot ${commit.slice(0, 9)} on ${fork}@${branch}, runner: ${runner.name}`)
  }

  try {
    const before = new Date().toISOString().replace(/\.\d+Z$/, 'Z')
    const inputs = [
      ...(runner.label ? [`runner=${runner.label}`] : []),
      ...(options.jobs ? [`jobs=${options.jobs}`] : []),
      ...options.inputs
    ].flatMap((input) => ['-f', input])
    await sh('gh', ['workflow', 'run', options.workflow, '-R', fork, '--ref', branch, ...inputs])

    let run
    for (let attempt = 0; attempt < 30 && !run; attempt++) {
      if (attempt) await deps.sleep(2000)
      const runs = JSON.parse(await sh('gh', ['run', 'list', '-R', fork, '--workflow', options.workflow, '--branch', branch,
        '--event', 'workflow_dispatch', '--json', 'databaseId,headSha,createdAt']))
      run = runs.find((candidate) => candidate.headSha === commit && candidate.createdAt >= before)?.databaseId
    }
    if (!run) throw new ExitError(1, 'the run did not appear on GitHub')
    deps.log(`run: https://github.com/${fork}/actions/runs/${run}`)
    if (!options.wait) return 0

    deps.onInterrupt?.(() => deps.run('gh', ['run', 'cancel', String(run), '-R', fork]))
    const watched = await deps.run('gh', ['run', 'watch', String(run), '-R', fork, '--interval', '15', '--exit-status'])
    const { jobs } = JSON.parse(await sh('gh', ['run', 'view', String(run), '-R', fork, '--json', 'jobs']))
    for (const line of formatJobs(jobs)) deps.log(line)
    if (watched.code === 0) {
      deps.log('CI RUN: PASS')
      return 0
    }
    const logs = (await deps.run('gh', ['run', 'view', String(run), '-R', fork, '--log-failed'])).stdout
    const tail = Number(deps.env.TAIL) || 60
    for (const line of logs.trimEnd().split('\n').slice(-tail)) deps.log(line.split('\t').slice(2).join('\t').slice(29))
    deps.log('CI RUN: FAIL')
    return 1
  } finally {
    await cleanup()
  }
}

function defaultDeps () {
  const children = new Set()
  return {
    env: process.env,
    log: (text) => console.log(text),
    error: (text) => console.error(text),
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    tempFile: () => join(tmpdir(), `listenarr-ci-index-${process.pid}`),
    copyFile: copyFileSync,
    removeFile: (file) => rmSync(file, { force: true }),
    run: (cmd, args, { cwd, env } = {}) => new Promise((resolve) => {
      const child = spawn(cmd, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] })
      children.add(child)
      let stdout = ''
      let stderr = ''
      child.stdout.on('data', (chunk) => { stdout += chunk })
      child.stderr.on('data', (chunk) => { stderr += chunk })
      child.on('error', (error) => resolve({ code: 127, stdout, stderr: `${cmd}: ${error.message}` }))
      child.on('close', (code) => { children.delete(child); resolve({ code: code ?? 1, stdout, stderr }) })
    }),
    // Ctrl+C while waiting cancels the run; the snapshot branch is deleted once the watch ends.
    onInterrupt: (cancel) => process.once('SIGINT', async () => {
      console.error('interrupted: cancelling the run')
      await cancel()
      for (const child of children) child.kill()
      process.exitCode = 130
    })
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const code = await main(process.argv.slice(2))
  if (process.exitCode !== 130) process.exitCode = code
}
