import { test as base } from '@playwright/test'
import { restoreSeed } from '../../support/docker-stack.js'

/** The `test` of the write suite: every test starts from the seeded database and files. */
export const test = base.extend<{ seededStack: void }>({
  seededStack: [
    async ({}, use) => {
      await restoreSeed()
      await use()
    },
    { auto: true, timeout: 180_000 },
  ],
})

export { expect } from '@playwright/test'
