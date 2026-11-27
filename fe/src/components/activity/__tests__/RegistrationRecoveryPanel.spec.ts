import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RegistrationRecoveryPanel from '../RegistrationRecoveryPanel.vue'

const api = vi.hoisted(() => ({
  getRegistrationReviews: vi.fn(),
  inspectRegistration: vi.fn(),
  resolveRegistration: vi.fn(),
}))
vi.mock('@/services/api', () => ({ apiService: api }))

const review = {
  operationId: 'test-operation',
  audiobookId: 1,
  action: 'Copy',
  protocolVersion: 1,
  state: 'NeedsAttention',
  error: 'Incomplete copy',
  sourcePath: '/source',
  destinationPath: '/target',
  source: { status: 'Present', length: 99, sha256: 'source-hash' },
  destination: { status: 'Missing', length: null, sha256: null },
  registrations: [],
  contentEvidence: 'Completeness cannot be verified',
  canResolve: true,
  reviewToken: 'review-token',
  resolutionNote: null,
  resolvedBy: null,
  resolvedAtUtc: null,
}

beforeEach(() => {
  vi.resetAllMocks()
  api.getRegistrationReviews.mockResolvedValue({ reviews: [review], total: 1 })
  api.inspectRegistration.mockResolvedValue(review)
})

