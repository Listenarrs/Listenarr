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
import { createPinia } from 'pinia'
import { ModalFooter } from '@/components/feedback'
import type { Indexer } from '@/types'

const updateIndexer = vi.fn()

vi.mock('@/services/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/services/api')>()),
  updateIndexer: (...args: unknown[]) => updateIndexer(...args),
}))

const { default: IndexerFormModal } = await import('@/components/settings/IndexerFormModal.vue')

const editingIndexer = {
  id: 7,
  name: 'Usenet',
  type: 'Usenet',
  implementation: 'Newznab',
  url: 'https://indexer.invalid',
  apiKey: 'secret',
  enableRss: true,
  enableAutomaticSearch: true,
  enableInteractiveSearch: true,
  enableAnimeStandardSearch: false,
  isEnabled: true,
  priority: 25,
  minimumAge: 0,
  retention: 0,
  maximumSize: 0,
  dailyRequestLimit: 2000,
  createdAt: '',
  updatedAt: '',
} as Indexer

const mountEditing = async () => {
  const wrapper = mount(IndexerFormModal, {
    global: { plugins: [createPinia()] },
    props: { visible: true, editingIndexer: null },
  })
  await wrapper.setProps({ editingIndexer })
  await wrapper.vm.$nextTick()
  return wrapper
}

const lastPayload = () => updateIndexer.mock.lastCall?.[1] as Partial<Indexer>

describe('IndexerFormModal daily request limit', () => {
  beforeEach(() => {
    updateIndexer.mockReset()
    updateIndexer.mockResolvedValue(editingIndexer)
  })

  it('loads the stored limit and saves it back unchanged', async () => {
    const wrapper = await mountEditing()
    const input = wrapper.find<HTMLInputElement>('#dailyRequestLimit')
    expect(input.element.value).toBe('2000')

    wrapper.findComponent(ModalFooter).vm.$emit('save')
    await vi.waitFor(() => expect(updateIndexer).toHaveBeenCalled())

    expect(updateIndexer.mock.lastCall?.[0]).toBe(7)
    expect(lastPayload().dailyRequestLimit).toBe(2000)
  })

  it('sends a cleared limit as null, meaning unlimited', async () => {
    const wrapper = await mountEditing()
    await wrapper.find('#dailyRequestLimit').setValue('')

    wrapper.findComponent(ModalFooter).vm.$emit('save')
    await vi.waitFor(() => expect(updateIndexer).toHaveBeenCalled())

    expect(lastPayload().dailyRequestLimit).toBeNull()
  })
})
