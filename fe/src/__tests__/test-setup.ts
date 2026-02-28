/* eslint-disable @typescript-eslint/no-explicit-any */
// Test setup: Polyfill / mock environment pieces that tests expect
// - Provide a Mock WebSocket implementation so SignalR code can run in jsdom

class MockWebSocket {
  static OPEN = 1
  public readyState = MockWebSocket.OPEN
  public onopen: (() => void) | null = null
  public onmessage: ((ev: { data: string }) => void) | null = null
  public onerror: ((err: Error) => void) | null = null
  public onclose: (() => void) | null = null
  private url: string
  constructor(url: string) {
    this.url = url
    // simulate async open
    setTimeout(() => {
      if (this.onopen) this.onopen()
    }, 0)
  }
  send(_data: string) {
    // Reference the arg so linters don't complain about unused params in tests
    void _data
    /* no-op in tests */
  }
  close() {
    if (this.onclose) this.onclose()
  }
}

// Centralized apiService and signalR mocks used by unit tests.
import { vi } from 'vitest'

// Diagnostic: help locate failures during test setup in CI/local runs
try {
  // eslint-disable-next-line no-console
  console.log('[test-setup] initializing test setup')
} catch {}

// Provide default component stubs for Modal teleporting components so unit tests
// render modal content inline instead of using real teleport behavior.
import { config as vtConfig } from '@vue/test-utils'
const globalConfig = ((vtConfig.global ??= {} as any) as any)
globalConfig.components = {
  ...(globalConfig.components || {}),
  // Render modal content inline with accessible dialog attributes so tests
  // can query for role="dialog" and aria-* attributes reliably.
  Modal: {
    template:
      '<div role="dialog" aria-modal="true" aria-labelledby="modal-title"><header id="modal-title"><slot name="header" /></header><div class="modal-body"><slot /></div><footer class="modal-footer"><slot name="footer" /></footer></div>',
  },
  ModalHeader: { template: '<div class="modal-header"><slot /></div>' },
  ModalBody: { template: '<div class="modal-body"><slot /></div>' },

  // Provide lightweight test stubs for commonly used components so unit tests
  // don't fail on missing component resolution for icon or small base pieces.
  LoadingState: {
    props: ['message', 'size'],
    template: '<div class="loading-state"><div class="spinner"/><p v-if="message">{{ message }}</p></div>',
  },
  PhSpinner: {
    props: ['size'],
    template: '<i class="ph-spinner" aria-hidden="true"></i>',
  },
  // Stub the BrandLogo component so tests don't trigger static-asset resolution
  BrandLogo: {
    template: '<div class="brand-logo-stub" />',
  },
}

// Also mock the BrandLogo module at import-time so Vite doesn't compile the real
// SFC (which would try to resolve `/logo.svg` at build/transform time and can
// cause file:/// URL issues in the test runner).
vi.mock('@/components/base/BrandLogo.vue', () => ({
  default: {
    template: '<div class="brand-logo-stub" />',
  },
}))

// Some components import the modal pieces locally (via named imports). To ensure
// tests always render the simplified accessible modal markup (and avoid teleport
// behavior), partially mock the feedback module so SFC-local imports receive the
// inline stubs while preserving other named exports from the real module.
vi.mock('@/components/feedback', async (importOriginal) => {
  const actual = (await importOriginal()) as Record<string, unknown>
  const modalStub: any = {
    emits: ['close'],
    props: ['visible', 'title', 'showClose', 'size'],
    template:
      '<div v-if="visible" v-bind="$attrs" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header id="modal-title"><slot name="header" /></header><div class="modal-body"><slot /></div><footer class="modal-footer"><slot name="footer" /></footer></div>',
    mounted() {
      this._onKey = (e: KeyboardEvent) => {
        if (e.key === 'Escape') this.$emit?.('close')
      }
      document.addEventListener('keydown', this._onKey)
    },
    unmounted() {
      if (this._onKey) document.removeEventListener('keydown', this._onKey)
    },
  }
  return {
    ...actual,
    Modal: modalStub,
    ModalHeader: {
      props: ['title', 'icon', 'iconLabel'],
      emits: ['close'],
      template:
        '<div class="modal-header"><component v-if="icon" :is="icon" /><h2 v-if="title">{{title}}</h2><button @click="$emit(\'close\')" class="close-btn">x</button></div>',
    },
    ModalBody: { template: '<div class="modal-body"><slot /></div>' },
    ModalFooter: { template: '<div class="modal-footer"><slot /></div>' },
  }
})

