import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import PasswordInput from '@/components/form/PasswordInput.vue'

import { apiService } from '@/services/api'
import * as useConfirmModule from '@/composables/useConfirm'

describe('ApiKeyControl', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    // Reset imported modules so doMock can take effect for each test
    vi.resetModules()
  })

  it('copies to clipboard when copy button clicked', async () => {
    const writeMock = vi.fn().mockResolvedValue(undefined)
    // @ts-ignore - provide fake clipboard
    global.navigator = { clipboard: { writeText: writeMock } } as any

    const { default: ApiKeyControl } = await import('@/components/ui/ApiKeyControl.vue')
    const wrapper = mount(ApiKeyControl, {
      props: { apiKey: 'MYKEY' },
      global: { components: { PasswordInput } },
    })

    const copyBtn = wrapper.find('button.copy-btn')
    expect(copyBtn.exists()).toBe(true)

    await copyBtn.trigger('click')
    expect(writeMock).toHaveBeenCalledWith('MYKEY')
  })

  it('regenerates key and emits update when confirmed', async () => {
    const writeMock = vi.fn().mockResolvedValue(undefined)
    // @ts-ignore
    global.navigator = { clipboard: { writeText: writeMock } } as any

    const confirmModule = await import('@/composables/useConfirm')
    vi.spyOn(confirmModule, 'showConfirm').mockResolvedValue(true as any)
    // Mock the api module for this test to return a new key on regenerate
    vi.doMock('@/services/api', () => ({
      apiService: {
        regenerateApiKey: vi.fn().mockResolvedValue({ apiKey: 'NEWKEY' }),
        generateInitialApiKey: vi.fn(),
      },
    }))

    const { default: ApiKeyControl } = await import('@/components/ui/ApiKeyControl.vue')
    const wrapper = mount(ApiKeyControl, {
      props: { apiKey: 'OLDKEY' },
      global: { components: { PasswordInput } },
    })

    // Call the internal handler directly to avoid DOM-event quirks in VTU
    const setupState = (wrapper.vm as any).$?.setupState || (wrapper.vm as any).$setup
    await (setupState.onRegenerate as Function)()
    // wait for async handlers and promise resolution
    await new Promise((r) => setTimeout(r, 0))

    // Ensure underlying API was called
    const apiModule = await import('@/services/api')




    expect((apiModule.apiService.regenerateApiKey as any).mock).toBeTruthy()
    expect((apiModule.apiService.regenerateApiKey as any).mock.calls.length).toBeGreaterThan(0)

    // Should emit update:apiKey with new key
    expect(wrapper.emitted()['update:apiKey']).toBeTruthy()
    expect(wrapper.emitted()['update:apiKey']![0]).toEqual(['NEWKEY'])

    expect(writeMock).toHaveBeenCalledWith('NEWKEY')
  })

  it('generates initial key when none exists', async () => {
    const writeMock = vi.fn().mockResolvedValue(undefined)
    // @ts-ignore
    global.navigator = { clipboard: { writeText: writeMock } } as any

    const confirmModule = await import('@/composables/useConfirm')
    vi.spyOn(confirmModule, 'showConfirm').mockResolvedValue(true as any)
    // Mock generateInitialApiKey to return a new key for initial generation
    vi.doMock('@/services/api', () => ({
      apiService: {
        regenerateApiKey: vi.fn(),
        generateInitialApiKey: vi.fn().mockResolvedValue({ apiKey: 'INITKEY' }),
      },
    }))

    const { default: ApiKeyControl } = await import('@/components/ui/ApiKeyControl.vue')
    const wrapper = mount(ApiKeyControl, {
      props: { apiKey: '' },
      global: { components: { PasswordInput } },
    })

    const regenBtn = wrapper.find('button.regen-btn')
    await regenBtn.trigger('click')
    await new Promise((r) => setTimeout(r, 0))

    // Ensure underlying API was called
    const apiModule = await import('@/services/api')
    expect((apiModule.apiService.generateInitialApiKey as any).mock).toBeTruthy()
    expect((apiModule.apiService.generateInitialApiKey as any).mock.calls.length).toBeGreaterThan(0)

    expect(wrapper.emitted()['update:apiKey']).toBeTruthy()
    expect(wrapper.emitted()['update:apiKey']![0]).toEqual(['INITKEY'])
    expect(writeMock).toHaveBeenCalledWith('INITKEY')
  })
})
