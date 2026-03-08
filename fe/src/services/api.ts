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
  AudibleBookMetadata,
  ManualImportPreviewResponse,
  ManualImportRequest,
  ManualImportResult,
  RootFolder,
  QualityScore,
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
} from '@/types'
import { getStartupConfigCached, getCachedStartupConfig, resetCache as resetStartupConfigCache } from './startupConfigCache'
import { sessionTokenManager } from '@/utils/sessionToken'
import { logger } from '@/utils/logger'
import { getRegionFromLanguage } from '@/utils/languageMapping'
import { errorTracking } from '@/services/errorTracking'
import {
  applyApiVersionFromStartupConfig,
  API_BASE_PATH,
  API_BASE_URL,
  API_IMAGES_PATH_PREFIX,
  API_ORIGIN,
  EFFECTIVE_API_BASE,
} from './apiBase'

const getApiImageOrigin = (): string => (import.meta.env.DEV ? '' : API_ORIGIN)
const getApiImagesBaseUrl = (): string => `${getApiImageOrigin()}${API_BASE_PATH}/images`
const ABSOLUTE_URL_REGEX = /^https?:\/\//i

const buildApiRequestUrl = (endpoint: string): string => {
  const raw = (endpoint || '').trim()
  if (!raw) return API_BASE_URL
  if (ABSOLUTE_URL_REGEX.test(raw)) return raw
  const normalized = raw.startsWith('/') ? raw : `/${raw}`
  return `${API_BASE_URL}${normalized}`
}

type ErrorWithStatus = Error & { status?: number; body?: string; retryAfter?: number }

class ApiService {
  private antiforgeryToken: string | null = null;
  private antiforgeryTokenSession: string | null = null;
  private tokenReadyPromise: Promise<void> | null = null;
  private imageBlobFetchInFlight = new Map<string, Promise<Blob>>();
  // Placeholder URL helper moved to '@/utils/placeholder' - import and use that utility instead

  private buildAuthHeaders(): Record<string, string> {
    const headers: Record<string, string> = {}
    try {
      const sc = getCachedStartupConfig()
      const rawAuth = sc?.authenticationRequired ?? sc?.AuthenticationRequired
      const authEnabled =
        typeof rawAuth === 'boolean'
          ? rawAuth
          : typeof rawAuth === 'string'
            ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
            : false

      // Always prefer the session token when present, even before startup
      // config cache is populated. This prevents early authenticated requests
      // (especially protected image fetches) from being sent without auth.
      const sessionToken = sessionTokenManager.getToken()
      if (sessionToken) {
        headers['Authorization'] = `Bearer ${sessionToken}`
      } else if (!authEnabled) {
        const apiKey = sc?.apiKey
        if (apiKey) headers['X-Api-Key'] = apiKey
      }
    } catch {}
    return headers
  }


  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
    // Await tokenReadyPromise before any unsafe request to guarantee fresh token
    const method = (options.method || 'GET').toString().toUpperCase();
    if (["POST", "PUT", "DELETE", "PATCH"].includes(method) && this.tokenReadyPromise) {
      logger.debug('[ApiService] Awaiting tokenReadyPromise before unsafe request');
      await this.tokenReadyPromise;
      this.tokenReadyPromise = null;
    }
    const url = buildApiRequestUrl(endpoint);

    // Build headers
    const headers: Record<string, string> = {
      ...(options.headers ? (options.headers as Record<string, string>) : {}),
    };

    // Attach Authorization or API key if needed
    Object.assign(headers, this.buildAuthHeaders());

    // Attach antiforgery token for unsafe requests
    if (["POST", "PUT", "DELETE", "PATCH"].includes(method)) {
      if (this.antiforgeryToken) {
        headers['X-XSRF-TOKEN'] = this.antiforgeryToken;
      }
    }

