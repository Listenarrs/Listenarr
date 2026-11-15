import { expect, test } from '@playwright/test'
import type { ListenarrDomainConfigurationApplicationSettings } from '../../../cypress/support/generated-api/types.gen.js'

test('writes settings and reads the persisted API contract back', async ({
  request,
}) => {
  const settingsResponse = await request.get('/api/v1/configuration/settings')
  expect(settingsResponse.status()).toBe(200)
  const settings =
    (await settingsResponse.json()) as ListenarrDomainConfigurationApplicationSettings
  expect(typeof settings.enableNotifications).toBe('boolean')
  const enableNotifications = !settings.enableNotifications

  const tokenResponse = await request.get('/api/v1/antiforgery/token')
  expect(tokenResponse.status()).toBe(200)
  const { token } = (await tokenResponse.json()) as { token: string }

  const saveResponse = await request.post('/api/v1/configuration/settings', {
    data: { ...settings, enableNotifications },
    headers: { 'X-XSRF-TOKEN': token },
  })
  expect(saveResponse.status()).toBe(200)
  const savedSettings =
    (await saveResponse.json()) as ListenarrDomainConfigurationApplicationSettings
  expect(savedSettings.enableNotifications).toBe(enableNotifications)

  const persistedResponse = await request.get('/api/v1/configuration/settings')
  expect(persistedResponse.status()).toBe(200)
  const persistedSettings =
    (await persistedResponse.json()) as ListenarrDomainConfigurationApplicationSettings
  expect(persistedSettings.enableNotifications).toBe(enableNotifications)
})
