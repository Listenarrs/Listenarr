import type {
  SearchResult,
  Download,
  ApiConfiguration,
  DownloadClientConfiguration,
  ApplicationSettings,
  Audiobook,
  History,
  Indexer,
  QueueItem,
  RemotePathMapping,
  // ...existing code...
  TranslatePathRequest,
  TranslatePathResponse,
  SystemInfo,
  StorageInfo,
  ServiceHealth,
  LogEntry,
  QualityProfile,
  SearchSortBy,
  SearchSortDirection,
  AudimetaSearchResponse,
  AudimetaBookResponse,
  AudibleBookMetadata,
  ManualImportPreviewResponse,
  ManualImportRequest,
  ManualImportResult,
  RootFolder,
  QualityScore,
  StartupConfig,
} from '@/types'
import { getStartupConfigCached, getCachedStartupConfig } from './startupConfigCache'
import { sessionTokenManager } from '@/utils/sessionToken'
import { logger } from '@/utils/logger'
import { getRegionFromLanguage } from '@/utils/languageMapping'
import { errorTracking } from '@/services/errorTracking'
import { getPlaceholderUrl } from '@/utils/placeholder'

// In development, use relative URLs (proxied by Vite to avoid CORS)
// In production, prefer a configured VITE_API_BASE_URL but fall back to a relative '/api'
const API_BASE_URL = import.meta.env.DEV ? '/api' : import.meta.env.VITE_API_BASE_URL || '/api'

// In Node test environments (Vitest), fetch does not accept bare-relative URLs.
// Create an effective absolute base when running without `window` so tests can
// call fetch('/api/...') by using 'http://localhost' as the origin.
const EFFECTIVE_API_BASE = typeof window === 'undefined' && API_BASE_URL.startsWith('/')
  ? `http://localhost${API_BASE_URL}`
  : API_BASE_URL

// Backend base (origin) used to build absolute image URLs or websocket origins
const BACKEND_BASE_URL = import.meta.env.DEV ? '' : API_BASE_URL.replace('/api', '')

type ErrorWithStatus = Error & { status?: number; body?: string; retryAfter?: number }

class ApiService {
  private antiforgeryToken: string | null = null;
  private antiforgeryTokenSession: string | null = null;
  private tokenReadyPromise: Promise<void> | null = null;
  // Placeholder URL helper moved to '@/utils/placeholder' - import and use that utility instead


  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
      // Await tokenReadyPromise before any unsafe request to guarantee fresh token
      const method = (options.method || 'GET').toString().toUpperCase();
      if (['POST', 'PUT', 'DELETE', 'PATCH'].includes(method) && this.tokenReadyPromise) {
        logger.debug('[ApiService] Awaiting tokenReadyPromise before unsafe request');
        await this.tokenReadyPromise;
        this.tokenReadyPromise = null;
      }
    const url = `${API_BASE_URL}${endpoint}`

    const config: RequestInit = {
      headers: {
        'Content-Type': 'application/json',
        ...options.headers,
      },
      credentials: 'include',
      ...options,
    }

    // Attach session token if available
    const sessionToken = sessionTokenManager.getToken()
    if (sessionToken) {
      const hdrs = config.headers as Record<string, string> | undefined
      config.headers = { ...(hdrs || {}), Authorization: `Bearer ${sessionToken}` }
    } else {
      // Only attach API key if authentication is disabled
      try {
        const sc = await getStartupConfigCached(2000)
        const apiKey = sc?.apiKey
        const rawAuth =
          sc?.authenticationRequired ??
          (sc as unknown as Record<string, unknown>)?.AuthenticationRequired
        const authEnabled =
          typeof rawAuth === 'boolean'
            ? rawAuth
            : typeof rawAuth === 'string'
              ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
              : false
        if (apiKey && !authEnabled) {
          const hdrs = config.headers as Record<string, string> | undefined
          config.headers = { ...(hdrs || {}), 'X-Api-Key': apiKey }
        }
      } catch {}
    }

    // Auto-attach antiforgery token for unsafe HTTP methods when not already provided.
    try {
      const method = (config.method || 'GET').toString().toUpperCase()
      if (['POST', 'PUT', 'DELETE', 'PATCH'].includes(method)) {
        // Only attach if header not already set
        const hdrs = config.headers as Record<string, string> | undefined
        if (!hdrs || !hdrs['X-XSRF-TOKEN']) {
          // Use cached antiforgery token if available and session matches
          const sessionToken = sessionTokenManager.getToken() || null;
          if (
            this.antiforgeryToken &&
            this.antiforgeryTokenSession === sessionToken
          ) {
            config.headers = {
              ...(config.headers as Record<string, string>),
              'X-XSRF-TOKEN': this.antiforgeryToken,
            }
          } else {
            // Fetch and cache new token for this session
            const token = await this.fetchAntiforgeryToken(
              sessionToken ? { Authorization: `Bearer ${sessionToken}` } : undefined
            )
            if (token) {
              this.antiforgeryToken = token;
              this.antiforgeryTokenSession = sessionToken;
              config.headers = {
                ...(config.headers as Record<string, string>),
                'X-XSRF-TOKEN': token,
              }
            }
          }
        }
      }
    } catch (e) {
      logger.debug('[ApiService] failed to fetch CSRF token', e)
    }

