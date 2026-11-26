// Pure planning helpers for the parallel browser E2E runner (scripts/e2e/run.mjs).
// Kept separate from the Docker and Playwright code so shard/image/coverage decisions are testable.
import { join } from 'node:path'

/** Stable tags let a self-hosted runner skip image builds on unchanged checkouts. */
export function imageTags({ head, dirty, runId, name = 'listenarr-e2e' }) {
  const reusable = !dirty && Boolean(head)
  const tag = reusable ? head.slice(0, 12) : runId
  return { appImage: `${name}-app:${tag}`, hostImage: `${name}-host:${tag}`, reusable }
}

/**
 * One stack per Playwright shard. Each stack owns its container, ports, seeded directories and,
 * with coverage on, its own report directories; images are shared.
 */
export function stackPlan({
  head,
  dirty,
  runId,
  name = 'listenarr-e2e',
  count = 1,
  coverage = false,
  coverageRoot = '',
}) {
  if (!Number.isInteger(count) || count < 1) throw new Error(`Invalid stack count: ${count}.`)
  const { appImage, hostImage, reusable } = imageTags({ head, dirty, runId, name })
  return {
    appImage,
    hostImage,
    reusable,
    stacks: Array.from({ length: count }, (_, index) => ({
      name: count === 1 ? name : `${name}-${index}`,
      shard: count === 1 ? [] : ['--shard', `${index + 1}/${count}`],
      backendCoverageDirectory: coverage
        ? join(coverageRoot, 'e2e-coverage-backend', 'raw', String(index))
        : null,
      frontendCoverageDirectory: coverage
        ? join(coverageRoot, 'e2e-coverage', 'frontend', String(index))
        : null,
    })),
  }
}

/**
 * How many stacks (Playwright shards) a run starts. The full suite runs on three, or
 * LISTENARR_E2E_STACKS. Playwright arguments (a spec, --grep) can select fewer tests than there are
 * shards, and an empty shard fails its coverage teardown, so with any the default is one stack. An
 * explicit --stacks=N wins, except for a coverage run given Playwright arguments, which must use one.
 * --serve keeps one stack up.
 */
export function stackCount({ stacks, environmentStacks, serve = false, coverage = false, playwrightArguments = [] }) {
  if (serve) {
    if (stacks !== undefined && Number(stacks) !== 1) throw new Error('--serve works with one stack only (use --stacks=1).')
    return 1
  }
  if (stacks !== undefined) {
    const count = Number(stacks)
    if (coverage && playwrightArguments.length && count !== 1) {
      throw new Error('A coverage run given Playwright arguments can leave a shard without tests: use --stacks=1.')
    }
    return count
  }
  if (playwrightArguments.length) return 1
  return Number(environmentStacks ?? 3)
}
