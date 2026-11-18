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
