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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import Checkbox from '@/components/form/Checkbox.vue'

describe('SearchSettingsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings for checkboxes and numeric inputs', async () => {
    const { default: SearchSettingsSection } =
      await import('@/components/settings/SearchSettingsSection.vue')
    const wrapper = mount(SearchSettingsSection, {
      props: { settings: { enableOpenLibrarySearch: false } },
      global: { components: { Checkbox } },
    })

    const checks = wrapper.findAll('input[type="checkbox"]')
    await checks[0].setValue(true)
    const last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.enableOpenLibrarySearch).toBe(true)
  })
})
