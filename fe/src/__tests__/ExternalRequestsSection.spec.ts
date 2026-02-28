import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'

describe('ExternalRequestsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('renders the External Requests section', async () => {
    const { default: ExternalRequestsSection } = await import('@/components/settings/ExternalRequestsSection.vue')
    const wrapper = mount(ExternalRequestsSection, {
      props: { settings: {} },
    })

    const heading = wrapper.find('h3')
    expect(heading.text()).toContain('External Requests')
  })
})

