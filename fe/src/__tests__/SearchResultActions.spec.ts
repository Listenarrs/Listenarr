import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import SearchResultActions from '@/components/search/SearchResultActions.vue'

describe('SearchResultActions', () => {
  it('renders add button when not added', () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: false,
      },
    })
    const addBtn = wrapper.findAll('.btn')[0]
    expect(addBtn.text()).toContain('Add to Library')
    expect(addBtn.classes()).toContain('btn-primary')
  })

  it('renders success button when already added', () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: true,
      },
    })
    const addBtn = wrapper.findAll('.btn')[0]
    expect(addBtn.text()).toContain('Added')
    expect(addBtn.classes()).toContain('btn-success')
  })

  it('disables add button when already added', () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: true,
      },
    })
    const addBtn = wrapper.findAll('.btn')[0]
    expect(addBtn.attributes('disabled')).toBeDefined()
  })

  it('emits add event when add button clicked', async () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: false,
      },
    })
    const addBtn = wrapper.findAll('.btn')[0]
    await addBtn.trigger('click')
    expect(wrapper.emitted('add')).toHaveLength(1)
  })

  it('does not emit add event when already added', async () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: true,
      },
    })
    const addBtn = wrapper.findAll('.btn')[0]
    await addBtn.trigger('click')
    expect(wrapper.emitted('add')).toBeUndefined()
  })

  it('transitions to success state after adding', async () => {
    const wrapper = mount(SearchResultActions, {
      props: {
        isAdded: false,
      },
    })

    let addBtn = wrapper.findAll('.btn')[0]
    expect(addBtn.classes()).toContain('btn-primary')
    expect(addBtn.text()).toContain('Add to Library')

    await wrapper.setProps({ isAdded: true })
    await wrapper.vm.$nextTick()

    addBtn = wrapper.findAll('.btn')[0]
    expect(addBtn.classes()).toContain('btn-success')
    expect(addBtn.text()).toContain('Added')
  })
})
