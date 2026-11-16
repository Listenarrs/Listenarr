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
import { mount } from '@vue/test-utils'
import { describe, it, expect } from 'vitest'
import { ModalHeader } from '@/components/feedback'
import { PhGlobe } from '@phosphor-icons/vue'

describe('ModalHeader', () => {
  it('renders title and icon prop and emits close', async () => {
    const wrapper = mount(ModalHeader, {
      props: { title: 'Hello', icon: PhGlobe, iconLabel: 'Globe' },
    })
    expect(wrapper.text()).toContain('Hello')
    // icon should render
    expect(wrapper.findComponent(PhGlobe).exists()).toBe(true)
    await wrapper.find('button.close-btn').trigger('click')
    expect(wrapper.emitted()).toHaveProperty('close')
  })
})
