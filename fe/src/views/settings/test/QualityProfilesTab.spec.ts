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
import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { baseStubs } from '@/test/stubs'
import { flushAsync } from '@/test/utils/wait'

describe('QualityProfilesTab', () => {
  it('shows loading state while fetching profiles', async () => {
    vi.resetModules()
    const pinia = createPinia()
    setActivePinia(pinia)

    let resolveFn: (value: unknown) => void = () => {}
    vi.doMock('@/services/api', async (importOriginal) => ({
      ...((await importOriginal()) as object),
      getQualityProfiles: vi.fn(
        () =>
          new Promise((res) => {
            resolveFn = res
          }),
      ),
    }))

    const QualityProfilesTab = (await import('@/views/settings/QualityProfilesTab.vue')).default
    const wrapper = mount(QualityProfilesTab, {
      global: { plugins: [pinia], stubs: baseStubs },
    })

    // debug: inspect rendered HTML during pending state
    await wrapper.vm.$nextTick()
    // console.log(wrapper.html())

    // initial pending state should show a loading indicator
    expect(wrapper.find('.loading-state').exists()).toBe(true)

    // resolve API and assert empty-state appears
    resolveFn([])
    await flushAsync()
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.empty-state').exists()).toBe(true)
  })
})
