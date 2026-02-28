import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import IndexerFormModal from '@/components/settings/IndexerFormModal.vue'

describe('IndexerFormModal', () => {
  it('renders API key input as PasswordInput for Newznab/Torznab', async () => {
    const wrapper = mount(IndexerFormModal, {
      global: { plugins: [createPinia()] },
      props: { visible: true, editingIndexer: null },
    })

    await wrapper.setProps({
      editingIndexer: ({
        id: 1,
        name: 'Test Indexer',
        implementation: 'Newznab',
        url: 'https://example.test',
        apiKey: 'secret',
      } as any),
    })
    await wrapper.vm.$nextTick()

    // PasswordInput is a child component; assert it exists and its `modelValue` is populated
    const pwdComp = wrapper.findComponent({ name: 'PasswordInput' })
    expect(pwdComp.exists()).toBe(true)
    expect(pwdComp.props('modelValue')).toBe('secret')
  })
})