    try {
      // Debug: log outbound request details in development
      logger.debug('[ApiService] request', { url, config })

      const response = await fetch(url, config)

      if (!response.ok) {
        const respText = await response.text().catch(() => '')

        // If the server returns 401, redirect to login (don't surface raw 401 errors to the UI)
        if (response.status === 401) {
          // Avoid redirecting to the login page for certain API routes
          // (e.g., Audible endpoints) so the UI can handle 401 and show
          // a helpful message instead of performing a full-page redirect.
          const audibleEndpoints = [
            '/search/audible-library',
            '/search/audible-catalog',
            '/audible-auth',
          ]
          if (endpoint && audibleEndpoints.some((e) => endpoint.startsWith(e))) {
            const err = new Error(`HTTP error! status: 401 - ${respText}`) as ErrorWithStatus
            err.status = 401
            err.body = respText
            throw err
          }

          // Avoid causing a SPA redirect loop when the app is trying to fetch
          // the startup configuration during router boot. Let callers (router/auth)
          // handle 401 for that specific endpoint instead of performing a navigation
          // here which can trigger nested navigation during beforeEach.
          if (endpoint && endpoint.startsWith('/configuration/startupconfig')) {
            const err = new Error(`HTTP error! status: 401 - ${respText}`) as ErrorWithStatus
            err.status = 401
            err.body = respText
            throw err
          }

          // Sanitize redirect to avoid open-redirects or unsafe values
          try {
            const { normalizeRedirect } = await import('@/utils/redirect')
            const current = window.location.pathname + window.location.search + window.location.hash
            const safe = normalizeRedirect(current)
            if (!current.startsWith('/login')) {
              logger.debug('[ApiService] 401 received, redirecting to login', { current, safe })

              // Persist the safe redirect in sessionStorage as a fallback in case the
              // query parameter gets lost or sanitized during navigation. This helps
              // recover the intended SPA destination after login.
              try {
                sessionStorage.setItem('listenarr_pending_redirect', safe)
              } catch {}

              // Perform a full-page redirect to the login route with a safe redirect query.
              // Avoid dynamic importing the router here to prevent circular imports and
              // Vite chunking warnings. SPA navigation will still work after login via the
              // redirect query parameter.
              window.location.href = `/login?redirect=${encodeURIComponent(safe)}`

              // stop further processing by throwing a specific error
              throw new Error('Redirecting to login')
            }
          } catch {
            // fallback to a safe redirect to root
            window.location.href = '/login?redirect=%2F'
            throw new Error('Redirecting to login')
          }
        }

        // If this looks like a missing/invalid CSRF token, try to fetch a fresh
        // antiforgery token and retry the request once before surfacing the error.
        if (response.status === 400 && /csrf|anti.?forgery|invalid or missing/i.test(respText)) {
          try {
            // First, attempt to fetch a fresh token using the request's headers (if present)
            let freshToken = await this.fetchAntiforgeryToken(config.headers as Record<string, string> | undefined)
            logger.debug('[ApiService] CSRF retry - fetched token?', {
              freshTokenExists: !!freshToken,
              freshTokenLength: freshToken ? freshToken.length : 0,
            })

            // If we didn't get a token, or the retry failed, attempt once more using
            // explicitly constructed headers that ensure both Authorization and
            // X-Api-Key (when available) are present. This helps in races where a
            // non-blocking startup prefetch created an anonymous token before
            // startup configuration (API key) was available.
            if (!freshToken) {
              try {
                const explicitHeaders: Record<string, string> = { ...(config.headers as Record<string, string> | undefined) || {} }
                try {
                  const sess = sessionTokenManager.getToken()
                  if (sess && !explicitHeaders['Authorization']) explicitHeaders['Authorization'] = `Bearer ${sess}`
                } catch {}
                try {
                  const sc = await getStartupConfigCached(2000)
                  const apiKey = sc?.apiKey
                  if (apiKey && !explicitHeaders['X-Api-Key']) explicitHeaders['X-Api-Key'] = apiKey
                } catch {}

                logger.debug('[ApiService] CSRF retry - attempting explicit token fetch', { explicitHeaders })
                freshToken = await this.fetchAntiforgeryToken(explicitHeaders)
                logger.debug('[ApiService] CSRF retry - explicit fetched token?', { freshTokenExists: !!freshToken, freshTokenLength: freshToken ? freshToken.length : 0 })
              } catch (e) {
                logger.debug('[ApiService] CSRF explicit retry failed', e)
              }
            }

            if (freshToken) {
              const retryConfig: RequestInit = {
                ...config,
                headers: {
                  ...((config.headers as Record<string, string>) || {}),
                  'X-XSRF-TOKEN': freshToken,
                },
              }
              logger.debug('[ApiService] CSRF retry - retryConfig.headers', {
                headersPreview: { ...retryConfig.headers, 'X-XSRF-TOKEN': '[redacted]' },
              })
              const retryResp = await fetch(url, retryConfig)
              if (retryResp.ok) {
                const retryText = await retryResp.text()
                if (!retryText || retryText.trim().length === 0) return null as T
                return JSON.parse(retryText) as T
              }
              // If retry failed, prefer showing the retry response body for clarity
              const retryBody = await retryResp.text().catch(() => '')
              const retryErr = new Error(
                `HTTP error! status: ${retryResp.status} - ${retryBody}`,
              ) as ErrorWithStatus
              retryErr.status = retryResp.status
              retryErr.body = retryBody
              throw retryErr
            }
          } catch (retryErr) {
            logger.debug('[ApiService] CSRF retry failed', retryErr)
            // fall through to throw original error if retry fails
          }
        }

        const err = new Error(`HTTP error! status: ${response.status} - ${respText}`)
        const typedErr = err as Error & { status?: number; body?: string }
        typedErr.status = response.status
        typedErr.body = respText
        throw err
      }

      // Handle empty responses (204 No Content or empty body)
      const text = await response.text()
      if (!text || text.trim().length === 0) {
        return null as T
      }

      return JSON.parse(text) as T
    } catch (error) {
      // Enhanced logging for browser console to capture connection failures
      try {
        logger.error('[ApiService] request failed', { url, options: config, error })
      } catch {}
      throw error
    }
  }

  // Search API
  // Deprecated compatibility shim removed. Use `intelligentSearch`, `searchIndexers`, or `searchByApi`.

  async intelligentSearch(
    query: string,
    category?: string,
    signal?: AbortSignal,
  ): Promise<SearchResult[]> {
    const body: Record<string, unknown> = { mode: 'Simple', query }
    if (category) body['category'] = category
    const resp = await this.request<SearchResult[] | { results?: SearchResult[] } | null>(
      '/search',
      { method: 'POST', body: JSON.stringify(body), signal },
    )
    const results = Array.isArray(resp) ? resp : (resp?.results ?? [])
    return results
  }

  async searchIndexers(
    query: string,
    category?: string,
    sortBy?: SearchSortBy,
    sortDirection?: SearchSortDirection,
  ): Promise<SearchResult[]> {
    const params = new URLSearchParams({ query })
    if (category) params.append('category', category)
    if (sortBy) params.append('sortBy', sortBy)
    if (sortDirection) params.append('sortDirection', sortDirection)

    return this.request<SearchResult[]>(`/search/indexers?${params}`)
  }

  async searchByApi(
    apiId: string,
    query: string,
    category?: string,
    opts?: {
      mamFilter?: string
      mamSearchInDescription?: boolean
      mamSearchInSeries?: boolean
      mamSearchInFilenames?: boolean
      mamLanguage?: string
      mamFreeleechWedge?: string
      mamEnrichResults?: boolean
      mamEnrichTopResults?: number
    },
  ): Promise<SearchResult[]> {
    const params = new URLSearchParams({ query })
    if (category) params.append('category', category)

    // Map MyAnonamouse frontend options to backend query params
    if (opts?.mamFilter) params.append('mamFilter', opts.mamFilter)
    if (opts?.mamSearchInDescription !== undefined)
      params.append('mamSearchInDescription', String(opts.mamSearchInDescription))
    if (opts?.mamSearchInSeries !== undefined)
      params.append('mamSearchInSeries', String(opts.mamSearchInSeries))
    if (opts?.mamSearchInFilenames !== undefined)
      params.append('mamSearchInFilenames', String(opts.mamSearchInFilenames))
    if (opts?.mamLanguage) params.append('mamLanguage', opts.mamLanguage)
    if (opts?.mamFreeleechWedge) params.append('mamFreeleechWedge', opts.mamFreeleechWedge)
    if (opts?.mamEnrichResults !== undefined)
      params.append('mamEnrichResults', String(opts.mamEnrichResults))
    if (opts?.mamEnrichTopResults !== undefined)
      params.append('mamEnrichTopResults', String(opts.mamEnrichTopResults))

    return this.request<SearchResult[]>(`/search/${apiId}?${params}`)
  }

  async testApiConnection(apiId: string): Promise<boolean> {
    return this.request<boolean>(`/search/test/${apiId}`, { method: 'POST' })
  }

  // Audimeta API
  async searchAudimeta(
    query: string,
    page: number = 1,
    limit: number = 50,
    region: string = 'us',
    language?: string,
  ): Promise<AudimetaSearchResponse> {
    const params = new URLSearchParams({ query, page: String(page), limit: String(limit), region })
    if (language) params.append('language', language)
    return this.request<AudimetaSearchResponse>(`/search/audimeta?${params}`)
  }

  // Audimeta series helpers (proxied through backend)
  async searchAudimetaSeries(name: string, region: string = 'us'): Promise<unknown> {
    const params = new URLSearchParams({ name, region })
    return this.request<unknown>(`/search/audimeta/series?${params}`)
  }

  async getAudimetaSeriesBooks(seriesAsin: string, region: string = 'us'): Promise<unknown> {
    const params = new URLSearchParams({ region })
    return this.request<unknown>(
      `/search/audimeta/series/books/${encodeURIComponent(seriesAsin)}?${params}`,
    )
  }

  async searchAudimetaByTitleAndAuthor(
    title: string,
    author: string,
    page: number = 1,
    limit: number = 50,
    region: string = 'us',
    language?: string,
  ): Promise<AudimetaSearchResponse> {
    // Use unified POST /search in Advanced mode to route author/title flows to Audimeta
    const body: Record<string, unknown> = { mode: 'Advanced', title, author, page, limit, region }
    if (language) (body as Record<string, unknown>).language = language
    const resp = await this.request<AudimetaSearchResponse | null>('/search', {
      method: 'POST',
      body: JSON.stringify(body),
    })
    return resp ?? { totalResults: 0, results: [] }
  }

  async getAudimetaMetadata(
    asin: string,
    region: string = 'us',
    cache: boolean = true,
  ): Promise<AudimetaBookResponse> {
    return this.request(`/search/audimeta/${asin}?region=${region}&cache=${cache}`)
  }

  async getAuthorLookup(
    name: string,
    region: string = 'us',
  ): Promise<{ asin?: string; name?: string; image?: string; cachedPath?: string } | null> {
    const params = new URLSearchParams({ name, region })
    try {
      return await this.request(`/metadata/author?${params.toString()}`)
    } catch {
      return null
    }
  }

  async getMetadata(
    asin: string,
    region: string = 'us',
    cache: boolean = true,
  ): Promise<{ metadata: AudimetaBookResponse; source: string; sourceUrl: string }> {
    return this.request(`/search/metadata/${asin}?region=${region}&cache=${cache}`)
  }

  async searchByTitle(
    query: string,
    options?: RequestInit & { language?: string },
  ): Promise<SearchResult[]> {
    const language = options?.language || 'english'
    const region = getRegionFromLanguage(language)
    const body: Record<string, unknown> = { mode: 'Simple', query, region }
    const resp = await this.request<SearchResult[] | { results?: SearchResult[] } | null>(
      '/search',
      { method: 'POST', body: JSON.stringify(body), ...options },
    )
    // Backend returns either an array or an envelope { results: [...] } depending on mode.
    const results = Array.isArray(resp) ? resp : (resp?.results ?? [])
    return results
  }

  async advancedSearch(params: {
    title?: string
    author?: string
    isbn?: string
    series?: string
    asin?: string
    language?: string
    pagination?: { page?: number; limit?: number }
    cap?: number
  }): Promise<SearchResult[]> {
    const body: Record<string, unknown> = { mode: 'Advanced' }
    if (params.title) (body as Record<string, unknown>).title = params.title
    if (params.author) (body as Record<string, unknown>).author = params.author
    if (params.isbn) (body as Record<string, unknown>).isbn = params.isbn
    if (params.series) (body as Record<string, unknown>).series = params.series
    if (params.asin) (body as Record<string, unknown>).asin = params.asin
    if (params.language)
      (body as Record<string, unknown>).region = getRegionFromLanguage(params.language)
    if (params.pagination) (body as Record<string, unknown>).pagination = params.pagination
    if (typeof params.cap === 'number') (body as Record<string, unknown>).cap = params.cap
    const resp = await this.request<SearchResult[] | { results?: SearchResult[] } | null>(
      '/search',
      { method: 'POST', body: JSON.stringify(body) },
    )
    let results = Array.isArray(resp) ? resp : (resp?.results ?? [])

    // If this is a series-based advanced search, apply additional client-side
    // filtering for non-author inputs (title/isbn/asin) and wait for images
    // to be cached before returning results so the UI doesn't flash placeholders.
    const isSeriesSearch = !!params.series
    if (isSeriesSearch) {
      try {
        // Client-side filtering: apply title/isbn/asin filters when provided.
        if (params.title) {
          const q = params.title.toLowerCase()
          results = (results as SearchResult[]).filter(
            (r: SearchResult) =>
              ((r.title || '') as string).toLowerCase().includes(q) ||
              ((r.album || '') as string).toLowerCase().includes(q),
          )
        }
        if (params.isbn) {
          const q = params.isbn.toLowerCase()
          results = (results as SearchResult[]).filter(
            (r: SearchResult) =>
              ((r.isbn || '') as string).toLowerCase() === q ||
              ((r.asin || '') as string).toLowerCase() === q,
          )
        }
        if (params.asin) {
          const q = params.asin.toLowerCase()
          results = (results as SearchResult[]).filter(
            (r: SearchResult) => ((r.asin || '') as string).toLowerCase() === q,
          )
        }

        // Wait for images to be cached (timeout after 10s) before returning.
        try {
          await this.waitForImagesCached(results, 10000)
        } catch {}
      } catch {}
    }

    return results
  }

  // Attempt to fetch each result's image to ensure the backend has cached it.
  // Returns when all images succeed or the overall timeout elapses.
  private async waitForImagesCached(
    results: SearchResult[],
    overallTimeoutMs: number = 10000,
  ): Promise<void> {
    if (!results || results.length === 0) return
    const asins = results.map((r) => (r.asin || '').toString()).filter(Boolean)
    if (asins.length === 0) return

    const start = Date.now()
    const perFetchTimeout = 5000

    // Build headers like `request()` would (API key or session token)
    const sc = await getStartupConfigCached(2000).catch(() => null)
    const apiKey = sc?.apiKey
    const rawAuth =
      sc?.authenticationRequired ??
      (sc as unknown as Record<string, unknown>)?.AuthenticationRequired
    const authEnabled =
      typeof rawAuth === 'boolean'
        ? rawAuth
        : typeof rawAuth === 'string'
          ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
          : false
    const sessionToken = sessionTokenManager.getToken()

    const fetchWithTimeout = async (url: string, timeoutMs: number) => {
      const controller = new AbortController()
      const id = setTimeout(() => controller.abort(), timeoutMs)
      try {
        const headers: Record<string, string> = {}
        if (apiKey && !authEnabled) headers['X-Api-Key'] = apiKey
        if (sessionToken) headers['Authorization'] = `Bearer ${sessionToken}`
        const resp = await fetch(url, {
          method: 'GET',
          credentials: 'include',
          headers,
          signal: controller.signal,
        })
        clearTimeout(id)
        return resp.ok
      } catch {
        clearTimeout(id)
        return false
      }
    }

    const checks = asins.map(async (asin) => {
      const url = `${EFFECTIVE_API_BASE}/images/${encodeURIComponent(asin)}`
      // Try repeatedly until per-fetch timeout or overall timeout
      const deadline = Date.now() + Math.min(perFetchTimeout, overallTimeoutMs)
      while (Date.now() < deadline && Date.now() - start < overallTimeoutMs) {
        const ok = await fetchWithTimeout(url, 2000)
        if (ok) return
        // small backoff
        await new Promise((r) => setTimeout(r, 300))
      }
    })

    // Wait for all checks to complete or until overall timeout
    await Promise.race([
      Promise.all(checks),
      new Promise<void>((res) => setTimeout(res, overallTimeoutMs)),
    ])
  }

  async searchAudibleLibrary(query?: string, language?: string): Promise<SearchResult[]> {
    const queryParams = new URLSearchParams()
    if (query) queryParams.append('query', query)
    if (language) queryParams.append('language', language)

    const url = `/search/audible-library${queryParams.toString() ? '?' + queryParams.toString() : ''}`
    return this.request(url)
  }

  async searchAudibleCatalog(
    query?: string,
    title?: string,
    author?: string,
    language?: string,
  ): Promise<SearchResult[]> {
    const queryParams = new URLSearchParams()
    if (query) queryParams.append('query', query)
    if (title) queryParams.append('title', title)
    if (author) queryParams.append('author', author)
    if (language) queryParams.append('language', language)

    const url = `/search/audible-catalog${queryParams.toString() ? '?' + queryParams.toString() : ''}`
    return this.request(url)
  }

  async getAudibleAuthStatus(): Promise<{ authenticated: boolean; identityFile?: string }> {
    return this.request('/audible-auth/status')
  }

  async startAudibleExternalLogin(
    locale: string = 'us',
    deviceName: string = 'Listenarr',
  ): Promise<{ loginUrl: string; message?: string }> {
    return this.request('/audible-auth/external-login-start', {
      method: 'POST',
      body: JSON.stringify({ locale, deviceName }),
    })
  }

  async completeAudibleExternalLogin(
    responseUrl: string,
    locale?: string,
    deviceName?: string,
  ): Promise<unknown> {
    return this.request<unknown>('/audible-auth/external-login-complete', {
      method: 'POST',
      body: JSON.stringify({ responseUrl, locale, deviceName }),
    })
  }

  async logoutAudible(): Promise<unknown> {
    return this.request<unknown>('/audible-auth/logout', { method: 'POST' })
  }

  // Downloads API
  async getDownloads(): Promise<Download[]> {
    return this.request<Download[]>('/downloads')
  }

  async getDownload(id: string): Promise<Download> {
    return this.request<Download>(`/downloads/${id}`)
  }

  async startDownload(searchResult: SearchResult, downloadClientId: string): Promise<string> {
    return this.request<string>('/downloads', {
      method: 'POST',
      body: JSON.stringify({ searchResult, downloadClientId }),
    })
  }

  async cancelDownload(id: string): Promise<boolean> {
    return this.request<boolean>(`/downloads/${id}`, { method: 'DELETE' })
  }

  async getCachedAnnounces(
    downloadId: string,
  ): Promise<{ downloadId: string; announces: string[] } | null> {
    return this.request<{ downloadId: string; announces: string[] } | null>(
      `/download/cached/${downloadId}/announces`,
    )
  }

  async getCachedTorrent(downloadId: string): Promise<{ blob: Blob; filename?: string } | null> {
    const url = `${EFFECTIVE_API_BASE}/download/cached/${downloadId}/torrent`
    const resp = await fetch(url, { method: 'GET', credentials: 'include' })
    if (!resp.ok) return null
    const contentDisposition = resp.headers.get('content-disposition') || ''
    let filename: string | undefined
    const match = /filename="?([^";]+)"?/.exec(contentDisposition)
    if (match) filename = match[1]
    const blob = await resp.blob()
    return { blob, filename }
  }

  async searchAndDownload(audiobookId: number): Promise<{
    success: boolean
    message?: string
    downloadId?: string
    indexerUsed?: string
    downloadClientUsed?: string
    searchResult?: SearchResult
  }> {
    return this.request<{
      success: boolean
      message?: string
      downloadId?: string
      indexerUsed?: string
      downloadClientUsed?: string
      searchResult?: SearchResult
    }>('/download/search-and-download', {
      method: 'POST',
      body: JSON.stringify({ audiobookId }),
    })
  }

  async sendToDownloadClient(
    searchResult: SearchResult,
    downloadClientId?: string,
    audiobookId?: number,
  ): Promise<{
    downloadId: string
    message: string
  }> {
    return this.request<{
      downloadId: string
      message: string
    }>('/download/send', {
      method: 'POST',
      body: JSON.stringify({ searchResult, downloadClientId, audiobookId }),
    })
  }

  // Download Queue API
  async getQueue(): Promise<QueueItem[]> {
    return this.request<QueueItem[]>('/download/queue')
  }

  async removeFromQueue(
    downloadId: string,
    downloadClientId?: string,
  ): Promise<{ message: string }> {
    const params = downloadClientId ? `?downloadClientId=${downloadClientId}` : ''
    return this.request<{ message: string }>(`/download/queue/${downloadId}${params}`, {
      method: 'DELETE',
    })
  }

  // API Configuration
  async getApiConfigurations(): Promise<ApiConfiguration[]> {
    return this.request<ApiConfiguration[]>('/configuration/apis')
  }

  async getApiConfiguration(id: string): Promise<ApiConfiguration> {
    return this.request<ApiConfiguration>(`/configuration/apis/${id}`)
  }

  async saveApiConfiguration(config: ApiConfiguration): Promise<string> {
    return this.request<string>('/configuration/apis', {
      method: 'POST',
      body: JSON.stringify(config),
    })
  }

  async deleteApiConfiguration(id: string): Promise<boolean> {
    return this.request<boolean>(`/configuration/apis/${id}`, { method: 'DELETE' })
  }

  // Download Client Configuration
  async getDownloadClientConfigurations(): Promise<DownloadClientConfiguration[]> {
    return this.request<DownloadClientConfiguration[]>('/configuration/download-clients')
  }

  async getDownloadClientConfiguration(id: string): Promise<DownloadClientConfiguration> {
    return this.request<DownloadClientConfiguration>(`/configuration/download-clients/${id}`)
  }

  async saveDownloadClientConfiguration(config: DownloadClientConfiguration): Promise<string> {
    return this.request<string>('/configuration/download-clients', {
      method: 'POST',
      body: JSON.stringify(config),
    })
  }

  async deleteDownloadClientConfiguration(id: string): Promise<boolean> {
    return this.request<boolean>(`/configuration/download-clients/${id}`, { method: 'DELETE' })
  }

  async testDownloadClient(
    config: DownloadClientConfiguration,
  ): Promise<{ success: boolean; message: string; client?: DownloadClientConfiguration }> {
    return this.request<{
      success: boolean
      message: string
      client?: DownloadClientConfiguration
    }>('/configuration/download-clients/test', {
      method: 'POST',
      body: JSON.stringify(config),
    })
  }

  async testNotification(
    trigger?: string,
    data?: Record<string, unknown>,
    webhookId?: string,
    webhookUrl?: string,
  ): Promise<{ success: boolean; message: string }> {
    // If trigger and data are provided, use the new diagnostics endpoint
    if (trigger && data) {
      return this.request<{ success: boolean; message: string }>('/diagnostics/test-notification', {
        method: 'POST',
        body: JSON.stringify({ trigger, data, webhookId, webhookUrl }),
      })
    }
    // Otherwise use the old configuration endpoint for backward compatibility
    return this.request<{ success: boolean; message: string }>(
      '/configuration/notifications/test',
      {
        method: 'POST',
      },
    )
  }

  // Application Settings
  async getApplicationSettings(): Promise<ApplicationSettings> {
    return this.request<ApplicationSettings>('/configuration/settings')
  }

  async saveApplicationSettings(settings: ApplicationSettings): Promise<ApplicationSettings> {
    // Delegate CSRF handling to request(); it will fetch/attach a fresh token and
    // wait for any login-related tokenReadyPromise if necessary. Avoid manually
    // calling fetchAntiforgeryToken here because that can return a stale anonymous
    // token and override the cached value.
    return this.request<ApplicationSettings>('/configuration/settings', {
      method: 'POST',
      body: JSON.stringify(settings),
    })
  }

  // Root Folders
  async getRootFolders(): Promise<RootFolder[]> {
    return this.request<RootFolder[]>('/rootfolders')
  }

  async createRootFolder(root: {
    name: string
    path: string
    isDefault?: boolean
  }): Promise<RootFolder> {
    return this.request<RootFolder>('/rootfolders', { method: 'POST', body: JSON.stringify(root) })
  }

  async updateRootFolder(
    id: number,
    root: { id: number; name: string; path: string; isDefault?: boolean },
    opts?: { moveFiles?: boolean; deleteEmptySource?: boolean },
  ): Promise<RootFolder> {
    const qs = opts
      ? `?moveFiles=${opts.moveFiles === true}&deleteEmptySource=${opts.deleteEmptySource !== false}`
      : ''
    return this.request<RootFolder>(`/rootfolders/${id}${qs}`, {
      method: 'PUT',
      body: JSON.stringify(root),
    })
  }

  async deleteRootFolder(id: number, reassignTo?: number): Promise<{ message?: string }> {
    const qs = reassignTo ? `?reassignTo=${reassignTo}` : ''
    return this.request<{ message?: string }>(`/rootfolders/${id}${qs}`, { method: 'DELETE' })
  }

  // Discord integration helpers
  async getDiscordStatus(): Promise<{
    success: boolean
    installed?: boolean | null
    guildId?: string
    botInfo?: unknown
    message?: string
  }> {
    return this.request<{
      success: boolean
      installed?: boolean | null
      guildId?: string
      botInfo?: unknown
      message?: string
    }>('/discord/status')
  }

  async registerDiscordCommands(): Promise<{ success: boolean; message?: string; body?: unknown }> {
    return this.request<{ success: boolean; message?: string; body?: unknown }>(
      '/discord/register-commands',
      { method: 'POST' },
    )
  }

  async startDiscordBot(): Promise<{ success: boolean; message: string; status?: string }> {
    return this.request<{ success: boolean; message: string; status?: string }>(
      '/discord/start-bot',
      { method: 'POST' },
    )
  }

  async stopDiscordBot(): Promise<{ success: boolean; message: string; status?: string }> {
    return this.request<{ success: boolean; message: string; status?: string }>(
      '/discord/stop-bot',
      { method: 'POST' },
    )
  }

  async getDiscordBotStatus(): Promise<{ success: boolean; status: string; isRunning: boolean }> {
    return this.request<{ success: boolean; status: string; isRunning: boolean }>(
      '/discord/bot-status',
    )
  }

  // Startup configuration (read + write) — backend exposes under /configuration/startupconfig
  async getStartupConfig(): Promise<import('@/types').StartupConfig> {
    // Make a direct fetch here to avoid calling `request()` which itself uses
    // `getStartupConfigCached()` (would cause a recursion / loop).
    const resp = await fetch(`${API_BASE_URL}/configuration/startupconfig`, {
      method: 'GET',
      credentials: 'include',
    });
    if (!resp.ok) {
      throw new Error(`Failed to fetch startup config: ${resp.status}`)
    }
    return await resp.json();
  }

    /**
     * Save the startup configuration to the backend.
     * @param config The StartupConfig object to save
     */
    async saveStartupConfig(config: import('@/types').StartupConfig): Promise<{ success: boolean; message?: string }> {
      return this.request<{ success: boolean; message?: string }>('/configuration/startupconfig', {
        method: 'POST',
        body: JSON.stringify(config),
      });
    }



  // ...existing code...

  // Regenerate server-side API key. Returns the new API key in the response.
  async regenerateApiKey(): Promise<{ apiKey: string }> {
    const res = await this.request<{ apiKey: string }>('/configuration/apikey/regenerate', {
      method: 'POST',
    })
    // After regenerating the API key, ensure antiforgery token is issued for
    // the (potentially) updated authentication state so subsequent unsafe
    // requests use the correct token bound to the current auth principal.
    try {
      await this.ensureAntiforgeryForCurrentAuth()
    } catch {}
    return res
  }

  // Generate initial API key for first-time setup. Returns the new API key in the response.
  async generateInitialApiKey(): Promise<{ apiKey: string; message?: string }> {
    const res = await this.request<{ apiKey: string; message?: string }>(
      '/configuration/apikey/generate-initial',
      { method: 'POST' },
    )
    try {
      await this.ensureAntiforgeryForCurrentAuth()
    } catch {}
    return res
  }

  // Amazon ASIN lookup
  async getAsinFromIsbn(
    isbn: string,
  ): Promise<{ success: boolean; asin?: string; error?: string }> {
    return this.request<{ success: boolean; asin?: string; error?: string }>(
      `/amazon/asin-from-isbn/${encodeURIComponent(isbn)}`,
    )
  }

  // Audible Metadata API
  async getAudibleMetadata<T>(asin: string): Promise<T> {
    return this.request<T>(`/audible/metadata/${asin}`)
  }

  // Library API
  async getLibrary(): Promise<Audiobook[]> {
    return this.request<Audiobook[]>('/library')
  }

  async addToLibrary(
    metadata: AudibleBookMetadata,
    options?: {
      monitored?: boolean
      qualityProfileId?: number
      autoSearch?: boolean
      searchResult?: SearchResult
      destinationPath?: string
    },
  ): Promise<{ message: string; audiobook: Audiobook }> {
    const request = {
      metadata,
      monitored: options?.monitored ?? true,
      qualityProfileId: options?.qualityProfileId,
      autoSearch: options?.autoSearch ?? false,
      searchResult: options?.searchResult,
      destinationPath: options?.destinationPath,
    }
    return this.request<{ message: string; audiobook: Audiobook }>('/library/add', {
      method: 'POST',
      body: JSON.stringify(request),
    })
  }

  async previewLibraryPath(
    metadata: AudibleBookMetadata,
    destinationRoot?: string,
  ): Promise<{ fullPath: string; relativePath: string; root?: string }> {
    const body = { metadata, destinationRoot }
    return this.request<{ fullPath: string; relativePath: string; root?: string }>(
      '/library/preview-path',
      {
        method: 'POST',
        body: JSON.stringify(body),
      },
    )
  }

  async getAudiobook(id: number): Promise<Audiobook> {
    return this.request<Audiobook>(`/library/${id}`)
  }

  async scanAudiobook(
    id: number,
    path?: string,
  ): Promise<{
    message: string
    scannedPath?: string
    found: number
    created: number
    audiobook?: Audiobook
    jobId?: string
  }> {
    return this.request(`/library/${id}/scan`, {
      method: 'POST',
      body: JSON.stringify({ path }),
    })
  }

  async updateAudiobook(
    id: number,
    audiobook: Partial<Audiobook>,
  ): Promise<{ message: string; audiobook: Audiobook }> {
    return this.request<{ message: string; audiobook: Audiobook }>(`/library/${id}`, {
      method: 'PUT',
      body: JSON.stringify(audiobook),
    })
  }

  async moveAudiobook(
    id: number,
    destinationPath: string,
    options?: { sourcePath?: string; moveFiles?: boolean; deleteEmptySource?: boolean },
  ): Promise<{ message: string; jobId?: string }> {
    const body: Record<string, unknown> = { destinationPath }
    if (options?.sourcePath) (body as Record<string, unknown>).sourcePath = options.sourcePath
    if (options?.moveFiles !== undefined)
      (body as Record<string, unknown>).moveFiles = options.moveFiles
    if (options?.deleteEmptySource !== undefined)
      (body as Record<string, unknown>).deleteEmptySource = options.deleteEmptySource
    return this.request<{ message: string; jobId?: string }>(`/library/${id}/move`, {
      method: 'POST',
      body: JSON.stringify(body),
    })
  }

  async removeFromLibrary(id: number): Promise<{ message: string; id: number }> {
    return this.request<{ message: string; id: number }>(`/library/${id}`, {
      method: 'DELETE',
    })
  }

  async bulkRemoveFromLibrary(
    ids: number[],
  ): Promise<{ message: string; deletedCount: number; deletedImagesCount: number; ids: number[] }> {
    return this.request<{
      message: string
      deletedCount: number
      deletedImagesCount: number
      ids: number[]
    }>('/library/delete-bulk', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    })
  }

  async bulkUpdateAudiobooks(
    ids: number[],
    updates: Record<string, boolean | number | string>,
  ): Promise<{
    message: string
    results: Array<{ id: number; success: boolean; errors: string[] }>
  }> {
    return this.request<{
      message: string
      results: Array<{ id: number; success: boolean; errors: string[] }>
    }>('/library/bulk-update', {
      method: 'POST',
      body: JSON.stringify({ ids, updates }),
    })
  }

  // File System API
  async browseDirectory(path?: string): Promise<{
    currentPath: string
    parentPath: string | null
    items: Array<{
      name: string
      path: string
      isDirectory: boolean
      lastModified: string
    }>
  }> {
    const params = path ? `?path=${encodeURIComponent(path)}` : ''
    return this.request(`/filesystem/browse${params}`)
  }

  async validatePath(path: string): Promise<{
    isValid: boolean
    exists: boolean
    isWritable: boolean
    message: string
  }> {
    return this.request(`/filesystem/validate?path=${encodeURIComponent(path)}`)
  }

  async checkVolume(sourcePath: string, destPath: string): Promise<{
    sameVolume: boolean
    willBreakHardlinks: boolean
    sourceVolume?: string
    destVolume?: string
    message?: string
  }> {
    return this.request(
      `/filesystem/check-volume?sourcePath=${encodeURIComponent(sourcePath)}&destPath=${encodeURIComponent(destPath)}`,
    )
  }

  // Manual import preview / start
  async previewManualImport(path: string): Promise<ManualImportPreviewResponse> {
    const params = path ? `?path=${encodeURIComponent(path)}` : ''
    return this.request<ManualImportPreviewResponse>(`/library/manual-import/preview${params}`)
  }

  async startManualImport(
    request: ManualImportRequest,
  ): Promise<{ importedCount: number; totalCount?: number; results?: ManualImportResult[] }> {
    return this.request<{
      importedCount: number
      totalCount?: number
      results?: ManualImportResult[]
    }>(`/library/manual-import`, {
      method: 'POST',
      body: JSON.stringify(request),
    })
  }

  // Helper to convert relative image URLs to absolute
  getImageUrl(imageUrl: string | undefined): string {
    if (!imageUrl) return ''
    // If already absolute URL, return as is
    if (imageUrl.startsWith('http://') || imageUrl.startsWith('https://')) {
      // Prefer serving images from the backend image cache when referencing
      // known vendor/product links (Amazon/Audible) or common CDN hosts. Try
      // to extract an ASIN-like identifier and map to our `/api/images/{id}`
      // endpoint. Fall back to the original URL only if extraction fails.
      try {
        const parsed = new URL(imageUrl)
        const hostname = (parsed.hostname || '').toLowerCase()

        // Amazon primary domains and subdomains
        const isAmazonHost =
          hostname === 'amazon.com' ||
          hostname === 'www.amazon.com' ||
          hostname.endsWith('.amazon.com') ||
          // common image/CDN hosts
          hostname === 'm.media-amazon.com' ||
          hostname.endsWith('.m.media-amazon.com') ||
          hostname === 'images-amazon.com' ||
          hostname.endsWith('.images-amazon.com')

        const isAudibleHost =
          hostname === 'audible.com' ||
          hostname === 'www.audible.com' ||
          hostname.endsWith('.audible.com')

        const isVendor = isAmazonHost || isAudibleHost

        if (isVendor) {
          // Try common ASIN patterns: 10 alphanumeric chars, or 10 digits
          let asinMatch = imageUrl.match(/([A-Z0-9]{10})/i) || imageUrl.match(/(\d{10})/)
          if (!asinMatch) {
            // For Amazon image URLs like https://m.media-amazon.com/images/I/9156QjXBIHL.jpg
            // Extract the identifier after /I/
            const amazonImageMatch = imageUrl.match(/\/I\/([A-Z0-9]{10,12})\./i)
            if (amazonImageMatch && amazonImageMatch[1]) {
              asinMatch = amazonImageMatch
            }
          }
          if (asinMatch && asinMatch[1]) {
            const identifier = asinMatch[1]
            let url = `${BACKEND_BASE_URL}/api/images/${encodeURIComponent(identifier)}`
            const params = new URLSearchParams()
            const sessionToken = sessionTokenManager.getToken()
            if (sessionToken) {
              params.append('access_token', sessionToken)
            } else {
              const cfg = getCachedStartupConfig()
              const apiKey = cfg?.apiKey
              if (apiKey) params.append('access_token', apiKey)
            }
            params.append('url', imageUrl)
            const query = params.toString()
            if (query) url += `?${query}`
            return url
          }

          // If we couldn't extract ASIN, try to parse filename from path and
          // use a 10-12 char filename (without extension) as identifier.
          try {
            const pathname = new URL(imageUrl).pathname
            const fname = pathname.split('/').pop() || ''
            const base = fname.replace(/\.[^.]+$/, '')
            if (base && base.length >= 10 && base.length <= 12) {
              const identifier = base
              let url = `${BACKEND_BASE_URL}/api/images/${encodeURIComponent(identifier)}`
              const params = new URLSearchParams()
              const sessionToken = sessionTokenManager.getToken()
              if (sessionToken) {
                params.append('access_token', sessionToken)
              } else {
                const cfg = getCachedStartupConfig()
                const apiKey = cfg?.apiKey
                if (apiKey) params.append('access_token', apiKey)
              }
              params.append('url', imageUrl)
              const query = params.toString()
              if (query) url += `?${query}`
              return url
            }
          } catch {}
        }
      } catch (e) {
        logger.debug('[ApiService] amazon-image-detect error', e)
      }

      return imageUrl
    }
    // If the stored path is the library cache path, convert to our images API endpoint
    // Example stored path: /config/cache/images/library/B0DD5FX7QG.jpg
    try {
      const libMatch = imageUrl.match(/\/config\/cache\/images\/library\/(.+)$/)
      if (libMatch && libMatch[1]) {
        // Extract filename (with extension) and strip extension to use as identifier
        const filename = libMatch[1]
        const identifier = filename.replace(/\.[^.]+$/, '')
        let url = `${BACKEND_BASE_URL}/api/images/${encodeURIComponent(identifier)}`

        // Append session token if available (for authenticated users)
        const sessionToken = sessionTokenManager.getToken()
        if (sessionToken) {
          url += `?access_token=${encodeURIComponent(sessionToken)}`
        } else {
          // Fallback to API key if no session token (for non-authenticated access)
          const cfg = getCachedStartupConfig()
          const apiKey = cfg?.apiKey
          if (apiKey) {
            url += `?access_token=${encodeURIComponent(apiKey)}`
          }
        }
        return url
      }
    } catch (e) {
      // fall back to default behavior below on any error
      logger.debug('[ApiService] getImageUrl library-detect error', e)
    }

    // If the stored path is the authors cache path, convert to our images API endpoint
    // Example stored path: /config/cache/images/authors/AUTHORASIN.jpg
    try {
      const authorMatch = imageUrl.match(/\/config\/cache\/images\/authors\/(.+)$/)
      if (authorMatch && authorMatch[1]) {
        const filename = authorMatch[1]
        const identifier = filename.replace(/\.[^.]+$/, '')
        let url = `${BACKEND_BASE_URL}/api/images/${encodeURIComponent(identifier)}`

        const sessionToken = sessionTokenManager.getToken()
        if (sessionToken) {
          url += `?access_token=${encodeURIComponent(sessionToken)}`
        } else {
          const cfg = getCachedStartupConfig()
          const apiKey = cfg?.apiKey
          if (apiKey) {
            url += `?access_token=${encodeURIComponent(apiKey)}`
          }
        }
        return url
      }
    } catch (e) {
      logger.debug('[ApiService] getImageUrl authors-detect error', e)
    }

    // Convert other relative URLs to absolute and append access_token
    const absolute = `${BACKEND_BASE_URL}${imageUrl}`
    try {
      // Try session token first (for authenticated users)
      const sessionToken = sessionTokenManager.getToken()
      if (sessionToken) {
        const sep = absolute.includes('?') ? '&' : '?'
        return `${absolute}${sep}access_token=${encodeURIComponent(sessionToken)}`
      }

      // Fallback to API key if no session token
      const cfg = getCachedStartupConfig()
      const apiKey = cfg?.apiKey
      if (apiKey) {
        const sep = absolute.includes('?') ? '&' : '?'
        return `${absolute}${sep}access_token=${encodeURIComponent(apiKey)}`
      }
    } catch {
      // ignore and return plain absolute URL
    }

    return absolute
  }

  // Expose a lightweight cache for image metadata candidates (tests and UI may seed/read this)
  // Keys: ASIN-like identifier => { urls: string[]; fetchedAt: number }
  public metadataUrlCache = new Map<string, { urls: string[]; fetchedAt: number }>()

  /**
   * Ensure the backend image cache has a cached copy for the given image endpoint.
   * Attempts to resolve candidate image URLs from Audimeta and Audnexus metadata,
   * caches discovered candidate URLs, and triggers a backend fetch for each candidate URL.
   * Returns true if any candidate (or the base image endpoint) returned a successful response.
   */
  async ensureImageCached(path: string): Promise<boolean> {
    try {
      // Expect path like '/api/images/{id}' optionally with query string
      const m = String(path).match(/\/api\/images\/([^\?\/]+)/)
      if (!m || !m[1]) return false
      const id = decodeURIComponent(m[1])

      // Check seeded cache first
      const cached = this.metadataUrlCache.get(id)
      let candidates: string[] = []
      if (cached && Array.isArray(cached.urls) && cached.urls.length > 0) {
        candidates = cached.urls.slice()
      } else {
        // Query audimeta first, then fallback to backend metadata (audnexus)
        try {
          const am = await this.getAudimetaMetadata(id, 'us', true)
          if (am && (am as any).imageUrl) candidates.push((am as any).imageUrl)
        } catch {}
        try {
          const mm = await this.getMetadata(id, 'us', true)
          if (mm && (mm as any).metadata && (mm as any).metadata.imageUrl)
            candidates.push((mm as any).metadata.imageUrl)
        } catch {}

        // Cache the discovered candidates for future calls (even if empty)
        this.metadataUrlCache.set(id, { urls: candidates, fetchedAt: Date.now() })
      }

      // Try each candidate by asking backend to fetch and cache it via /api/images/{id}?url=...
      for (const url of candidates) {
        try {
          const resp = await fetch(`${API_BASE_URL}/images/${encodeURIComponent(id)}?url=${encodeURIComponent(url)}`)
          if ((resp as any).ok) return true
        } catch {}
      }

      // As a fallback, check the base image endpoint (maybe already cached)
      try {
        const baseResp = await fetch(`${API_BASE_URL}/images/${encodeURIComponent(id)}`)
        if ((baseResp as any).ok) return true
      } catch {}

      return false
    } catch (e) {
      return false
    }
  }

  // History API
  async getHistory(
    limit?: number,
    offset?: number,
  ): Promise<{
    history: History[]
    total: number
    limit: number
    offset: number
  }> {
    const params = new URLSearchParams()
    if (limit) params.append('limit', limit.toString())
    if (offset) params.append('offset', offset.toString())
    const queryString = params.toString()
    return this.request<{
      history: History[]
      total: number
      limit: number
      offset: number
    }>(`/history${queryString ? '?' + queryString : ''}`)
  }

  async getHistoryByAudiobookId(audiobookId: number): Promise<History[]> {
    return this.request<History[]>(`/history/audiobook/${audiobookId}`)
  }

  async getHistoryByEventType(eventType: string, limit?: number): Promise<History[]> {
    const params = limit ? `?limit=${limit}` : ''
    return this.request<History[]>(`/history/type/${eventType}${params}`)
  }

  async getHistoryBySource(source: string, limit?: number): Promise<History[]> {
    const params = limit ? `?limit=${limit}` : ''
    return this.request<History[]>(`/history/source/${source}${params}`)
  }

  async getRecentHistory(limit: number = 50): Promise<History[]> {
    return this.request<History[]>(`/history/recent?limit=${limit}`)
  }

  async deleteHistoryEntry(id: number): Promise<{ message: string; id: number }> {
    return this.request<{ message: string; id: number }>(`/history/${id}`, {
      method: 'DELETE',
    })
  }

  async clearAllHistory(): Promise<{ message: string; deletedCount: number }> {
    return this.request<{ message: string; deletedCount: number }>('/history/clear', {
      method: 'DELETE',
    })
  }

  async cleanupOldHistory(days: number = 90): Promise<{ message: string; deletedCount: number }> {
    return this.request<{ message: string; deletedCount: number }>(
      `/history/cleanup?days=${days}`,
      {
        method: 'DELETE',
      },
    )
  }

  // Indexers API
  async getIndexers(): Promise<Indexer[]> {
    return this.request<Indexer[]>('/indexers')
  }

  async getIndexerById(id: number): Promise<Indexer> {
    return this.request<Indexer>(`/indexers/${id}`)
  }

  async createIndexer(indexer: Omit<Indexer, 'id' | 'createdAt' | 'updatedAt'>): Promise<Indexer> {
    return this.request<Indexer>('/indexers', {
      method: 'POST',
      body: JSON.stringify(indexer),
    })
  }

  async updateIndexer(id: number, indexer: Partial<Indexer>): Promise<Indexer> {
    return this.request<Indexer>(`/indexers/${id}`, {
      method: 'PUT',
      body: JSON.stringify(indexer),
    })
  }

  async deleteIndexer(id: number): Promise<{ message: string; id: number }> {
    return this.request<{ message: string; id: number }>(`/indexers/${id}`, {
      method: 'DELETE',
    })
  }

  async testIndexer(
    id: number,
  ): Promise<{ success: boolean; message: string; error?: string; indexer: Indexer }> {
    return this.request<{ success: boolean; message: string; error?: string; indexer: Indexer }>(
      `/indexers/${id}/test`,
      {
        method: 'POST',
      },
    )
  }

  async testIndexerDraft(
    indexer: Omit<Indexer, 'id' | 'createdAt' | 'updatedAt'>,
  ): Promise<{ success: boolean; message: string; error?: string; indexer: Indexer }> {
    return this.request<{ success: boolean; message: string; error?: string; indexer: Indexer }>(
      '/indexers/test',
      {
        method: 'POST',
        body: JSON.stringify(indexer),
      },
    )
  }

  async toggleIndexer(id: number): Promise<Indexer> {
    return this.request<Indexer>(`/indexers/${id}/toggle`, {
      method: 'PUT',
    })
  }

  async importProwlarrIndexers(payload: {
    url: string
    port?: number
    apiKey: string
  }): Promise<{
    addedCount: number
    skippedCount: number
    total: number
    indexers: Array<{ id: number; name: string; url: string; implementation: string }>
  }> {
    return this.request(`/indexers/prowlarr/import`, {
      method: 'POST',
      body: JSON.stringify(payload),
    })
  }

  async getEnabledIndexers(): Promise<Indexer[]> {
    return this.request<Indexer[]>('/indexers/enabled')
  }

  // Remote Path Mappings
  async getRemotePathMappings(): Promise<RemotePathMapping[]> {
    return this.request<RemotePathMapping[]>('/remotepathmappings')
  }

  async getRemotePathMappingById(id: number): Promise<RemotePathMapping> {
    return this.request<RemotePathMapping>(`/remotepathmappings/${id}`)
  }

  async getRemotePathMappingsByClient(downloadClientId: string): Promise<RemotePathMapping[]> {
    return this.request<RemotePathMapping[]>(
      `/remotepathmappings/client/${encodeURIComponent(downloadClientId)}`,
    )
  }

  async createRemotePathMapping(
    mapping: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>,
  ): Promise<RemotePathMapping> {
    return this.request<RemotePathMapping>('/remotepathmappings', {
      method: 'POST',
      body: JSON.stringify(mapping),
    })
  }

  async updateRemotePathMapping(
    id: number,
    mapping: Partial<RemotePathMapping>,
  ): Promise<RemotePathMapping> {
    return this.request<RemotePathMapping>(`/remotepathmappings/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ...mapping, id }),
    })
  }

  async deleteRemotePathMapping(id: number): Promise<void> {
    return this.request<void>(`/remotepathmappings/${id}`, {
      method: 'DELETE',
    })
  }

  async translatePath(request: TranslatePathRequest): Promise<TranslatePathResponse> {
    return this.request<TranslatePathResponse>('/remotepathmappings/translate', {
      method: 'POST',
      body: JSON.stringify(request),
    })
  }

  // System endpoints
  async getSystemInfo(): Promise<SystemInfo> {
    return this.request<SystemInfo>('/system/info')
  }

  async getStorageInfo(): Promise<StorageInfo> {
    return this.request<StorageInfo>('/system/storage')
  }

  async getServiceHealth(): Promise<ServiceHealth> {
    return this.request<ServiceHealth>('/system/health')
  }

  async getLogs(limit: number = 100): Promise<LogEntry[]> {
    return this.request<LogEntry[]>(`/system/logs?limit=${limit}`)
  }

  async downloadLogs(): Promise<void> {
    const url = `${EFFECTIVE_API_BASE}/system/logs/download`
    window.open(url, '_blank')
  }

  // Quality Profile endpoints
  async getQualityProfiles(): Promise<QualityProfile[]> {
    return this.request<QualityProfile[]>('/qualityprofile')
  }

  async getQualityProfileById(id: number): Promise<QualityProfile> {
    return this.request<QualityProfile>(`/qualityprofile/${id}`)
  }

  async getDefaultQualityProfile(): Promise<QualityProfile> {
    return this.request<QualityProfile>('/qualityprofile/default')
  }

  async createQualityProfile(
    profile: Omit<QualityProfile, 'id' | 'createdAt' | 'updatedAt'>,
  ): Promise<QualityProfile> {
    return this.request<QualityProfile>('/qualityprofile', {
      method: 'POST',
      body: JSON.stringify(profile),
    })
  }

  async updateQualityProfile(
    id: number,
    profile: Partial<QualityProfile>,
  ): Promise<QualityProfile> {
    return this.request<QualityProfile>(`/qualityprofile/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ...profile, id }),
    })
  }

  async deleteQualityProfile(id: number): Promise<{ message: string; id: number }> {
    return this.request<{ message: string; id: number }>(`/qualityprofile/${id}`, {
      method: 'DELETE',
    })
  }

  async scoreSearchResults(
    profileId: number,
    searchResults: SearchResult[],
  ): Promise<QualityScore[]> {
    return this.request<QualityScore[]>(`/qualityprofile/${profileId}/score`, {
      method: 'POST',
      body: JSON.stringify(searchResults),
    })
  }

  // Antiforgery token for SPA (calls our new /api/antiforgery/token endpoint)
  async fetchAntiforgeryToken(headersToUse?: Record<string, string>): Promise<string | null> {
    try {
      // If the caller provides headers, use them as the base.
      const headers: Record<string, string> = headersToUse ? { ...headersToUse } : {}

      // If Authorization is present, never attach API key (enforce user session only)
      if (!headers['Authorization']) {
        // Only attach API key if authentication is disabled
        try {
          const sc = await getStartupConfigCached(2000)
          const apiKey = sc?.apiKey
          const rawAuth =
            sc?.authenticationRequired ??
            (sc as unknown as Record<string, unknown>)?.AuthenticationRequired
          const authEnabled =
            typeof rawAuth === 'boolean'
              ? rawAuth
              : typeof rawAuth === 'string'
                ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
                : false
          if (apiKey && !authEnabled) headers['X-Api-Key'] = apiKey
        } catch {}
      }

      logger.debug('[ApiService] fetching antiforgery token', {
        url: `${API_BASE_URL}/antiforgery/token`,
        headers,
      })

      const resp = await fetch(`${API_BASE_URL}/antiforgery/token`, {
        method: 'GET',
        credentials: 'include',
        headers,
      })
      if (!resp.ok) {
        logger.debug('[ApiService] antiforgery token request failed', { status: resp.status })
        return null
      }
      const json = await resp.json()
      const token = json?.token ?? null
      logger.debug('[ApiService] antiforgery token fetched', {
        tokenExists: !!token,
        tokenLength: token ? token.length : 0,
      })
      // Cache the token in memory for the current session
      const sessionToken = sessionTokenManager.getToken() || null;
      this.antiforgeryToken = token;
      this.antiforgeryTokenSession = sessionToken;
      return token
    } catch {
      return null
    }
  }

  // Account login - uses session-based authentication
  async login(
    username: string,
    password: string,
    rememberMe: boolean,
    csrfToken?: string,
  ): Promise<void> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' }
    if (csrfToken) headers['X-XSRF-TOKEN'] = csrfToken
    // Include API key when present so login requests that bypass request() still send the token
    try {
      const sc = await getStartupConfigCached(2000)
      const apiKey = sc?.apiKey
      if (apiKey) headers['X-Api-Key'] = apiKey
    } catch {}

    const resp = await fetch(`${API_BASE_URL}/account/login`, {
      method: 'POST',
      credentials: 'include',
      headers,
      body: JSON.stringify({ username, password, rememberMe }),
    })

    if (resp.status === 429) {
      const body = await resp.json().catch(() => ({}))
      const retryAfter = body?.retryAfterSeconds ?? parseInt(resp.headers.get('Retry-After') || '0')
      const err: ErrorWithStatus = new Error('Too many login attempts')
      err.status = 429
      err.retryAfter = retryAfter
      throw err
    }

    if (!resp.ok) {
      const txt = await resp.text().catch(() => '')
      const err = new Error(`Login failed: ${resp.status} ${txt}`)
      ;(err as ErrorWithStatus).status = resp.status
      throw err
    }

    // Handle session token response (only expected when authentication is required)
    const responseData = await resp.json()
    if (responseData.sessionToken) {
      // Clear antiforgery token cache BEFORE setting session token
      this.antiforgeryToken = null;
      this.antiforgeryTokenSession = null;
      sessionTokenManager.setToken(responseData.sessionToken);
      logger.debug('[ApiService] Session token received and stored');
      if (typeof window !== 'undefined') {
        try { window.localStorage.removeItem('listenarr_csrf_token'); } catch {}
      }
      // Set tokenReadyPromise and resolve after token is fetched
      this.tokenReadyPromise = (async () => {
        try {
          await new Promise(resolve => setTimeout(resolve, 10));
          const token = await this.fetchAntiforgeryToken({ Authorization: `Bearer ${responseData.sessionToken}` });
          logger.debug('[ApiService] Fetched antiforgery token after login', {
            tokenExists: !!token,
            tokenLength: token ? token.length : 0,
            sessionToken: responseData.sessionToken,
          });
        } catch (e) {
          logger.debug('[ApiService] Failed to fetch antiforgery token after login', e);
        }
      })();
      await this.tokenReadyPromise;
      this.tokenReadyPromise = null;
    } else if (responseData.authType === 'none') {
      sessionTokenManager.clearToken();
      this.antiforgeryToken = null;
      this.antiforgeryTokenSession = null;
      logger.debug('[ApiService] Authentication not required - no session token needed');
      this.tokenReadyPromise = (async () => {
        try {
          const token = await this.fetchAntiforgeryToken();
          logger.debug('[ApiService] Fetched antiforgery token after anonymous login', {
            tokenExists: !!token,
            tokenLength: token ? token.length : 0,
          });
        } catch (e) {
          logger.debug('[ApiService] Failed to fetch antiforgery token after anonymous login', e);
        }
      })();
      await this.tokenReadyPromise;
      this.tokenReadyPromise = null;
    } else {
      throw new Error('Login succeeded but expected session token or auth type not received');
    }
  }

  // Public helper to fetch antiforgery token for the current auth state.
  // Call this after any programmatic authentication change (login, API key set)
  // to ensure subsequent unsafe requests have a token bound to the current user.
  async ensureAntiforgeryForCurrentAuth(): Promise<void> {
    try {
      await this.fetchAntiforgeryToken()
    } catch {
      // Swallow here; callers will handle request failures.
    }
  }

  // Current authenticated user (me)
  async getCurrentUser(): Promise<{ authenticated: boolean; name?: string }> {
    return this.request<{ authenticated: boolean; name?: string }>('/account/me')
  }

  async logout(): Promise<void> {
    logger.debug('[ApiService] Making logout request to /account/logout')
    try {
      await this.request<void>('/account/logout', { method: 'POST' })
      logger.debug('[ApiService] Logout request completed successfully')
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ApiService',
        operation: 'logout',
      })
      throw error
    } finally {
      // Always clear session token and antiforgery token on logout
      sessionTokenManager.clearToken()
      this.antiforgeryToken = null;
      this.antiforgeryTokenSession = null;
      logger.debug('[ApiService] Session token cleared')
      // Prefetch antiforgery token for anonymous principal after logout
      try {
        await this.fetchAntiforgeryToken()
        logger.debug('[ApiService] Fetched antiforgery token after logout')
      } catch (e) {
        logger.debug('[ApiService] Failed to fetch antiforgery token after logout', e)
      }
    }
  }

  // Admin users
  async getAdminUsers(): Promise<
    Array<{ id: number; username: string; email?: string; isAdmin: boolean; createdAt: string }>
  > {
    return this.request<
      Array<{ id: number; username: string; email?: string; isAdmin: boolean; createdAt: string }>
    >('/account/admins')
  }
}