    // Always send JSON for POST/PUT unless overridden
    if (["POST", "PUT", "PATCH"].includes(method) && !headers['Content-Type'] && options.body && typeof options.body === 'string') {
      headers['Content-Type'] = 'application/json';
    }

    const config: RequestInit = {
      ...options,
      headers,
      credentials: 'include',
    };

    let resp: Response;
    try {
      resp = await fetch(url, config);
    } catch (err) {
      logger.error('[ApiService] Network error', err);
      throw new Error('Network error');
    }

    if (resp.status === 401) {
      // Unauthorized: clear session token and antiforgery token
      sessionTokenManager.clearToken();
      this.antiforgeryToken = null;
      this.antiforgeryTokenSession = null;
      // Optionally, trigger a global logout or redirect
      throw Object.assign(new Error('Unauthorized'), { status: 401 });
    }

    if (resp.status === 429) {
      // Too many requests
      const body = await resp.json().catch(() => ({}));
      const retryAfter = body?.retryAfterSeconds ?? parseInt(resp.headers.get('Retry-After') || '0');
      const err: ErrorWithStatus = new Error('Too many requests');
      err.status = 429;
      err.retryAfter = retryAfter;
      throw err;
    }

    // If an unsafe request failed with a CSRF error, fetch a fresh antiforgery
    // token and retry. We allow two refresh attempts to handle stale cached
    // tokens that were issued for a prior auth principal.
    if (["POST", "PUT", "DELETE", "PATCH"].includes(method) && resp.status === 400) {
      let text = await resp.text().catch(() => '')
      const looksLikeCsrfFailure = /csrf|xsrf|antiforgery/i.test(text)
      if (looksLikeCsrfFailure) {
        const tokenHeaders: Record<string, string> = {}
        if (headers['Authorization']) tokenHeaders['Authorization'] = headers['Authorization']
        if (headers['X-Api-Key']) tokenHeaders['X-Api-Key'] = headers['X-Api-Key']
        if (headers['x-api-key']) tokenHeaders['x-api-key'] = headers['x-api-key']

        let retryAttempts = 0
        while (retryAttempts < 2) {
          retryAttempts += 1
          const refreshedToken = await this.fetchAntiforgeryToken(tokenHeaders)
          if (!refreshedToken) break

          headers['X-XSRF-TOKEN'] = refreshedToken
          const retryConfig: RequestInit = {
            ...config,
            headers,
          }

          try {
            resp = await fetch(url, retryConfig)
          } catch (err) {
            logger.error('[ApiService] Network error during CSRF retry', err)
            throw new Error('Network error')
          }

          if (resp.status === 401) {
            sessionTokenManager.clearToken();
            this.antiforgeryToken = null;
            this.antiforgeryTokenSession = null;
            throw Object.assign(new Error('Unauthorized'), { status: 401 });
          }

          if (resp.status === 429) {
            const body = await resp.json().catch(() => ({}));
            const retryAfter = body?.retryAfterSeconds ?? parseInt(resp.headers.get('Retry-After') || '0');
            const err: ErrorWithStatus = new Error('Too many requests');
            err.status = 429;
            err.retryAfter = retryAfter;
            throw err;
          }

          if (resp.ok) {
            break
          }

          if (resp.status !== 400) {
            break
          }

          text = await resp.text().catch(() => '')
          if (!/csrf|xsrf|antiforgery/i.test(text)) {
            break
          }
        }

        if (!resp.ok) {
          const err: ErrorWithStatus = new Error(`API error: ${resp.status} ${text}`);
          err.status = resp.status;
          err.body = text;
          throw err;
        }
      } else {
        const err: ErrorWithStatus = new Error(`API error: ${resp.status} ${text}`);
        err.status = resp.status;
        err.body = text;
        throw err;
      }
    }

    if (!resp.ok) {
      const text = await resp.text().catch(() => '');
      const err: ErrorWithStatus = new Error(`API error: ${resp.status} ${text}`);
      err.status = resp.status;
      err.body = text;
      throw err;
    }

