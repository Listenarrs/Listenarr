/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'

// The field is only worth having if the edit reaches the save path. SettingsView
// persists whatever `startupConfig` holds, so the tab has to pass the child's
// change upwards; a section that renders correctly and swallows its own emit
// would look right and save nothing.
describe('GeneralSettingsTab, url base wiring', () => {
  it('passes a url base change up to the parent that saves it', async () => {
    const { default: GeneralSettingsTab } = await import('@/views/settings/GeneralSettingsTab.vue')

    const wrapper = mount(GeneralSettingsTab, {
      props: {
        settings: { downloadPath: '/downloads' },
        startupConfig: { urlBase: '', port: 4545 },
        apiKey: 'key',
        authEnabled: false,
      },
      global: {
        stubs: {
          FileManagementSection: true,
          DownloadSettingsSection: true,
          FeaturesSection: true,
          SearchSettingsSection: true,
          AuthenticationSection: true,
        },
      },
    })

    const input = wrapper.get('#urlBase')
    ;(input.element as HTMLInputElement).value = '/listenarr'
    await input.trigger('change')

    const emitted = wrapper.emitted('update:startupConfig')
    expect(emitted).toBeTruthy()
    expect(emitted![0][0]).toEqual({ urlBase: '/listenarr', port: 4545 })
  })
})
