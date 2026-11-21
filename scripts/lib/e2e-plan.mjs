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

/** HEAD-derived tags: 12 lowercase hex characters (see imageTags). Run-specific tags never match. */
const headTag = /^[0-9a-f]{12}$/

/**
 * The HEAD-tagged images a run removes so they don't pile up on a long-lived Docker daemon: every
 * `${name}-app` / `${name}-host` tag except `current` and the newest `keep - 1` others. `images` are
 * `docker image ls` rows ({ repository, tag, createdAt }); a zero or invalid `keep` prunes nothing.
 */
export function imagesToPrune(images, { current, keep, name = 'listenarr-e2e' }) {
  if (!Number.isInteger(keep) || keep < 1) return []
  const repositories = [`${name}-app`, `${name}-host`]
  const created = (value) => Date.parse(String(value).replace(/ [A-Z]{2,5}$/, ''))
  const newest = new Map()
  for (const { repository, tag, createdAt } of images) {
    if (!repositories.includes(repository) || !headTag.test(tag)) continue
    newest.set(tag, Math.max(newest.get(tag) ?? -Infinity, created(createdAt)))
  }
  const others = [...newest.keys()].filter((tag) => tag !== current).sort((a, b) => newest.get(b) - newest.get(a))
  const removed = new Set(others.slice(keep - (newest.has(current) ? 1 : 0)))
  return [...removed].flatMap((tag) =>
    repositories
      .filter((repository) => images.some((image) => image.repository === repository && image.tag === tag))
      .map((repository) => `${repository}:${tag}`),
  )
}

/**
 * The number of stacks (and Playwright shards) when neither --stacks=N nor LISTENARR_E2E_STACKS says.
 * Each stack runs an app container, a Vite server and a Chromium: on a 4-vCPU GitHub-hosted runner
 * three of them only trade the test-time gain for start-up time (measured), so they pay off from 8 CPUs.
 */
export function defaultStackCount(cpus) {
  return Number.isFinite(cpus) && cpus >= 8 ? 3 : 1
}