    // Try to parse JSON, fallback to text if not JSON
    const contentType = resp.headers.get('content-type') || '';
    if (contentType.includes('application/json')) {
      return await resp.json();
    } else if (contentType.startsWith('text/')) {
      return (await resp.text()) as unknown as T;
    } else {
      return (await resp.blob()) as unknown as T;
    }
  }

  private async refreshStartupConfigCache(): Promise<void> {
    resetStartupConfigCache()
    try {
      await getStartupConfigCached(0)
    } catch {
      // Best effort only; callers should not fail if refresh cannot complete.
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
    config: Partial<DownloadClientConfiguration>,
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
    // Prefer session auth when a token exists, even when startup-config cache is cold.
    // This avoids false 401s immediately after cache reset (login/logout/settings save).
    let authEnabled = false;
    try {
      const cached = getCachedStartupConfig();
      const rawAuth = cached?.authenticationRequired ?? cached?.AuthenticationRequired;
      authEnabled = typeof rawAuth === 'boolean'
        ? rawAuth
        : typeof rawAuth === 'string'
          ? rawAuth.toLowerCase() === 'enabled' || rawAuth.toLowerCase() === 'true'
          : false;
    } catch {}

    const headers: Record<string, string> = {};
    const sessionToken = sessionTokenManager.getToken();
    if (sessionToken) {
      headers['Authorization'] = `Bearer ${sessionToken}`;
    } else if (authEnabled) {
      // Auth is expected to be enabled, but no token is available yet.
      // Leave headers empty so backend can return a typed 401.
    }
    const resp = await fetch(`${API_BASE_URL}/configuration/startupconfig`, {
      method: 'GET',
      credentials: 'include',
      headers,
    });
    if (!resp.ok) {
      const body = await resp.text().catch(() => '')
      const err: ErrorWithStatus = new Error(`Failed to fetch startup config: ${resp.status}`)
      err.status = resp.status
      err.body = body
      throw err
    }
    const config = await resp.json()
    applyApiVersionFromStartupConfig(config)
    return config
  }

    /**
     * Save the startup configuration to the backend.
     * @param config The StartupConfig object to save
     */
    async saveStartupConfig(config: import('@/types').StartupConfig): Promise<{ success: boolean; message?: string }> {
      const result = await this.request<{ success: boolean; message?: string }>('/configuration/startupconfig', {
        method: 'POST',
        body: JSON.stringify(config),
      });
      await this.refreshStartupConfigCache()
      return result
    }



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

  // ISBN -> ASIN lookup
  async getAsinFromIsbn(
    isbn: string,
  ): Promise<{ success: boolean; asin?: string; error?: string }> {
    return this.request<{ success: boolean; asin?: string; error?: string }>(
      `/metadata/asin-from-isbn/${encodeURIComponent(isbn)}`,
    )
  }

  // Audible Metadata API
  async getAudibleMetadata<T>(asin: string): Promise<T> {
    return this.request<T>(`/metadata/${asin}`)
  }

  // Library API
  async getLibrary(): Promise<Audiobook[]> {
    return this.request<Audiobook[]>('/library')
  }

  private normalizeMetadataForApi(
    metadata: AudibleBookMetadata,
  ): Omit<AudibleBookMetadata, 'isbn'> & { isbn?: string[] } {
    const rawIsbn = (metadata as unknown as { isbn?: unknown }).isbn
    const normalizedIsbn = Array.isArray(rawIsbn)
      ? rawIsbn
          .map((v) => (typeof v === 'string' ? v.trim() : String(v ?? '').trim()))
          .filter((v) => v.length > 0)
      : typeof rawIsbn === 'string' && rawIsbn.trim().length > 0
        ? [rawIsbn.trim()]
        : []

    const normalized: Omit<AudibleBookMetadata, 'isbn'> & { isbn?: string[] } = {
      ...(metadata as Omit<AudibleBookMetadata, 'isbn'>),
    }

    if (normalizedIsbn.length > 0) {
      normalized.isbn = normalizedIsbn
    } else {
      delete (normalized as Record<string, unknown>).isbn
    }

    return normalized
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
    const normalizedMetadata = this.normalizeMetadataForApi(metadata)
    const request = {
      metadata: normalizedMetadata,
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
    const normalizedMetadata = this.normalizeMetadataForApi(metadata)
    const body = { metadata: normalizedMetadata, destinationRoot }
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

  async getAudiobookIdentifiers(
    id: number,
  ): Promise<{ audiobookId: number; identifiers: AudiobookExternalIdentifier[] }> {
    return this.request<{ audiobookId: number; identifiers: AudiobookExternalIdentifier[] }>(
      `/library/${id}/identifiers`,
    )
  }

  async updateAudiobookIdentifiers(
    id: number,
    identifiers: AudiobookExternalIdentifierInput[],
  ): Promise<{
    message: string
    audiobook: { id: number; asin?: string; isbn?: string[]; openLibraryId?: string }
    identifiers: AudiobookExternalIdentifier[]
  }> {
    return this.request<{
      message: string
      audiobook: { id: number; asin?: string; isbn?: string[]; openLibraryId?: string }
      identifiers: AudiobookExternalIdentifier[]
    }>(`/library/${id}/identifiers`, {
      method: 'PUT',
      body: JSON.stringify({ identifiers }),
    })
  }

  async rescanAudiobookMetadata(
    id: number,
  ): Promise<{ message: string; audiobookId: number; source?: string; asin?: string; region?: string }> {
    return this.request<{ message: string; audiobookId: number; source?: string; asin?: string; region?: string }>(
      `/library/${id}/rescan-metadata`,
      {
        method: 'POST',
      },
    )
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
    id: number,
    mapping: Partial<RemotePathMapping>,
  ): Promise<RemotePathMapping> {
    return this.request<RemotePathMapping>(`/remotepathmappings/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ...mapping, id }),
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
      // to extract an ASIN-like identifier and map to our image endpoint
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
            let url = `${getApiImagesBaseUrl()}/${encodeURIComponent(identifier)}`
            const params = new URLSearchParams()
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
              let url = `${getApiImagesBaseUrl()}/${encodeURIComponent(identifier)}`
              const params = new URLSearchParams()
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
        return `${getApiImagesBaseUrl()}/${encodeURIComponent(identifier)}`
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
        return `${getApiImagesBaseUrl()}/${encodeURIComponent(identifier)}`
      }
    } catch (e) {
      logger.debug('[ApiService] getImageUrl authors-detect error', e)
    }

    // Convert other relative URLs to absolute (no query-string auth tokens).
    return `${getApiImageOrigin()}${imageUrl}`
  }

  async fetchImageObjectUrl(imageUrl: string | undefined): Promise<string> {
    if (!imageUrl) return ''
    const resolved = this.getImageUrl(imageUrl)
    if (!resolved) return ''

    // Prefer direct same-origin backend image URLs only when auth is not in
    // play. In authenticated mode, <img src="/api/vX/images/..."> cannot attach
    // Authorization headers and will fail with 401.
    try {
      if (typeof window !== 'undefined') {
        const parsed = new URL(resolved, window.location.origin)
        if (parsed.origin === window.location.origin && parsed.pathname.startsWith(API_IMAGES_PATH_PREFIX)) {
          const cfg = getCachedStartupConfig() as Record<string, unknown> | null
          const rawAuth = cfg?.authenticationRequired ?? cfg?.AuthenticationRequired
          const authRequired =
            typeof rawAuth === 'boolean'
              ? rawAuth
              : typeof rawAuth === 'string'
                ? rawAuth.trim().toLowerCase() === 'enabled' || rawAuth.trim().toLowerCase() === 'true'
                : true
          const hasSessionToken = !!sessionTokenManager.getToken()
          if (!authRequired && !hasSessionToken) {
            return `${parsed.pathname}${parsed.search}`
          }
        }
      }
    } catch {
      // If URL parsing fails, continue with existing fetch->blob behavior below.
    }

    // Keep external URLs as-is; auth headers/cors may not be accepted cross-origin.
    if (resolved.startsWith('http://') || resolved.startsWith('https://')) {
      try {
        const u = new URL(resolved)
        if (typeof window !== 'undefined' && u.origin !== window.location.origin) {
          return resolved
        }
      } catch {
        // If URL parsing fails, fall through and try fetch anyway.
      }
    }

    let blobPromise = this.imageBlobFetchInFlight.get(resolved)
    if (!blobPromise) {
      const headers: Record<string, string> = {
        ...this.buildAuthHeaders(),
      }

      blobPromise = (async () => {
        const resp = await fetch(resolved, {
          method: 'GET',
          headers,
          credentials: 'include',
        })

        if (!resp.ok) {
          throw new Error(`Image request failed with status ${resp.status}`)
        }

        return await resp.blob()
      })()

      this.imageBlobFetchInFlight.set(resolved, blobPromise)
    }

    let blob: Blob
    try {
      blob = await blobPromise
    } finally {
      if (this.imageBlobFetchInFlight.get(resolved) === blobPromise) {
        this.imageBlobFetchInFlight.delete(resolved)
      }
    }

    return URL.createObjectURL(blob)
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
      // Expect path like '/api/vX/images/{id}' optionally with query string
      const m = String(path).match(/\/api(?:\/v\d+(?:\.\d+)?)?\/images\/([^\?\/]+)/)
      if (!m || !m[1]) return false
      const id = decodeURIComponent(m[1])

      // Check seeded cache first
      const cached = this.metadataUrlCache.get(id)
      let candidates: string[] = []
      if (cached && Array.isArray(cached.urls) && cached.urls.length > 0) {
        candidates = cached.urls.slice()
      } else {
        // Deprecated metadata endpoints removed; skip dynamic candidate discovery
        // Cache empty candidates for future calls
        this.metadataUrlCache.set(id, { urls: candidates, fetchedAt: Date.now() })
      }

      const requestConfig: RequestInit = {
        method: 'GET',
        headers: {
          ...this.buildAuthHeaders(),
        },
        credentials: 'include',
      }

      // Try each candidate by asking backend to fetch and cache it via /api/vX/images/{id}?url=...
      for (const url of candidates) {
        try {
          const resp = await fetch(
            `${API_BASE_URL}/images/${encodeURIComponent(id)}?url=${encodeURIComponent(url)}`,
            requestConfig,
          )
          if (resp.ok) return true
        } catch {}
      }

      // As a fallback, check the base image endpoint (maybe already cached)
      try {
        const baseResp = await fetch(`${API_BASE_URL}/images/${encodeURIComponent(id)}`, requestConfig)
        if (baseResp.ok) return true
      } catch {}

      return false
    } catch {
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

  // Antiforgery token for SPA (calls /api/vX/antiforgery/token endpoint)
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
      sessionTokenManager.setToken(responseData.sessionToken, { persistent: rememberMe });
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

    await this.refreshStartupConfigCache()
  }

  // Public helper to fetch antiforgery token for the current auth state.
  // Call this after any programmatic authentication change (login, API key set)
  // to ensure subsequent unsafe requests have a token bound to the current user.
  async ensureAntiforgeryForCurrentAuth(): Promise<void> {
    try {
      await this.fetchAntiforgeryToken(this.buildAuthHeaders())
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
      await this.refreshStartupConfigCache()
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

// Compatibility export for legacy code expecting apiService.search
export const search = apiService.advancedSearch.bind(apiService);

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
  apiService.testDownloadClient(config)

// Audimeta helpers
// ...existing code...
// ...existing code...
export const ensureImageCached = apiService.ensureImageCached.bind(apiService);
