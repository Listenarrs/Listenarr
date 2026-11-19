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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { reactive } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { JobEntry } from '@/stores/jobs'

const jobsStore = reactive({
  jobs: [] as JobEntry[],
  dismiss: vi.fn(),
  cancel: vi.fn(async () => undefined),
  clearFinished: vi.fn(),
})

vi.mock('@/stores/jobs', () => ({ useJobsStore: () => jobsStore }))

import JobsView from '@/views/system/JobsView.vue'

function job(overrides: Partial<JobEntry> = {}): JobEntry {
  return {
    id: 'library-import-scan',
    kind: 'library-import-scan',
    title: 'Scanning Audiobooks A for unmatched files',
    description: 'Scan root dir: /media/audiobooks',
    message: 'Library Import · started at 10:40',
    status: 'running',
    active: true,
    startedAt: '2026-08-08T10:40:00Z',
    cancelable: true,
    stale: false,
    to: { name: 'library-import', query: { rootFolder: '7' } },
    action: {
      label: 'Follow job progress',
      to: { name: 'library-import', query: { rootFolder: '7' } },
    },
    icon: 'ph ph-folder-open',
    ...overrides,
  }
}

describe('JobsView', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(() => {
    jobsStore.jobs = []
    jobsStore.dismiss.mockReset()
    jobsStore.cancel.mockReset()
    jobsStore.clearFinished.mockReset()
  })
  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
  })

  async function mountView() {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/jobs', name: 'jobs', component: JobsView },
        { path: '/library-import', name: 'library-import', component: { template: '<div />' } },
        { path: '/audiobooks/:id', name: 'audiobook-detail', component: { template: '<div />' } },
      ],
    })
    await router.push('/jobs')
    await router.isReady()
    wrapper = mount(JobsView, { global: { plugins: [router] } })
    return { wrapper, router }
  }

  const rows = (w: VueWrapper) => w.findAll('[data-testid="job-row"]')

  it('lists every job with its details, start and status', async () => {
    jobsStore.jobs = [
      job(),
      job({
        id: 'scan-1',
        kind: 'folder-scan',
        title: 'Scan failed: Dune',
        message: 'Root unavailable',
        status: 'failed',
        active: false,
        cancelable: false,
      }),
    ]
    const { wrapper: w } = await mountView()

    expect(w.find('h1').text()).toContain('Jobs')
    expect(rows(w)).toHaveLength(2)
    expect(rows(w)[0]!.text()).toContain('Scanning Audiobooks A for unmatched files')
    expect(rows(w)[0]!.text()).toContain('Library Import · started at 10:40')
    expect(rows(w)[0]!.find('.status-badge').text()).toBe('Running')
    expect(rows(w)[0]!.find('.col-started').text()).not.toBe('')
    expect(rows(w)[1]!.find('.status-badge').text()).toBe('Failed')
    expect(rows(w)[1]!.find('.status-badge').classes()).toContain('failed')
  })

  it('shows a spinner for a running job, and a progress bar when it knows a percentage', async () => {
    jobsStore.jobs = [
      job(),
      job({ id: 'move-1', kind: 'move', title: 'Moving Dune', progress: 42, cancelable: false }),
    ]
    const { wrapper: w } = await mountView()

    expect(rows(w)[0]!.find('.col-progress .ph-spin').exists()).toBe(true)
    expect(rows(w)[0]!.find('.progress-fill').exists()).toBe(false)
    expect(rows(w)[1]!.find('.progress-fill').exists()).toBe(true)
    expect(rows(w)[1]!.text()).toContain('42%')
  })

  it("leads to the job's source from its title", async () => {
    jobsStore.jobs = [job()]
    const { wrapper: w, router } = await mountView()

    await rows(w)[0]!.find('a.title-link').trigger('click')
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(router.currentRoute.value.fullPath).toBe('/library-import?rootFolder=7')
  })

  it('cancels a cancelable job, and dismisses one that ended', async () => {
    jobsStore.jobs = [
      job(),
      job({ id: 'scan-1', status: 'done', active: false, cancelable: false, title: 'Scan done' }),
    ]
    const { wrapper: w } = await mountView()

    expect(rows(w)[0]!.find('[data-testid="job-dismiss"]').exists()).toBe(false)
    expect(rows(w)[1]!.find('[data-testid="job-cancel"]').exists()).toBe(false)
    await rows(w)[0]!.find('[data-testid="job-cancel"]').trigger('click')
    await rows(w)[1]!.find('[data-testid="job-dismiss"]').trigger('click')

    expect(jobsStore.cancel).toHaveBeenCalledWith('library-import-scan')
    expect(jobsStore.dismiss).toHaveBeenCalledWith('scan-1')
  })

  it('clears the jobs that ended', async () => {
    jobsStore.jobs = [job({ status: 'done', active: false, cancelable: false })]
    const { wrapper: w } = await mountView()

    await w.find('[data-testid="jobs-clear"]').trigger('click')

    expect(jobsStore.clearFinished).toHaveBeenCalled()
  })

  it('filters the jobs on their title and details', async () => {
    jobsStore.jobs = [
      job(),
      job({ id: 'move-1', kind: 'move', title: 'Moving Dune', message: 'Copying to /new' }),
    ]
    const { wrapper: w } = await mountView()

    await w.find('input.filter-input').setValue('copying')

    expect(rows(w)).toHaveLength(1)
    expect(rows(w)[0]!.text()).toContain('Moving Dune')
  })

  it('says when there are no jobs', async () => {
    const { wrapper: w } = await mountView()

    expect(rows(w)).toHaveLength(0)
    expect(w.text()).toContain('No jobs')
  })

  describe('Action column', () => {
    const actionButton = (row: ReturnType<typeof rows>[number]) =>
      row.find('.col-action [data-testid="job-action"]')

    it("comes first, with the job's button", async () => {
      jobsStore.jobs = [job()]
      const { wrapper: w } = await mountView()

      expect(w.find('.jobs-header > div').text()).toBe('Action')
      expect(rows(w)[0]!.element.firstElementChild?.classList).toContain('col-action')
      expect(actionButton(rows(w)[0]!).text()).toBe('Follow job progress')
    })

    it('follows a running job, and shows the results of a finished one', async () => {
      jobsStore.jobs = [
        job(),
        job({
          id: 'scan-b',
          status: 'done',
          active: false,
          cancelable: false,
          to: { name: 'library-import', query: { rootFolder: '9' } },
          action: {
            label: 'See job results',
            to: { name: 'library-import', query: { rootFolder: '9' } },
          },
        }),
      ]
      const { wrapper: w, router } = await mountView()

      expect(actionButton(rows(w)[1]!).text()).toBe('See job results')
      await actionButton(rows(w)[1]!).trigger('click')
      await new Promise((resolve) => setTimeout(resolve, 0))

      expect(router.currentRoute.value.fullPath).toBe('/library-import?rootFolder=9')
    })

    it('grays out a stale job, says why, and does not link its title', async () => {
      const reason = 'Stale result: this scan was replaced by a more recent scan of the same folder'
      jobsStore.jobs = [
        job({
          status: 'done',
          active: false,
          cancelable: false,
          stale: true,
          to: undefined,
          action: { label: 'See job results', disabledReason: reason },
        }),
      ]
      const { wrapper: w, router } = await mountView()
      const row = rows(w)[0]!

      expect(actionButton(row).attributes('disabled')).toBeDefined()
      expect(row.find('.col-action [title]').attributes('title')).toBe(reason)
      expect(row.find('a.title-link').exists()).toBe(false)
      expect(row.classes()).toContain('jobs-row--stale')
      await actionButton(row).trigger('click')
      expect(router.currentRoute.value.name).toBe('jobs')
    })

    it('has no button for a job without results to show', async () => {
      jobsStore.jobs = [job({ id: 'move-1', kind: 'move', action: undefined, cancelable: false })]
      const { wrapper: w } = await mountView()

      expect(rows(w)[0]!.find('.col-action').exists()).toBe(true)
      expect(actionButton(rows(w)[0]!).exists()).toBe(false)
    })
  })

  describe('Finished and Duration columns', () => {
    afterEach(() => vi.useRealTimers())

    it('shows when a job finished and how long it took', async () => {
      jobsStore.jobs = [
        job({
          status: 'done',
          active: false,
          cancelable: false,
          startedAt: '2026-08-08T10:40:00Z',
          finishedAt: '2026-08-08T10:42:05Z',
        }),
      ]
      const { wrapper: w } = await mountView()

      expect(w.findAll('.jobs-header > div').map((cell) => cell.text())).toEqual([
        'Action',
        'Job',
        'Description',
        'Details',
        'Progress',
        'Started',
        'Finished',
        'Duration',
        'Status',
        '',
      ])
      expect(rows(w)[0]!.find('.col-finished').text()).not.toBe('-')
      expect(rows(w)[0]!.find('.col-duration').text()).toBe('2m 05s')
    })

    it('counts the duration of a running job as it goes', async () => {
      vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] })
      vi.setSystemTime(new Date('2026-08-08T10:40:45Z'))
      jobsStore.jobs = [job({ startedAt: '2026-08-08T10:40:00Z' })]
      const { wrapper: w } = await mountView()

      expect(rows(w)[0]!.find('.col-finished').text()).toBe('-')
      expect(rows(w)[0]!.find('.col-duration').text()).toBe('45s')

      vi.advanceTimersByTime(1000 * 60)
      await w.vm.$nextTick()
      expect(rows(w)[0]!.find('.col-duration').text()).toBe('1m 45s')
    })

    it('shows no duration when the start is unknown', async () => {
      jobsStore.jobs = [
        job({ id: 'move-1', kind: 'move', startedAt: undefined, action: undefined }),
      ]
      const { wrapper: w } = await mountView()

      expect(rows(w)[0]!.find('.col-duration').text()).toBe('-')
    })
  })

  it('says what each job works on, after its name', async () => {
    jobsStore.jobs = [job()]
    const { wrapper: w } = await mountView()

    const cells = rows(w)[0]!.findAll(':scope > div')
    expect(cells[1]!.classes()).toContain('col-title')
    expect(cells[2]!.classes()).toContain('col-description')
    expect(cells[2]!.text()).toBe('Scan root dir: /media/audiobooks')
    expect(cells[2]!.attributes('title')).toBe('Scan root dir: /media/audiobooks')
  })

  it('filters on the description too', async () => {
    jobsStore.jobs = [
      job(),
      job({ id: 'b', title: 'Other', message: 'x', description: 'Scan root dir: /srv/other' }),
    ]
    const { wrapper: w } = await mountView()

    await w.find('input.filter-input').setValue('/srv/other')

    expect(rows(w)).toHaveLength(1)
  })
})
