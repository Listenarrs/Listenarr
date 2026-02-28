import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

// Ensure we use the actual implementation (test-setup globally mocks /services/api)
vi.unmock('../services/api')
import { apiService as svc } from '../services/api'

describe('ApiService.ensureImageCached metadata flow', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    vi.resetAllMocks()
  })

  it('uses Audimeta image when available and triggers backend fetch', async () => {
    // Arrange
    // Stub getImageUrl to return the same local images path
    vi.spyOn(svc, 'getImageUrl').mockImplementation((url?: string) => url || '')

    // Stub audimeta response
    vi.spyOn(svc, 'getAudimetaMetadata').mockResolvedValue({ imageUrl: 'https://audimeta.covers/cover1.jpg' } as any)

    // Mock fetch: initial resolved URL missing -> 404, candidate audimeta triggers OK
    const fetchMock = vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo) => {
      const s = String(input)
      if (s.includes('/api/images/ASIN000001?url=') && s.includes('audimeta.covers')) {
        return { ok: true, status: 200 }
      }
      if (s.endsWith('/api/images/ASIN000001')) {
        return { ok: false, status: 404 }
      }
      return { ok: false, status: 404 }
    }))

    // Act
    const ok = await svc.ensureImageCached('/api/images/ASIN000001')

    // Assert
    expect(ok).toBe(true)
    expect(svc.getAudimetaMetadata).toHaveBeenCalledWith('ASIN000001', 'us', true)
    expect((globalThis.fetch as unknown as any).mock.calls.some((c) => String(c[0]).includes('audimeta.covers'))).toBe(true)
  })

  it('falls back to metadata (Audnexus) when Audimeta returns nothing', async () => {
    vi.spyOn(svc, 'getImageUrl').mockImplementation((url?: string) => url || '')

    vi.spyOn(svc, 'getAudimetaMetadata').mockResolvedValue({} as any)
    vi.spyOn(svc, 'getMetadata').mockResolvedValue({ metadata: { imageUrl: 'https://audnexus.covers/cover2.jpg' }, source: 'audnexus', sourceUrl: '' } as any)

    const fetchMock = vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo) => {
      const s = String(input)
      if (s.includes('/api/images/ASIN000002?url=') && s.includes('audnexus.covers')) {
        return { ok: true, status: 200 }
      }
      if (s.endsWith('/api/images/ASIN000002')) {
        return { ok: false, status: 404 }
      }
      return { ok: false, status: 404 }
    }))

    const ok = await svc.ensureImageCached('/api/images/ASIN000002')

    expect(ok).toBe(true)
    expect(svc.getAudimetaMetadata).toHaveBeenCalled()
    expect(svc.getMetadata).toHaveBeenCalledWith('ASIN000002', 'us', true)
    expect((globalThis.fetch as unknown as any).mock.calls.some((c) => String(c[0]).includes('audnexus.covers'))).toBe(true)
  })

  it('uses cached candidate urls and avoids repeated metadata lookups', async () => {
    vi.spyOn(svc, 'getImageUrl').mockImplementation((url?: string) => url || '')

    // Seed the cache manually
    ;(svc as any).metadataUrlCache.set('ASIN000003', { urls: ['https://cached.example/cover3.jpg'], fetchedAt: Date.now() })

    // Ensure metadata methods would throw if called
    vi.spyOn(svc, 'getAudimetaMetadata').mockImplementation(() => { throw new Error('should not call') })
    vi.spyOn(svc, 'getMetadata').mockImplementation(() => { throw new Error('should not call') })

    const fetchMock = vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo) => {
      const s = String(input)
      if (s.includes('/api/images/ASIN000003?url=') && s.includes('cached.example')) {
        return { ok: true, status: 200 }
      }
      if (s.endsWith('/api/images/ASIN000003')) {
        return { ok: false, status: 404 }
      }
      return { ok: false, status: 404 }
    }))

    const ok = await svc.ensureImageCached('/api/images/ASIN000003')
    expect(ok).toBe(true)
    expect(svc.getAudimetaMetadata).not.toHaveBeenCalled()
    expect(svc.getMetadata).not.toHaveBeenCalled()
  })
})