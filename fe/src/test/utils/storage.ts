import { vi } from 'vitest'

export function installStorageMock() {
  let localStore: Record<string, string> = {}
  let sessionStore: Record<string, string> = {}

  const createStorage = (
    getStore: () => Record<string, string>,
    setStore: (store: Record<string, string>) => void,
  ) => ({
    getItem: vi.fn((key: string) => getStore()[key] ?? null),
    setItem: vi.fn((key: string, value: string) => {
      getStore()[key] = `${value}`
    }),
    removeItem: vi.fn((key: string) => {
      delete getStore()[key]
    }),
    clear: vi.fn(() => {
      setStore({})
    }),
    key: vi.fn((index: number) => Object.keys(getStore())[index] ?? null),
    get length() {
      return Object.keys(getStore()).length
    },
  })

  const localStorageMock = createStorage(
    () => localStore,
    (store) => {
      localStore = store
    },
  )
  const sessionStorageMock = createStorage(
    () => sessionStore,
    (store) => {
      sessionStore = store
    },
  )

  vi.stubGlobal('localStorage', localStorageMock)
  vi.stubGlobal('sessionStorage', sessionStorageMock)

  Object.defineProperty(window, 'localStorage', {
    value: localStorageMock,
    configurable: true,
  })
  Object.defineProperty(window, 'sessionStorage', {
    value: sessionStorageMock,
    configurable: true,
  })

  return {
    localStorage: localStorageMock,
    sessionStorage: sessionStorageMock,
    get localStore() {
      return localStore
    },
    get sessionStore() {
      return sessionStore
    },
  }
}
