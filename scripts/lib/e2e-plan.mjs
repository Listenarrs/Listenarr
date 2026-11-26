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
