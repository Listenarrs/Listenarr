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
import StorageDisksList from '@/components/system/StorageDisksList.vue'
import { ProgressBar } from '@/components/base'
import type { DiskStorageInfo } from '@/types'

const makeDisk = (overrides: Partial<DiskStorageInfo> = {}): DiskStorageInfo => ({
  label: 'Audiobooks',
  path: '/audiobooks',
  usedBytes: 1_200_000_000_000,
  totalBytes: 8_000_000_000_000,
  freeBytes: 6_800_000_000_000,
  usedPercentage: 15,
  usedFormatted: '1.09 TB',
  totalFormatted: '7.28 TB',
  freeFormatted: '6.18 TB',
  status: 'available',
  ...overrides,
})

describe('StorageDisksList', () => {
  it('renders one entry per disk with label, path and a full-width usage bar', () => {
    const disks = [makeDisk({ label: 'App Data', path: '/app/config' }), makeDisk()]
    const wrapper = mount(StorageDisksList, { props: { disks } })

    const entries = wrapper.findAll('.disk-entry')
    expect(entries).toHaveLength(2)
    expect(entries[0].text()).toContain('App Data')
    expect(entries[0].text()).toContain('/app/config')
    expect(entries[1].text()).toContain('Audiobooks')
    expect(entries[1].text()).toContain('/audiobooks')

    const bars = wrapper.findAllComponents(ProgressBar)
    expect(bars).toHaveLength(2)
    expect(bars[1].props('value')).toBe(15)
    expect(bars[1].props('downloaded')).toBe(1_200_000_000_000)
    expect(bars[1].props('total')).toBe(8_000_000_000_000)
    expect(bars[1].props('showPercentage')).toBe(true)
    expect(bars[1].props('showSize')).toBe(true)
  })

  it('marks unavailable disks instead of showing a bar', () => {
    const disks = [makeDisk({ label: 'Missing', path: '/gone', status: 'unavailable' })]
    const wrapper = mount(StorageDisksList, { props: { disks } })

    const entry = wrapper.find('.disk-entry')
    expect(entry.text()).toContain('Missing')
    expect(entry.text()).toContain('unavailable')
    expect(wrapper.findAllComponents(ProgressBar)).toHaveLength(0)
  })
})