// Provide both the `apiService` object and common named exports that components
// import directly (e.g. `getRemotePathMappings`, `ensureImageCached`). Tests
// expect these named exports to exist on the mocked module.
vi.mock('@/services/api', () => {
  const apiService = {
    searchAudimetaByTitleAndAuthor: vi.fn(async () => ({ totalResults: 0, results: [] })),
    advancedSearch: async (params: unknown) => {
      const p = params as { title?: string; author?: string } | undefined
      if (p?.title) {
        const mod = await import('@/services/api')
        const svc = mod.apiService as unknown as {
          searchAudimetaByTitleAndAuthor?: (
            title: string,
            author?: string,
          ) => Promise<{ totalResults?: number; results?: unknown[] } | unknown>
        }
        if (svc.searchAudimetaByTitleAndAuthor) {
          const resp = (await svc.searchAudimetaByTitleAndAuthor(p.title, p.author)) as unknown
          const r = resp as any
          return (r?.results) || r || []
        }
        return []
      }
      return { totalResults: 0, results: [] }
    },
    getImageUrl: vi.fn((url: string) => url || ''),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getLibrary: vi.fn(async () => []),
    previewLibraryPath: vi.fn(async () => ({ path: '' })),
    getQualityProfiles: vi.fn(async () => []),
    getApiConfigurations: vi.fn(async () => []),
    // add getRootFolders to apiService so tests that spy on apiService.getRootFolders work
    getRootFolders: vi.fn(async () => []),

    // add checkVolume to apiService so components that call `apiService.checkVolume` in
    // unit tests have a sensible default value that matches the real API signature.
    // Default behaviour: treat paths on the same root as sameVolume and do not break
    // hardlinks.
    checkVolume: vi.fn(async (sourcePath: string, destPath: string) => {
      const same =
        typeof sourcePath === 'string' &&
        typeof destPath === 'string' &&
        // simple heuristic: same leading path segment or same drive letter on Windows
        (sourcePath.split(/[\\/]/)[1] === destPath.split(/[\\/]/)[1] ||
          (/^[A-Za-z]:/.test(sourcePath) && sourcePath[0].toLowerCase() === destPath[0]?.toLowerCase()))
      return {
        sameVolume: Boolean(same),
        willBreakHardlinks: !same,
        sourceVolume: typeof sourcePath === 'string' ? sourcePath.split(/[\\/]/)[1] || '' : undefined,
        destVolume: typeof destPath === 'string' ? destPath.split(/[\\/]/)[1] || '' : undefined,
        message: same ? 'Same volume' : 'Different volumes',
      }
    }),
  }

  // Named exports commonly imported by components/tests
  return {
    apiService,
    // Path/remote helpers
    getRemotePathMappings: vi.fn(async () => []),
    testDownloadClient: vi.fn(async () => ({ success: true })),

    // Image helpers
    ensureImageCached: vi.fn(async (url: string) => url || ''),

    // Logs / files
    getLogs: vi.fn(async () => []),
    downloadLogs: vi.fn(async () => null),

    // Root folders / profiles
    getRootFolders: vi.fn(async () => []),
    getQualityProfiles: vi.fn(async () => []),

    // Keep the startup / app settings helpers available as named exports too
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),

    // Expose checkVolume as a named export as well (delegates to the apiService
    // mock above) so tests that import the function directly behave the same.
    checkVolume: vi.fn(async (sourcePath: string, destPath: string) =>
      (apiService as any).checkVolume(sourcePath, destPath),
    ),
  }
})

vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: () => {},
    onDownloadsList: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onSearchProgress: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onQueueUpdate: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onDownloadUpdate: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onFilesRemoved: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onAudiobookUpdate: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onNotification: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
    onToast: (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    },
  },
}))

// Ensure global WebSocket exists for code that references it
if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
  ;(globalThis as unknown as { WebSocket?: unknown }).WebSocket = MockWebSocket
}

