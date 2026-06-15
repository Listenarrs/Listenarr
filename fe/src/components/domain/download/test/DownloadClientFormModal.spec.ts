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
import { beforeEach, describe, it, expect, vi } from 'vitest'
import { nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import DownloadClientFormModal from '@/components/domain/download/DownloadClientFormModal.vue'
import { modalStubs } from '@/test/stubs'

const testDownloadClientMock = vi.hoisted(() => vi.fn())

vi.mock('@/services/api', () => ({
  testDownloadClient: testDownloadClientMock,
}))

describe('DownloadClientFormModal', () => {
  beforeEach(() => {
    testDownloadClientMock.mockReset()
  })

  it('renders password input for qbittorrent', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()], stubs: modalStubs },
      props: { visible: true, editingClient: null },
    })

    // Provide an editingClient prop to initialize formData for qbittorrent
    await wrapper.setProps({
      editingClient: {
        id: '1',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'qbittorrent.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings: {},
      },
    })
    await wrapper.vm.$nextTick()

    const passwordComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(passwordComponent.exists()).toBe(true)
  })

  it('renders api key input for sabnzbd', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()], stubs: modalStubs },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '2',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sab.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings: {},
      },
    })
    await wrapper.vm.$nextTick()

    const apiKeyComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(apiKeyComponent.exists()).toBe(true)
  })

  it('test button on modal uses current input values and includes ID for existing client fallback', async () => {
    testDownloadClientMock.mockImplementationOnce(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()], stubs: modalStubs },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '3',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'original.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        settings: {},
        password: 'dbpass',
      },
    })
    await wrapper.vm.$nextTick()

    // change host input to a new value before testing
    const hostInput = wrapper.find('input[id="host"]')
    await hostInput.setValue('http://edited.local/nzbget')

    // click the Test button (use class selector to reliably find the correct button)
    const testButton = wrapper.find('button.btn-info')
    expect(testButton.exists()).toBe(true)
    await testButton.trigger('click')

    expect(testDownloadClientMock).toHaveBeenCalled()
    const calledWith = testDownloadClientMock.mock.calls[0][0]
    expect(calledWith.host).toBe('edited.local')
    // Existing client id should be sent so backend can reuse saved credentials when needed.
    expect(calledWith.id).toBe('3')
  })

  it('modal sends existing client ID when password is cleared so backend can pull saved password', async () => {
    testDownloadClientMock.mockImplementationOnce(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()], stubs: modalStubs },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '4',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'host.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        settings: {},
        password: 'dbpass',
      },
    })
    await wrapper.vm.$nextTick()

    const passwordComponent = wrapper.findComponent({ name: 'PasswordInput' })
    expect(passwordComponent.exists()).toBe(true)
    // prepopulated value should match DB via v-model prop
    expect(passwordComponent.props('modelValue')).toBe('dbpass')

    // clear the password input by emitting v-model update
    await (passwordComponent.vm as any).$emit('update:modelValue', '')
    await nextTick()

    // click Test
    const testButton = wrapper.find('button.btn-info')
    await testButton.trigger('click')

    expect(testDownloadClientMock).toHaveBeenCalled()
    const calledWith = testDownloadClientMock.mock.calls[0][0]
    // We still send an empty password input, but include id so backend can reuse saved credentials.
    expect(calledWith.password).toBe('')
    expect(calledWith.id).toBe('4')
  })
})
