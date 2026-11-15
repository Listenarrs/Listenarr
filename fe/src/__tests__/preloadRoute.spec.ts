import { describe, expect, it, vi } from 'vitest'

vi.mock('@/views/SettingsView.vue', () => {
  throw new Error('Failed to fetch dynamically imported module')
})

describe('preloadRoute', () => {
  it('does not reject when a route chunk cannot be loaded', async () => {
    const { preloadRoute } = await import('@/router')

    await expect(preloadRoute('settings')).resolves.toBeUndefined()
    await expect(preloadRoute('/settings')).resolves.toBeUndefined()
  })

  it('resolves for a route that does not exist', async () => {
    const { preloadRoute } = await import('@/router')

    await expect(preloadRoute('no-such-route')).resolves.toBeUndefined()
  })
})
