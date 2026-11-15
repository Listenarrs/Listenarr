import type { ListenarrDomainConfigurationApplicationSettings } from '../../support/generated-api/types.gen.js'
import { antiforgeryHeaders } from '../support/stack.js'
import { expect, test } from '../support/write-test.js'

type Settings = ListenarrDomainConfigurationApplicationSettings

test.describe('Backend integration write path', () => {
  test('saves a settings change and reads it back', async ({ request }) => {
    const settingsResponse = await request.get('/api/v1/configuration/settings')
    expect(settingsResponse.status()).toBe(200)
    const settings = (await settingsResponse.json()) as Settings
    expect(settings.enableNotifications).toBe(false)

    const saveResponse = await request.post('/api/v1/configuration/settings', {
      data: { ...settings, enableNotifications: true },
      headers: await antiforgeryHeaders(request),
    })
    expect(saveResponse.status()).toBe(200)
    expect(((await saveResponse.json()) as Settings).enableNotifications).toBe(true)

    const persistedResponse = await request.get('/api/v1/configuration/settings')
    expect(persistedResponse.status()).toBe(200)
    expect(((await persistedResponse.json()) as Settings).enableNotifications).toBe(true)
  })

  test('restores seeded settings and books before the next write test', async ({ request }) => {
    const settings = (await (await request.get('/api/v1/configuration/settings')).json()) as Settings
    expect(settings.enableNotifications).toBe(false)

    expect(await (await request.get('/api/v1/library')).json()).toHaveLength(4)
  })
})
