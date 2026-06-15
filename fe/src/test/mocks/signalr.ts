import { vi } from 'vitest'

type SignalRCallback = (...args: unknown[]) => void

export const signalREvents = [
  'connected',
  'disconnected',
  'downloadUpdate',
  'downloadsList',
  'queueUpdate',
  'audiobookUpdate',
  'scanJobUpdate',
  'moveJobUpdate',
  'searchProgress',
  'toast',
  'notification',
  'indexersUpdated',
  'unmatchedScanComplete',
  'filesRemoved',
] as const

export type SignalREvent = (typeof signalREvents)[number]

export type SignalRCallbacks = Record<SignalREvent, Set<SignalRCallback>>

function createCallbackRegistry(): SignalRCallbacks {
  return Object.fromEntries(signalREvents.map((event) => [event, new Set()])) as SignalRCallbacks
}

export function createSignalRServiceMock(overrides: Record<string, unknown> = {}) {
  const callbacks = createCallbackRegistry()
  const subscribe = (event: SignalREvent, callback?: SignalRCallback) => {
    if (callback) callbacks[event].add(callback)
    return () => {
      if (callback) callbacks[event].delete(callback)
    }
  }

  const signalRService = {
    connect: vi.fn(async () => undefined),
    connectSettings: vi.fn(async () => undefined),
    disconnect: vi.fn(() => undefined),
    requestDownloadsUpdate: vi.fn(() => undefined),
    isConnected: false,
    onConnected: vi.fn((callback?: SignalRCallback) => subscribe('connected', callback)),
    onDisconnected: vi.fn((callback?: SignalRCallback) => subscribe('disconnected', callback)),
    onDownloadsList: vi.fn((callback?: SignalRCallback) => subscribe('downloadsList', callback)),
    onSearchProgress: vi.fn((callback?: SignalRCallback) => subscribe('searchProgress', callback)),
    onQueueUpdate: vi.fn((callback?: SignalRCallback) => subscribe('queueUpdate', callback)),
    onDownloadUpdate: vi.fn((callback?: SignalRCallback) => subscribe('downloadUpdate', callback)),
    onFilesRemoved: vi.fn((callback?: SignalRCallback) => subscribe('filesRemoved', callback)),
    onAudiobookUpdate: vi.fn((callback?: SignalRCallback) =>
      subscribe('audiobookUpdate', callback),
    ),
    onNotification: vi.fn((callback?: SignalRCallback) => subscribe('notification', callback)),
    onToast: vi.fn((callback?: SignalRCallback) => subscribe('toast', callback)),
    onMoveJobUpdate: vi.fn((callback?: SignalRCallback) => subscribe('moveJobUpdate', callback)),
    onScanJobUpdate: vi.fn((callback?: SignalRCallback) => subscribe('scanJobUpdate', callback)),
    onIndexersUpdated: vi.fn((callback?: SignalRCallback) =>
      subscribe('indexersUpdated', callback),
    ),
    onUnmatchedScanComplete: vi.fn((callback?: SignalRCallback) =>
      subscribe('unmatchedScanComplete', callback),
    ),
    ...overrides,
  }

  return {
    callbacks,
    signalRService,
    emit(event: SignalREvent, ...args: unknown[]) {
      for (const callback of callbacks[event]) {
        callback(...args)
      }
    },
    reset() {
      for (const callbackSet of Object.values(callbacks)) {
        callbackSet.clear()
      }
      for (const value of Object.values(signalRService)) {
        if (vi.isMockFunction(value)) {
          value.mockClear()
        }
      }
      signalRService.isConnected = false
    },
  }
}

export const signalRServiceMock = createSignalRServiceMock()

export function resetSignalRServiceMock() {
  signalRServiceMock.reset()
}