export const apiService = new ApiService()

// Export individual indexer functions for convenience
export const getIndexers = () => apiService.getIndexers()
export const getIndexerById = (id: number) => apiService.getIndexerById(id)
export const createIndexer = (indexer: Omit<Indexer, 'id' | 'createdAt' | 'updatedAt'>) =>
  apiService.createIndexer(indexer)
export const updateIndexer = (id: number, indexer: Partial<Indexer>) =>
  apiService.updateIndexer(id, indexer)
export const deleteIndexer = (id: number) => apiService.deleteIndexer(id)
export const testIndexer = (id: number) => apiService.testIndexer(id)
export const testIndexerDraft = (indexer: Omit<Indexer, 'id' | 'createdAt' | 'updatedAt'>) =>
  apiService.testIndexerDraft(indexer)
export const toggleIndexer = (id: number) => apiService.toggleIndexer(id)
export const getEnabledIndexers = () => apiService.getEnabledIndexers()
export const importProwlarrIndexers = (payload: { url: string; port?: number; apiKey: string }) =>
  apiService.importProwlarrIndexers(payload)

// Export individual remote path mapping functions for convenience
export const getRemotePathMappings = () => apiService.getRemotePathMappings()
export const getRemotePathMappingById = (id: number) => apiService.getRemotePathMappingById(id)
export const getRemotePathMappingsByClient = (downloadClientId: string) =>
  apiService.getRemotePathMappingsByClient(downloadClientId)
