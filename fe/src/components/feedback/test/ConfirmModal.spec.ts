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
import { ConfirmModal } from '@/components/feedback'

describe('ConfirmModal', () => {
  it('renders message and emits confirm', async () => {
    const wrapper = mount(ConfirmModal, {
      props: { visible: true, message: 'Are you sure?', confirmLabel: 'Yes' },
    })
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
