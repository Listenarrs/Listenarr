import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import { ConfirmModal } from '@/components/feedback' 

describe('ConfirmModal', () => {
  it('renders message and emits confirm', async () => {
    const wrapper = mount(ConfirmModal, { props: { visible: true, message: 'Are you sure?', confirmLabel: 'Yes' } })
    // Modal content is teleported to document.body; assert message there
    expect(document.body.textContent).toContain('Are you sure?')
    // find save/confirm button rendered by teleport (in document.body)
    const btn = document.querySelector('button.btn-primary') as HTMLButtonElement | null
    expect(btn).not.toBeNull()
    btn!.click()
    // Modal emits 'confirm' on save
    expect(wrapper.emitted()).toHaveProperty('confirm')
  })
})