// Also provide a minimal window.WebSocket for code referencing window
if (typeof (window as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
  ;(window as unknown as { WebSocket?: unknown }).WebSocket = MockWebSocket
}

// Provide a noop for console.debug in tests where code wraps in try/catch
if (typeof console.debug !== 'function') console.debug = console.log.bind(console)

// Ensure JSDOM's base URL is HTTP (not file://) so absolute static asset paths
// (e.g. `/logo.svg`) resolve to `http://localhost/...` instead of `file:///...`.
// On Windows the `file:///` form can surface in source-maps and cause Node APIs
// to reject the path; setting the location prevents those file URLs from
// appearing during transforms and stacktrace processing.
try {
  if (typeof window !== 'undefined' && window.location && window.location.href.startsWith('file:')) {
    // Replace file://... base with http://localhost/
    window.history.replaceState({}, '', 'http://localhost/')
  }
  // Also ensure document.baseURI is an HTTP URL (some code consults baseURI directly)
  if (typeof document !== 'undefined' && document.baseURI && document.baseURI.startsWith('file:')) {
    try {
      Object.defineProperty(document, 'baseURI', { value: 'http://localhost/', configurable: true })
    } catch {}
  }
} catch {}

// Provide a simple localStorage polyfill for tests that rely on it
// Ensure a working localStorage implementation exists for tests. Some test
// runners may set a placeholder object; normalize it so .setItem/.getItem exist.
if (
  typeof (globalThis as unknown as { localStorage?: { setItem?: unknown } }).localStorage ===
    'undefined' ||
  typeof (globalThis as unknown as { localStorage?: { setItem?: unknown } }).localStorage
    ?.setItem !== 'function'
) {
  ;(
    globalThis as unknown as {
      localStorage?: {
        _store?: Record<string, string>
        getItem?: (k: string) => string | null
        setItem?: (k: string, v: string) => void
        removeItem?: (k: string) => void
        clear?: () => void
      }
    }
  ).localStorage = {
    _store: {} as Record<string, string>,
    getItem(key: string) {
      return this._store[key] ?? null
    },
    setItem(key: string, value: string) {
      this._store[key] = value + ''
    },
    removeItem(key: string) {
      delete this._store[key]
    },
    clear() {
      this._store = {}
    },
  }
}

// Defensive: JSDOM / Vitest may encounter `file://` asset URLs (e.g. transformed
// static asset paths like `file:///logo.svg`). Some environments propagate
// those to HTMLImageElement.src setters which can trigger Node internal URL/path
// handling and cause tests to crash. Normalize `file://` image URLs to plain
// absolute paths to avoid runtime errors during tests.
try {
  const imgProto = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src')
  Object.defineProperty(HTMLImageElement.prototype, 'src', {
    set(this: HTMLImageElement, value: string) {
      try {
        if (typeof value === 'string' && value.startsWith('file:///')) {
          // Convert file URL (file:///logo.svg) to a usable pathname (/logo.svg)
          const u = new URL(value)
          return imgProto?.set?.call(this, u.pathname)
        }
      } catch {
        // fall through to default setter
      }
      return imgProto?.set?.call(this, value)
    },
    get(this: HTMLImageElement) {
      return imgProto?.get?.call(this)
    },
    configurable: true,
  })

  // Some code sets src via setAttribute('src', ...) which bypasses the property
  // setter. Intercept attribute assignments and normalize file:// URLs for src.
  const origSetAttr = Element.prototype.setAttribute
  Element.prototype.setAttribute = function (name: string, value: string) {
    try {
      if (
        typeof name === 'string' &&
        name.toLowerCase() === 'src' &&
        typeof value === 'string' &&
        value.startsWith('file:///')
      ) {
        const u = new URL(value)
        return origSetAttr.call(this, name, u.pathname)
      }
    } catch {}
    return origSetAttr.call(this, name, value)
  }
} catch {}

// Defensive: some test runners / source-map consumers may attempt to read 'file:///logo.svg'
  // which can throw on Windows / Node file APIs. Normalize any `file:///...` paths to an
  // absolute pathname or return an empty string so tests don't crash during stacktrace
  // or source-map processing.
  try {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const _fs = require('fs') as typeof import('fs')
    const _origRead = _fs.readFile.bind(_fs)
    const _origReadSync = _fs.readFileSync.bind(_fs)
    const _origExistsSync = _fs.existsSync.bind(_fs)
    const _origStatSync = _fs.statSync.bind(_fs)
    const _origRealpathSync = _fs.realpathSync.bind(_fs)
    const _origCreateReadStream = _fs.createReadStream.bind(_fs)
    const _origOpenSync = _fs.openSync ? _fs.openSync.bind(_fs) : undefined
    const _origPromisesRead = _fs.promises && _fs.promises.readFile ? _fs.promises.readFile.bind(_fs.promises) : undefined

    function normalizePathArg(p: unknown) {
      try {
        if (typeof p === 'string' && p.startsWith('file:///')) {
          // Convert 'file:///logo.svg' -> '/logo.svg' (safe for test runtime)
          return new URL(p).pathname
        }
      } catch {}
      return p
    }

    function isProblematicLogoPath(p: unknown) {
      const np = typeof p === 'string' ? p : ''
      return np === 'file:///logo.svg' || np.endsWith('/logo.svg')
    }

    ;(_fs as any).readFile = function (p: any, ...args: any[]) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) {
        const cb = args[args.length - 1]
        if (typeof cb === 'function') return cb(null, '')
        return Promise.resolve('')
      }
      return _origRead(path, ...args)
    }

    ;(_fs as any).readFileSync = function (p: any, ...args: any[]) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) return ''
      return _origReadSync(path, ...args)
    }

    if (_origPromisesRead) {
      ;(_fs as any).promises.readFile = function (p: any, ...args: any[]) {
        const path = normalizePathArg(p)
        if (isProblematicLogoPath(p)) return Promise.resolve('')
        return _origPromisesRead(path, ...args)
      }
    }

    ;(_fs as any).existsSync = function (p: any) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) return true
      return _origExistsSync(path)
    }

    ;(_fs as any).statSync = function (p: any, ...args: any[]) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) return { isFile: () => true, isDirectory: () => false } as any
      return _origStatSync(path, ...args)
    }

    ;(_fs as any).realpathSync = function (p: any, ...args: any[]) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) return path
      return _origRealpathSync(path, ...args)
    }

    ;(_fs as any).createReadStream = function (p: any, ...args: any[]) {
      const path = normalizePathArg(p)
      if (isProblematicLogoPath(p)) return _origCreateReadStream('/dev/null')
      return _origCreateReadStream(path, ...args)
    }

    if (_origOpenSync) {
      ;(_fs as any).openSync = function (p: any, ...args: any[]) {
        const path = normalizePathArg(p)
        if (isProblematicLogoPath(p)) return _origOpenSync(path)
        return _origOpenSync(path, ...args)
      }
    }
  } catch {}
