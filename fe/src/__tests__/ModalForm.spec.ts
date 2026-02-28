import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import { ModalForm } from '@/components/feedback' 

describe('ModalForm', () => {
  it('emits submit when form is submitted', async () => {
    const wrapper = mount(ModalForm, { slots: { default: '<input name="x" />' } })
    await wrapper.find('form').trigger('submit')
    expect(wrapper.emitted()).toHaveProperty('submit')
  })

  it('does not render a modal-body wrapper (use ModalBody for that)', () => {
    const wrapper = mount(ModalForm, { slots: { default: '<input name="x" />' } })
    expect(wrapper.find('[data-modal-body]').exists()).toBe(false)
  })
})