export const createRemotePathMapping = (
  mapping: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>,
) => apiService.createRemotePathMapping(mapping)
export const updateRemotePathMapping = (id: number, mapping: Partial<RemotePathMapping>) =>
  apiService.updateRemotePathMapping(id, mapping)
export const deleteRemotePathMapping = (id: number) => apiService.deleteRemotePathMapping(id)
export const translatePath = (request: TranslatePathRequest) => apiService.translatePath(request)
// Export individual system functions for convenience
export const getSystemInfo = () => apiService.getSystemInfo()
export const getStorageInfo = () => apiService.getStorageInfo()
export const getServiceHealth = () => apiService.getServiceHealth()
export const getLogs = (limit?: number) => apiService.getLogs(limit)
export const downloadLogs = () => apiService.downloadLogs()

// Export individual quality profile functions for convenience
export const getQualityProfiles = () => apiService.getQualityProfiles()
export const getQualityProfileById = (id: number) => apiService.getQualityProfileById(id)
export const getDefaultQualityProfile = () => apiService.getDefaultQualityProfile()
export const createQualityProfile = (
  profile: Omit<QualityProfile, 'id' | 'createdAt' | 'updatedAt'>,
) => apiService.createQualityProfile(profile)
export const updateQualityProfile = (id: number, profile: Partial<QualityProfile>) =>
  apiService.updateQualityProfile(id, profile)
export const deleteQualityProfile = (id: number) => apiService.deleteQualityProfile(id)
export const scoreSearchResults = (profileId: number, searchResults: SearchResult[]) =>
  apiService.scoreSearchResults(profileId, searchResults)

// Download client helpers
export const testDownloadClient = (config: Partial<DownloadClientConfiguration>) =>
  // The backend test endpoint accepts partial client objects (no id) — cast to any for the lower-level call
  apiService.testDownloadClient(config as any)

// Audimeta helpers
// ...existing code...
// ...existing code...
export const ensureImageCached = apiService.ensureImageCached.bind(apiService);
