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
import { nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import DownloadClientFormModal from '@/components/domain/download/DownloadClientFormModal.vue'

describe('DownloadClientFormModal', () => {
  it('renders password input for qbittorrent', async () => {
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
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
      global: { plugins: [createPinia()] },
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
    const api = await import('@/services/api')
    ;(api.testDownloadClient as unknown) = vi.fn(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
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

    expect(api.testDownloadClient as unknown).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as unknown).mock.calls[0][0]
    expect(calledWith.host).toBe('edited.local')
    // Existing client id should be sent so backend can reuse saved credentials when needed.
    expect(calledWith.id).toBe('3')
  })

  it('modal sends existing client ID when password is cleared so backend can pull saved password', async () => {
    const api = await import('@/services/api')
    ;(api.testDownloadClient as unknown) = vi.fn(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
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
    await (passwordComponent.vm as unknown).$emit('update:modelValue', '')
    await nextTick()

    // click Test
    const testButton = wrapper.find('button.btn-info')
    await testButton.trigger('click')

    expect(api.testDownloadClient as unknown).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as unknown).mock.calls[0][0]
    // We still send an empty password input, but include id so backend can reuse saved credentials.
    expect(calledWith.password).toBe('')
    expect(calledWith.id).toBe('4')
  })

  it('offers only priority values the usenet planners accept', async () => {
    // The values here have to stay in step with DownloadClientPriorityTests on the backend.
    // They were not: the form offered default, last and first, none of which match an arm of
    // the switch in NzbgetRequestPlanner or SabnzbdAddRequestPlanner, so every choice but
    // Default resolved to normal and the control did nothing.
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '1',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sabnzbd.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
      } as unknown as import('@/types').DownloadClientConfiguration,
    })
    await nextTick()

    const options = wrapper
      .findAll('#recentPriority option')
      .map((option) => option.attributes('value'))

    expect(options).toEqual(['default', 'low', 'normal', 'high', 'force'])
    expect(options).not.toContain('first')
    expect(options).not.toContain('last')

    wrapper.unmount()
  })

  it('no longer renders the superseded legacy removal checkboxes', async () => {
    // Remove Completed (Legacy) and Remove Failed (Legacy) had no backend reader at all, and
    // the Completed Download Action dropdown above them already covers removing and deleting.
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '1',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sabnzbd.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
      } as unknown as import('@/types').DownloadClientConfiguration,
    })
    await nextTick()

    expect(wrapper.text()).not.toContain('Remove Completed (Legacy)')
    expect(wrapper.text()).not.toContain('Remove Failed (Legacy)')
    // The control: the section that replaced them is still there.
    expect(wrapper.find('#removeCompletedDownloads').exists()).toBe(true)

    wrapper.unmount()
  })

  it.each([
    ['last', 'normal'],
    ['first', 'normal'],
    ['high', 'high'],
    ['', 'default'],
  ])('reads a stored priority of %s back as %s', async (stored, expected) => {
    // Every install that ever moved this control off Default has 'last' or 'first' stored,
    // because those are the only other values the old list offered. Neither is in the new list,
    // and a select whose model holds a value none of its options carry renders with
    // selectedIndex -1 and an empty value, so the operator is shown a blank dropdown by the
    // release that fixed the control. Both fell through to normal on the wire before this
    // change, so normal is what preserves their behaviour.
    //
    // The last two rows are the control: a normaliser that simply returned 'normal' would break
    // the stored value that is already valid and the empty one that has always meant Default.
    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '1',
        name: 'sab',
        type: 'sabnzbd',
        host: 'sabnzbd.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        settings: stored ? { recentPriority: stored } : {},
      } as unknown as import('@/types').DownloadClientConfiguration,
    })
    await nextTick()

    const select = wrapper.find('#recentPriority').element as HTMLSelectElement
    expect(select.value).toBe(expected)
    expect(select.selectedIndex).toBeGreaterThanOrEqual(0)

    wrapper.unmount()
  })

  it('renders URL Base field for qbittorrent and includes it in the test payload when set', async () => {
    const api = await import('@/services/api')
    ;(api.testDownloadClient as unknown) = vi.fn(async (config: unknown) => ({
      success: true,
      message: 'ok',
      client: config,
    }))

    const wrapper = mount(DownloadClientFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingClient: null },
    })

    await wrapper.setProps({
      editingClient: {
        id: '5',
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

    const urlBaseInput = wrapper.find('input[id="urlBase"]')
    expect(urlBaseInput.exists()).toBe(true)

    await urlBaseInput.setValue('/qbittorrent')

    const testButton = wrapper.find('button.btn-info')
    await testButton.trigger('click')

    expect(api.testDownloadClient as unknown).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as unknown).mock.calls[0][0]
    expect(calledWith.settings.urlBase).toBe('/qbittorrent')
  })

  describe('URL Base persistence for qbittorrent', () => {
    const storedClient = (settings: Record<string, unknown>) =>
      ({
        id: '6',
        name: 'qbt',
        type: 'qbittorrent',
        host: 'qbittorrent.local',
        port: 8080,
        isEnabled: true,
        useSSL: false,
        downloadPath: '',
        username: '',
        password: '',
        settings,
      }) as unknown as import('@/types').DownloadClientConfiguration

    // Mounts the form with a save spy in place of the store action, so a submit records the
    // configuration the form would persist without calling the API.
    const mountWithSaveSpy = async (
      editingClient: import('@/types').DownloadClientConfiguration,
    ) => {
      const pinia = createPinia()
      const { useConfigurationStore } = await import('@/stores/configuration')
      const store = useConfigurationStore(pinia)
      const save = vi.fn(async (config: import('@/types').DownloadClientConfiguration) => {
        void config
      })
      store.saveDownloadClientConfiguration = save

      const wrapper = mount(DownloadClientFormModal, {
        global: { plugins: [pinia] },
        props: { visible: true, editingClient: null },
      })
      await wrapper.setProps({ editingClient })
      await nextTick()

      const submit = async () => {
        await wrapper.find('form').trigger('submit')
        await nextTick()
        expect(save).toHaveBeenCalledTimes(1)
        return save.mock.calls[0]![0]
      }

      return { wrapper, submit }
    }

    it('saves the entered URL Base and restores it when the saved client is reopened', async () => {
      const first = await mountWithSaveSpy(storedClient({}))
      await first.wrapper.find('input[id="urlBase"]').setValue('/qbittorrent')
      const saved = await first.submit()

      expect(saved.id).toBe('6')
      expect(saved.settings.urlBase).toBe('/qbittorrent')
      first.wrapper.unmount()

      // Reopen the form on exactly what was saved, the way the settings list hands a stored
      // client back, so the round trip is the thing under test and not a hand-written fixture.
      const reopened = await mountWithSaveSpy(saved)
      const input = reopened.wrapper.find('input[id="urlBase"]').element as HTMLInputElement
      expect(input.value).toBe('/qbittorrent')

      // Saving again without touching the field must not lose it.
      const resaved = await reopened.submit()
      expect(resaved.settings.urlBase).toBe('/qbittorrent')
      reopened.wrapper.unmount()
    })

    it('removes URL Base from the saved settings when the field is cleared', async () => {
      const { wrapper, submit } = await mountWithSaveSpy(
        storedClient({ urlBase: '/qbittorrent', category: 'listenarr' }),
      )

      const input = wrapper.find('input[id="urlBase"]')
      expect((input.element as HTMLInputElement).value).toBe('/qbittorrent')
      await input.setValue('')
      const saved = await submit()

      // Removed outright, not saved as an empty string: an empty prefix and no prefix must not
      // be two different stored states.
      expect(saved.settings).not.toHaveProperty('urlBase')
      // The control: clearing one setting leaves the others alone.
      expect(saved.settings.category).toBe('listenarr')
      wrapper.unmount()
    })
  })
})
