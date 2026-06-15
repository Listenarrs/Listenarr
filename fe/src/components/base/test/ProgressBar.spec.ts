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
import ProgressBar from '@/components/base/ProgressBar.vue'

const TiB = 1024 ** 4
const GiB = 1024 ** 3

describe('ProgressBar', () => {
  it('formats multi-terabyte sizes with the TB unit', () => {
    const wrapper = mount(ProgressBar, {
      props: { value: 50, downloaded: 1.5 * TiB, total: 3 * TiB, showSize: true },
    })

    const text = wrapper.find('.size').text()
    expect(text).toContain('1.5 TB')
    expect(text).toContain('3.0 TB')
  })

  it('keeps the GB unit for sub-terabyte sizes', () => {
    const wrapper = mount(ProgressBar, {
      props: { value: 25, downloaded: 2 * GiB, total: 8 * GiB, showSize: true },
    })

    const text = wrapper.find('.size').text()
    expect(text).toContain('2.0 GB')
    expect(text).toContain('8.0 GB')
    expect(text).not.toContain('TB')
  })
})