describe('RegistrationRecoveryPanel', () => {
  it('states which operation types are listed and which are not', async () => {
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    expect(wrapper.find('h2').text()).toBe('File registration recovery')
    expect(wrapper.text()).toContain('move, copy and hardlink registrations')
    expect(wrapper.text()).toContain('existing-file move and rename operations is separate')
  })

  it('requires inspection, a note and acknowledgement before resolving one operation', async () => {
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    const inspect = wrapper.findAll('button').find((b) => b.text() === 'Inspect operation')!
    await inspect.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Completeness cannot be verified')
    const release = wrapper
      .findAll('button')
      .find((b) => b.text() === 'Release this recovery block')!
    expect(release.attributes('disabled')).toBeDefined()
    await wrapper.get('textarea').setValue('Preserved incomplete copy for repair')
    expect(release.attributes('disabled')).toBeDefined()
    await wrapper.get('input[type=checkbox]').setValue(true)
    api.resolveRegistration.mockResolvedValue({
      ...review,
      state: 'OperatorResolved',
      canResolve: false,
      resolvedBy: 'tester',
    })
    await release.trigger('click')
    await flushPromises()
    expect(api.resolveRegistration).toHaveBeenCalledWith(
      'test-operation',
      'review-token',
      'Preserved incomplete copy for repair',
    )
    expect(wrapper.text()).toContain('Resolved by tester')
  })

  it('requires a new inspection and acknowledgement after a stale or failed resolution', async () => {
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Inspect operation')!
      .trigger('click')
    await flushPromises()
    await wrapper.get('textarea').setValue('reviewed')
    await wrapper.get('input[type=checkbox]').setValue(true)
    api.resolveRegistration.mockRejectedValue(new Error('stale'))
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Release this recovery block')!
      .trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Inspect the operation again')
    expect(wrapper.find('textarea').exists()).toBe(false)
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Inspect operation')!
      .trigger('click')
    await flushPromises()
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(false)
    expect((wrapper.get('textarea').element as HTMLTextAreaElement).value).toBe('')
  })

  it('shows how many of the total operations are listed when the list is truncated', async () => {
    api.getRegistrationReviews.mockResolvedValue({ reviews: [review], total: 250 })
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    expect(wrapper.text()).toContain('Showing 1 of 250 operations.')
  })

  it('places the status line and Load more control after the list', async () => {
    api.getRegistrationReviews.mockResolvedValue({ reviews: [review], total: 3 })
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    const html = wrapper.html()
    expect(html.indexOf('Showing 1 of 3')).toBeGreaterThan(html.indexOf('</ul>'))
    expect(html.indexOf('Load more')).toBeGreaterThan(html.indexOf('</ul>'))
  })

  const row = (id: string) => ({ ...review, operationId: id })
  const buttonByText = (wrapper: ReturnType<typeof mount>, text: string) =>
    wrapper.findAll('button').find((b) => b.text() === text)

  it('loads further rows with a Load more control until every row is listed', async () => {
    api.getRegistrationReviews.mockImplementation(async (skip = 0) =>
      skip === 0
        ? { reviews: [row('op-1'), row('op-2')], total: 3 }
        : { reviews: [row('op-3')], total: 3 },
    )
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    expect(wrapper.text()).toContain('Showing 2 of 3 operations.')
    await buttonByText(wrapper, 'Load more')!.trigger('click')
    await flushPromises()
    expect(api.getRegistrationReviews).toHaveBeenLastCalledWith(2)
    expect(wrapper.findAll('code').map((c) => c.text())).toEqual(['op-1', 'op-2', 'op-3'])
    expect(wrapper.text()).toContain('Showing 3 of 3 operations.')
    expect(buttonByText(wrapper, 'Load more')).toBeUndefined()
  })

  it('reloads every loaded row, not only the first page, after a resolve', async () => {
    const all = [row('op-1'), row('op-2'), row('op-3')]
    api.getRegistrationReviews.mockImplementation(async (skip = 0) => ({
      reviews: all.slice(skip, skip + 2),
      total: 3,
    }))
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    await buttonByText(wrapper, 'Load more')!.trigger('click')
    await flushPromises()
    await buttonByText(wrapper, 'Inspect operation')!.trigger('click')
    await flushPromises()
    await wrapper.get('textarea').setValue('reviewed')
    await wrapper.get('input[type=checkbox]').setValue(true)
    api.resolveRegistration.mockResolvedValue({ ...review, canResolve: false })
    api.getRegistrationReviews.mockClear()
    await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
    await flushPromises()
    expect(api.getRegistrationReviews.mock.calls).toEqual([[], [2]])
    expect(wrapper.findAll('code').map((c) => c.text())).toContain('op-3')
    expect(wrapper.text()).toContain('Showing 3 of 3 operations.')
  })

  it('takes the total from the reload after a resolve', async () => {
    api.getRegistrationReviews.mockResolvedValue({ reviews: [review], total: 5 })
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    expect(wrapper.text()).toContain('Showing 1 of 5 operations.')
    await buttonByText(wrapper, 'Inspect operation')!.trigger('click')
    await flushPromises()
    await wrapper.get('textarea').setValue('reviewed')
    await wrapper.get('input[type=checkbox]').setValue(true)
    api.resolveRegistration.mockResolvedValue({ ...review, canResolve: false })
    api.getRegistrationReviews.mockResolvedValue({ reviews: [review], total: 4 })
    await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Showing 1 of 4 operations.')
  })

  async function openResolveForm(wrapper: ReturnType<typeof mount>) {
    await buttonByText(wrapper, 'Inspect operation')!.trigger('click')
    await flushPromises()
    await wrapper.get('textarea').setValue('reviewed')
    await wrapper.get('input[type=checkbox]').setValue(true)
  }

  it('says the resolution was not confirmed only when the resolve request fails', async () => {
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    await openResolveForm(wrapper)
    api.resolveRegistration.mockRejectedValue(new Error('stale'))
    api.getRegistrationReviews.mockClear()
    await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Resolution was not confirmed')
    expect(wrapper.text()).not.toContain('Resolved, but')
    expect(wrapper.find('textarea').exists()).toBe(false)
    expect(api.getRegistrationReviews).not.toHaveBeenCalled()
  })

  it('keeps the old list and says so when the resolve succeeds but the reload fails', async () => {
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    await openResolveForm(wrapper)
    api.resolveRegistration.mockResolvedValue({
      ...review,
      state: 'OperatorResolved',
      canResolve: false,
      resolvedBy: 'tester',
    })
    api.getRegistrationReviews.mockRejectedValue(new Error('offline'))
    await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Resolved, but the list could not be refreshed.')
    expect(wrapper.text()).not.toContain('Resolution was not confirmed')
    expect(wrapper.text()).toContain('Resolved by tester')
    expect(wrapper.findAll('li code').map((c) => c.text())).toEqual(['test-operation'])
  })

  it('lists a row once when two reload pages overlap by one row', async () => {
    const pages: Record<number, ReturnType<typeof row>[]> = {
      0: [row('op-1'), row('op-2')],
      2: [row('op-3')],
    }
    api.getRegistrationReviews.mockImplementation(async (skip = 0) => ({
      reviews: pages[skip],
      total: 3,
    }))
    const wrapper = mount(RegistrationRecoveryPanel)
    await flushPromises()
    await buttonByText(wrapper, 'Load more')!.trigger('click')
    await flushPromises()
    await buttonByText(wrapper, 'Inspect operation')!.trigger('click')
    await flushPromises()
    await wrapper.get('textarea').setValue('reviewed')
    await wrapper.get('input[type=checkbox]').setValue(true)
    api.resolveRegistration.mockResolvedValue({ ...review, canResolve: false })
    pages[2] = [row('op-2'), row('op-3')]
    await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
    await flushPromises()
    expect(wrapper.findAll('li code').map((c) => c.text())).toEqual(['op-1', 'op-2', 'op-3'])
  })

  describe('against a list that other clients change between page requests', () => {
    // Mirrors the server contract: pending rows first, resolved rows after them with the most
    // recently resolved first, 200 rows per page, X-Total-Count counting both groups.
    const pageSize = 200
    let pending: string[]
    let resolved: string[]
    const listed = () => [...pending, ...resolved]
    const resolveElsewhere = (id: string) => {
      pending = pending.filter((p) => p !== id)
      resolved = [id, ...resolved]
    }

    beforeEach(() => {
      pending = []
      resolved = []
      api.getRegistrationReviews.mockImplementation(async (skip = 0) => ({
        reviews: listed()
          .slice(skip, skip + pageSize)
          .map((id) => ({
            ...row(id),
            state: resolved.includes(id) ? 'OperatorResolved' : 'NeedsAttention',
          })),
        total: listed().length,
      }))
    })

    const names = (count: number) => Array.from({ length: count }, (_, i) => `op-${i + 1}`)
    const shown = (wrapper: ReturnType<typeof mount>) =>
      wrapper.findAll('li code').map((c) => c.text())
    const loadMoreUntilGone = async (wrapper: ReturnType<typeof mount>) => {
      for (let i = 0; i < 5 && buttonByText(wrapper, 'Load more'); i++) {
        await buttonByText(wrapper, 'Load more')!.trigger('click')
        await flushPromises()
      }
    }

    it('lists every operation when a loaded one is resolved by another client before Load more', async () => {
      pending = names(300)
      const wrapper = mount(RegistrationRecoveryPanel)
      await flushPromises()
      expect(wrapper.text()).toContain('Showing 200 of 300 operations.')
      resolveElsewhere('op-7')
      api.getRegistrationReviews.mockClear()
      await buttonByText(wrapper, 'Load more')!.trigger('click')
      await flushPromises()
      expect(wrapper.text()).toContain('Showing 300 of 300 operations.')
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      await loadMoreUntilGone(wrapper)
      expect(wrapper.text()).toContain('Showing 300 of 300 operations.')
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      expect(shown(wrapper)).toHaveLength(300)
      expect(
        api.getRegistrationReviews.mock.calls.filter(([skip]) => skip === 299).length,
      ).toBeLessThanOrEqual(1)
    })

    it('lists every operation when several loaded ones are resolved before Load more', async () => {
      pending = names(300)
      const wrapper = mount(RegistrationRecoveryPanel)
      await flushPromises()
      resolveElsewhere('op-3')
      resolveElsewhere('op-200')
      resolveElsewhere('op-100')
      await loadMoreUntilGone(wrapper)
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      expect(wrapper.text()).toContain('Showing 300 of 300 operations.')
    })

    it('lists every operation when another client adds operations before Load more', async () => {
      pending = names(300)
      const wrapper = mount(RegistrationRecoveryPanel)
      await flushPromises()
      pending = ['op-new-1', 'op-new-2', ...pending]
      await buttonByText(wrapper, 'Load more')!.trigger('click')
      await flushPromises()
      expect(wrapper.text()).toContain('Showing 302 of 302 operations.')
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      await loadMoreUntilGone(wrapper)
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      expect(shown(wrapper)).toHaveLength(302)
      expect(wrapper.text()).toContain('Showing 302 of 302 operations.')
    })

    it('stops cleanly when a page legitimately returns no rows at the end', async () => {
      pending = names(250)
      const wrapper = mount(RegistrationRecoveryPanel)
      await flushPromises()
      api.getRegistrationReviews.mockImplementation(async (skip = 0) => ({
        reviews:
          skip >= 250
            ? []
            : listed()
                .slice(skip, skip + pageSize)
                .map((id) => row(id)),
        total: 251,
      }))
      await buttonByText(wrapper, 'Load more')!.trigger('click')
      await flushPromises()
      expect(shown(wrapper)).toHaveLength(250)
      expect(wrapper.text()).not.toContain('Could not')
      expect(buttonByText(wrapper, 'Load more')!.attributes('disabled')).toBeUndefined()
      expect(api.getRegistrationReviews.mock.calls.length).toBeLessThan(10)
    })

    it('reloads every loaded row after a resolve even when a loaded row moved behind the pending rows', async () => {
      pending = names(300)
      const wrapper = mount(RegistrationRecoveryPanel)
      await flushPromises()
      await buttonByText(wrapper, 'Inspect operation')!.trigger('click')
      await flushPromises()
      await wrapper.get('textarea').setValue('reviewed')
      await wrapper.get('input[type=checkbox]').setValue(true)
      api.resolveRegistration.mockImplementation(async () => {
        resolveElsewhere('op-1')
        resolveElsewhere('op-9')
        return { ...review, canResolve: false }
      })
      await buttonByText(wrapper, 'Release this recovery block')!.trigger('click')
      await flushPromises()
      await loadMoreUntilGone(wrapper)
      expect(new Set(shown(wrapper))).toEqual(new Set(listed()))
      expect(wrapper.text()).toContain('Showing 300 of 300 operations.')
    })
  })
})
