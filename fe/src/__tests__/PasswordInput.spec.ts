import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import PasswordInput from '@/components/form/PasswordInput.vue'

describe('PasswordInput', () => {
  it('toggles visibility and binds value', async () => {
    const wrapper = mount(PasswordInput, { props: { modelValue: 'secret' } })
    const input = wrapper.find('input')
    const toggle = wrapper.find('button.password-toggle')

    // initial should be password type
    expect((input.element as HTMLInputElement).type).toBe('password')

    // toggle to show
    await toggle.trigger('click')
    expect((input.element as HTMLInputElement).type).toBe('text')

    // toggle back to hide
    await toggle.trigger('click')
    expect((input.element as HTMLInputElement).type).toBe('password')

    // v-model binding works
    await input.setValue('newsecret')
    expect(wrapper.emitted()['update:modelValue']).toBeTruthy()
    expect(wrapper.emitted()['update:modelValue']![0][0]).toBe('newsecret')
  })
})