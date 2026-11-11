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
  api.getRegistrationReviews.mockResolvedValue([review])
  api.inspectRegistration.mockResolvedValue(review)
})

describe('RegistrationRecoveryPanel', () => {
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
})
