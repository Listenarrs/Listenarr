import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import { ModalHeader } from '@/components/feedback'
import { PhGlobe } from '@phosphor-icons/vue' 

describe('ModalHeader', () => {
  it('renders title and icon prop and emits close', async () => {
    const wrapper = mount(ModalHeader, { props: { title: 'Hello', icon: PhGlobe, iconLabel: 'Globe' } })
    expect(wrapper.text()).toContain('Hello')
    // icon should render
    expect(wrapper.findComponent(PhGlobe).exists()).toBe(true)
    await wrapper.find('button.close-btn').trigger('click')
    expect(wrapper.emitted()).toHaveProperty('close')
  })
})