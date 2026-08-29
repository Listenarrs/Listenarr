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
import type {
  Download,
  QueueSnapshot,
  QueueUpdatePayload,
  Audiobook,
  RootFolderPathChangeResult,
} from '@/types'
import { sessionTokenManager } from '@/utils/sessionToken'
import { setConnected, setLastError, setReconnectAttempts } from './signalrEvents'
import { logger } from '@/utils/logger'
import { API_BASE_URL } from '@/services/apiBase'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

// SignalR client for real-time download updates
// Using native WebSocket with fallback to long polling

const API_SUFFIX_REGEX = /\/api(?:\/v\d+(?:\.\d+)?)?$/i
const KNOWN_APP_ROUTE_PREFIXES = [
  '/library-import',
  '/audiobooks',
  '/collection',
  '/add-new',
  '/activity',
  '/wanted',
  '/calendar',
  '/downloads',
  '/settings',
  '/system',
  '/logs',
  '/login',
]

const stripApiSuffix = (value: string): string => value.replace(API_SUFFIX_REGEX, '')
const trimTrailingSlash = (value: string): string => value.replace(/\/+$/, '')
const ensureLeadingSlash = (value: string): string => (value.startsWith('/') ? value : `/${value}`)

const normalizePathPrefix = (value: string): string => {
  const trimmed = trimTrailingSlash((value || '').trim())
  if (!trimmed || trimmed === '/') return ''
  return ensureLeadingSlash(trimmed)
}

const toWebSocketOrigin = (httpOrigin: string): string => {
  const trimmed = (httpOrigin || '').trim()
  if (!trimmed) return ''
  if (trimmed.startsWith('https://')) return `wss://${trimmed.slice('https://'.length)}`
  if (trimmed.startsWith('http://')) return `ws://${trimmed.slice('http://'.length)}`
  return trimmed
}

const detectPathPrefixFromLocation = (): string => {
  if (typeof window === 'undefined') return ''
  const pathname = window.location?.pathname || '/'
  if (!pathname || pathname === '/') return ''

  for (const routePrefix of KNOWN_APP_ROUTE_PREFIXES) {
    const idx = pathname.indexOf(routePrefix)
    if (idx > 0) {
      return normalizePathPrefix(pathname.slice(0, idx))
    }
    if (idx === 0) {
      return ''
    }
  }

  // Fallback for subpath root requests (e.g., /listenarr before router navigation).
  const segments = pathname.split('/').filter(Boolean)
  if (segments.length === 1) {
    return normalizePathPrefix(`/${segments[0]}`)
  }

  return ''
}

const resolveHubHttpBase = (): { origin: string; pathPrefix: string } => {
  const browserOrigin =
    typeof window !== 'undefined' && window.location?.origin
      ? window.location.origin
      : 'http://localhost'

  const candidates = [
    (import.meta.env.VITE_API_BASE_URL || '').toString().trim(),
    (API_BASE_URL || '').toString().trim(),
  ]

  for (const candidate of candidates) {
    if (!candidate) continue
    try {
      const url = new URL(candidate, browserOrigin)
      const pathPrefix = normalizePathPrefix(stripApiSuffix(url.pathname || '/'))
      return { origin: url.origin || browserOrigin, pathPrefix }
    } catch {
      // Continue to next candidate.
    }
  }

  return { origin: browserOrigin, pathPrefix: detectPathPrefixFromLocation() }
}

const buildHubWebSocketUrl = (hubPath: '/hubs/downloads' | '/hubs/settings'): string => {
  const resolved = resolveHubHttpBase()
  const origin = toWebSocketOrigin(resolved.origin)
  const prefix = normalizePathPrefix(resolved.pathPrefix)
  return `${origin}${prefix}${hubPath}`
}

const SENSITIVE_QUERY_KEYS = new Set(['access_token', 'token', 'apikey', 'api_key', 'x-api-key'])

const sanitizeWebSocketUrlForLog = (rawUrl: string): string => {
  if (!rawUrl) return rawUrl
  try {
    const parsed = new URL(rawUrl)
    let changed = false
    for (const key of Array.from(parsed.searchParams.keys())) {
      if (SENSITIVE_QUERY_KEYS.has(key.toLowerCase())) {
        parsed.searchParams.set(key, 'redacted')
        changed = true
      }
    }
    return changed ? parsed.toString() : rawUrl
  } catch {
    return rawUrl.replace(
      /([?&](?:access_token|token|apikey|api_key|x-api-key)=)[^&]*/gi,
      '$1redacted',
    )
  }
}

type DownloadUpdateCallback = (downloads: Download[]) => void
type DownloadListCallback = (downloads: Download[]) => void
type QueueUpdateCallback = (queueSnapshot: QueueSnapshot) => void
type ScanJobCallback = (job: {
  jobId: string
  audiobookId?: number | null
  status: string
  found?: number
  created?: number
  error?: string
}) => void

class SignalRService {
  constructor() {
    // Reconnect when browser auth state changes so the hub handshake can pick
    // up the latest session cookie or anonymous/API-key mode.
    try {
      sessionTokenManager.onTokenChange((token, context) => {
        if (context?.source === 'initial') {
          return
        }
        try {
          logger.debug('[SignalR] auth state changed, reconnecting', { authenticated: !!token })
        } catch {}
        // If a connection exists, close it and reconnect after a short delay
        // to avoid racing with other connection lifecycle events.
        try {
          if (this.connection) {
            this.disconnect()
          }
        } catch {}
        setTimeout(() => {
          try {
            this.connect()
          } catch {}
        }, 150)
      })
    } catch {}
  }
  private connection: WebSocket | null = null
  private settingsConnection: WebSocket | null = null
  private reconnectAttempts = 0
  private settingsReconnectAttempts = 0
  private reconnectDelay = 2000
  private maxReconnectDelay = 60000
  private isConnecting = false
  private isSettingsConnecting = false
  private messageId = 0
  private downloadUpdateCallbacks: Set<DownloadUpdateCallback> = new Set()
  private downloadListCallbacks: Set<DownloadListCallback> = new Set()
  private queueUpdateCallbacks: Set<QueueUpdateCallback> = new Set()
  private audiobookUpdateCallbacks: Set<(a: Audiobook) => void> = new Set()
  private scanJobCallbacks: Set<ScanJobCallback> = new Set()
  private moveJobCallbacks: Set<
    (job: {
      jobId: string
      audiobookId?: number
      status: string
      target?: string
      error?: string
      sourceRetained?: boolean
    }) => void
  > = new Set()
  private rootFolderRelocationCallbacks: Set<(update: RootFolderPathChangeResult) => void> =
    new Set()
  private filesRemovedCallbacks: Set<
    (payload: { audiobookId: number; removed: Array<{ id: number; path: string }> }) => void
  > = new Set()
  private searchProgressCallbacks: Map<
    (payload: {
      message: string
      asin?: string | null
      type?: string
      audiobookId?: number
    }) => void,
    boolean
  > = new Map()
  private toastCallbacks: Set<
    (payload: { level: string; title: string; message: string; timeoutMs?: number }) => void
  > = new Set()
  private notificationCallbacks: Set<
    (notification: {
      id: string
      title: string
      message: string
      icon?: string
      timestamp: string
      dismissed?: boolean
    }) => void
  > = new Set()
  private indexersUpdatedCallbacks: Set<
    (payload?: { created?: number; skipped?: number }) => void
  > = new Set()
  private unmatchedScanCompleteCallbacks: Set<
    (payload: { jobId: string; count: number; error?: string }) => void
  > = new Set()
  private pingInterval: number | null = null
  private visibilityListener: (() => void) | null = null
  // Connection state listeners (for UI to subscribe to connect/disconnect events)
  private connectedListeners: Set<() => void> = new Set()
  private disconnectedListeners: Set<() => void> = new Set()

  async connect(): Promise<void> {
    if (this.connection?.readyState === WebSocket.OPEN || this.isConnecting) {
      return
    }

    this.isConnecting = true
    try {
      // Using SignalR protocol over WebSocket
      const hubUrl = buildHubWebSocketUrl('/hubs/downloads')

      if (import.meta.env.DEV) {
        console.log('[SignalR] Connecting to:', sanitizeWebSocketUrlForLog(hubUrl))
      }
      this.connection = new WebSocket(hubUrl)

      this.connection.onopen = () => {
        if (import.meta.env.DEV) {
          console.log('[SignalR] Connected to download hub')
        }
        this.reconnectAttempts = 0
        this.isConnecting = false
        this.sendHandshake()
        this.startPingInterval()
        try {
          for (const cb of Array.from(this.connectedListeners)) {
            try {
              cb()
            } catch {}
          }
        } catch {}
        try {
          setConnected(true)
        } catch {}
        try {
          setReconnectAttempts(0)
        } catch {}

        // Also attempt to establish the settings hub connection so the SPA receives settings-related broadcasts
        try {
          setTimeout(() => {
            try {
              this.connectSettings()
            } catch {}
          }, 100)
        } catch {}
      }

      this.connection.onmessage = (event) => {
        this.handleMessage(event.data)
      }

      this.connection.onerror = (error) => {
        console.error('[SignalR] WebSocket error:', error)
        try {
          setLastError(String(error))
        } catch {}
      }

      this.connection.onclose = () => {
        if (import.meta.env.DEV) {
          console.log('[SignalR] Connection closed')
        }
        this.isConnecting = false
        this.stopPingInterval()
        this.attemptReconnect()
        try {
          for (const cb of Array.from(this.disconnectedListeners)) {
            try {
              cb()
            } catch {}
          }
        } catch {}
        try {
          setConnected(false)
        } catch {}
      }
    } catch (error) {
      console.error('[SignalR] Failed to connect:', error)
      this.isConnecting = false
      this.attemptReconnect()
    }
  }

  private sendHandshake() {
    // SignalR handshake message
    const handshake = {
      protocol: 'json',
      version: 1,
    }
    this.send(JSON.stringify(handshake) + '\x1e')
  }

  private handleMessage(data: string) {
    try {
      // SignalR messages are terminated with \x1e
      const messages = data.split('\x1e').filter((m) => m.length > 0)

      for (const message of messages) {
        const parsed = JSON.parse(message)

        // Handle different message types
        if (parsed.type === 1) {
          // Invocation message from server
          this.handleInvocation(parsed)
        } else if (parsed.type === 2) {
          // Stream item
          if (import.meta.env.DEV) {
            console.log('[SignalR] Stream item:', parsed)
          }
        } else if (parsed.type === 3) {
          // Completion
          if (import.meta.env.DEV) {
            console.log('[SignalR] Completion:', parsed)
          }
        } else if (parsed.type === 6) {
          // Ping
          this.sendPong()
        } else if (parsed.type === 7) {
          // Close
          if (import.meta.env.DEV) {
            console.log('[SignalR] Server requested close')
          }
          this.disconnect()
        } else if (!parsed.type) {
          // Handshake response
          if (import.meta.env.DEV) {
            console.log('[SignalR] Handshake response:', parsed)
          }
        }
      }
    } catch (error) {
      console.error('[SignalR] Error parsing message:', error, data)
    }
  }

  private handleInvocation(message: {
    type: number
    invocationId?: string
    target: string
    arguments: unknown[]
  }) {
    const { target, arguments: args } = message

    if (import.meta.env.DEV) {
      console.log('[SignalR] Received:', target, args)
    }

    switch (target) {
      case 'DownloadUpdate':
        // Single or multiple download updates
        if (args && args[0]) {
          const downloads = Array.isArray(args[0]) ? args[0] : [args[0]]
          this.downloadUpdateCallbacks.forEach((cb) => cb(downloads as Download[]))
        }
        break

      case 'DownloadsList':
        // Full downloads list
        if (args && args[0]) {
          this.downloadListCallbacks.forEach((cb) => cb(args[0] as Download[]))
        }
        break

      case 'QueueUpdate':
        // Queue update from external download clients
        if (args && args[0]) {
          const queueSnapshot = normalizeQueueSnapshot(args[0] as QueueUpdatePayload)
          this.queueUpdateCallbacks.forEach((cb) => cb(queueSnapshot))
        }
        break

      case 'AudiobookUpdate':
        if (args && args[0]) {
          const ab = args[0] as Audiobook
          this.audiobookUpdateCallbacks.forEach((cb) => cb(ab))
        }
        break
      case 'ScanJobUpdate':
        if (args && args[0]) {
          const job = args[0] as unknown as {
            jobId: string
            audiobookId?: number | null
            status: string
            found?: number
            created?: number
            error?: string
          }
          this.scanJobCallbacks.forEach((cb) => cb(job))
        }
        break
      case 'MoveJobUpdate':
        if (args && args[0]) {
          const job = args[0] as unknown as {
            jobId: string
            audiobookId?: number
            status: string
            progress?: number
            phase?: string
            target?: string
            error?: string
            sourceRetained?: boolean
          }
          this.moveJobCallbacks.forEach((cb) => cb(job))
        }
        break
      case 'RootFolderRelocationUpdate':
        if (args && args[0]) {
          const update = args[0] as RootFolderPathChangeResult
          this.rootFolderRelocationCallbacks.forEach((cb) => cb(update))
        }
        break
      case 'FilesRemoved':
        if (args && args[0]) {
          const payload = args[0] as unknown as {
            audiobookId: number
            removed: Array<{ id: number; path: string }>
          }
          this.filesRemovedCallbacks.forEach((cb) => cb(payload))
        }
        break
      case 'SearchProgress':
        if (args && args[0]) {
          const payload = args[0] as {
            message: string
            asin?: string | null
            type?: string
            audiobookId?: number
          }
          // Deliver payload to callbacks that opted-in to automatic messages
          for (const [cb, includeAutomatic] of Array.from(this.searchProgressCallbacks.entries())) {
            try {
              if (payload.type === 'automatic' && !includeAutomatic) continue
              cb(payload)
            } catch {}
          }
        }
        break
      case 'ToastMessage':
        if (args && args[0]) {
          const payload = args[0] as {
            level: string
            title: string
            message: string
            timeoutMs?: number
          }
          this.toastCallbacks.forEach((cb) => cb(payload))
        }
        break
      case 'Notification':
        if (args && args[0]) {
          const notification = args[0] as {
            id: string
            title: string
            message: string
            icon?: string
            timestamp: string
            dismissed?: boolean
          }
          this.notificationCallbacks.forEach((cb) => cb(notification))
        }
        break

      case 'IndexersUpdated':
        if (args && args[0]) {
          const payload = args[0] as {
            created?: number
            skipped?: number
            indexers?: Array<{ id: number; name: string; baseUrl: string }>
          }
          if (import.meta.env.DEV) console.info('[SignalR] IndexersUpdated payload:', payload)
          this.indexersUpdatedCallbacks.forEach((cb) => cb(payload))
        } else {
          if (import.meta.env.DEV) console.info('[SignalR] IndexersUpdated (no payload)')
          this.indexersUpdatedCallbacks.forEach((cb) => cb())
        }
        break

      case 'UnmatchedScanComplete':
        if (args && args[0]) {
          const payload = args[0] as { jobId: string; count: number; error?: string }
          this.unmatchedScanCompleteCallbacks.forEach((cb) => cb(payload))
        }
        break
    }
  }

  private send(data: string) {
    if (this.connection?.readyState === WebSocket.OPEN) {
      this.connection.send(data)
    }
  }

  private sendPong() {
    const pong = { type: 6 }
    this.send(JSON.stringify(pong) + '\x1e')
  }

  private startPingInterval() {
    this.stopPingInterval()
    // If the page is hidden, delay starting the ping interval until visible
    if (typeof document !== 'undefined' && document.hidden) {
      if (!this.visibilityListener) {
        this.visibilityListener = () => {
          if (!document.hidden) {
            this.startPingInterval()
          }
        }
        document.addEventListener('visibilitychange', this.visibilityListener)
      }
      return
    }

    // Send ping every 15 seconds to keep connection alive
    this.pingInterval = window.setInterval(() => {
      const ping = { type: 6 }
      this.send(JSON.stringify(ping) + '\x1e')
    }, 15000)

    // Ensure we have a listener to stop pings when the page becomes hidden
    if (!this.visibilityListener) {
      this.visibilityListener = () => {
        if (document.hidden) this.stopPingInterval()
      }
      document.addEventListener('visibilitychange', this.visibilityListener)
    }
  }

  private stopPingInterval() {
    if (this.pingInterval) {
      clearInterval(this.pingInterval)
      this.pingInterval = null
    }
    if (this.visibilityListener) {
      document.removeEventListener('visibilitychange', this.visibilityListener)
      this.visibilityListener = null
    }
  }

  private attemptReconnect() {
    this.reconnectAttempts++
    try {
      setReconnectAttempts(this.reconnectAttempts)
    } catch {}
    const rawDelay = this.reconnectDelay * Math.pow(1.5, this.reconnectAttempts - 1)
    const cappedDelay = Math.min(this.maxReconnectDelay, rawDelay)
    const jitter = Math.round(cappedDelay * 0.2 * Math.random())
    const delay = cappedDelay + jitter

    if (import.meta.env.DEV) {
      console.log(`[SignalR] Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`)
    }

    setTimeout(() => {
      this.connect()
    }, delay)
  }

  disconnect() {
    this.stopPingInterval()
    if (this.connection) {
      this.connection.close()
      this.connection = null
    }

    // Also close settings hub connection if present
    if (this.settingsConnection) {
      this.settingsConnection.close()
      this.settingsConnection = null
    }
    this.isConnecting = false
    try {
      for (const cb of Array.from(this.disconnectedListeners)) {
        try {
          cb()
        } catch {}
      }
    } catch {}
    try {
      setConnected(false)
    } catch {}
  }

  async connectSettings(): Promise<void> {
    if (this.settingsConnection?.readyState === WebSocket.OPEN || this.isSettingsConnecting) return

    this.isSettingsConnecting = true

    const hubUrl = buildHubWebSocketUrl('/hubs/settings')

    try {
      if (import.meta.env.DEV) {
        console.log('[SignalR] Connecting to settings hub:', sanitizeWebSocketUrlForLog(hubUrl))
      }
      this.settingsConnection = new WebSocket(hubUrl)

      this.settingsConnection.onopen = () => {
        if (import.meta.env.DEV) console.log('[SignalR] Connected to settings hub')
        this.settingsReconnectAttempts = 0
        this.isSettingsConnecting = false
        // Send handshake on the settings connection as well
        const handshake = { protocol: 'json', version: 1 }
        this.sendOn(this.settingsConnection, JSON.stringify(handshake) + '\x1e')
      }

      this.settingsConnection.onmessage = (ev) => {
        // Reuse existing message handler
        this.handleMessage(ev.data)
      }

      this.settingsConnection.onclose = () => {
        if (import.meta.env.DEV) console.log('[SignalR] settings connection closed')
        this.isSettingsConnecting = false
        // Try to reconnect with exponential backoff
        this.settingsReconnectAttempts++
        const rawDelay = this.reconnectDelay * Math.pow(1.5, this.settingsReconnectAttempts - 1)
        const cappedDelay = Math.min(this.maxReconnectDelay, rawDelay)
        const jitter = Math.round(cappedDelay * 0.2 * Math.random())
        const delay = cappedDelay + jitter
        setTimeout(() => this.connectSettings(), delay)
      }

      this.settingsConnection.onerror = (err) => {
        console.error('[SignalR] settings WebSocket error:', err)
      }
    } catch (err) {
      console.error('[SignalR] Failed to connect to settings hub:', err)
      this.isSettingsConnecting = false
      setTimeout(() => this.connectSettings(), this.reconnectDelay)
    }
  }

  private sendOn(ws: WebSocket | null, data: string) {
    try {
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(data)
      }
    } catch (e) {
      console.warn('[SignalR] failed to send on socket', e)
    }
  }

  // Public hooks for consumers to react to connection state changes.
  onConnected(cb: () => void): () => void {
    this.connectedListeners.add(cb)
    return () => {
      this.connectedListeners.delete(cb)
    }
  }

  onDisconnected(cb: () => void): () => void {
    this.disconnectedListeners.add(cb)
    return () => {
      this.disconnectedListeners.delete(cb)
    }
  }

  // Subscribe to download updates
  onDownloadUpdate(callback: DownloadUpdateCallback): () => void {
    this.downloadUpdateCallbacks.add(callback)

    // Return unsubscribe function
    return () => {
      this.downloadUpdateCallbacks.delete(callback)
    }
  }

  // Subscribe to full downloads list
  onDownloadsList(callback: DownloadListCallback): () => void {
    this.downloadListCallbacks.add(callback)

    // Return unsubscribe function
    return () => {
      this.downloadListCallbacks.delete(callback)
    }
  }

  // Subscribe to queue updates (external download clients)
  onQueueUpdate(callback: QueueUpdateCallback): () => void {
    this.queueUpdateCallbacks.add(callback)

    // Return unsubscribe function
    return () => {
      this.queueUpdateCallbacks.delete(callback)
    }
  }

  // Subscribe to audiobook updates (full audiobook object)
  onAudiobookUpdate(callback: (a: Audiobook) => void): () => void {
    this.audiobookUpdateCallbacks.add(callback)
    return () => {
      this.audiobookUpdateCallbacks.delete(callback)
    }
  }

  // Subscribe to scan job updates
  onScanJobUpdate(callback: ScanJobCallback): () => void {
    this.scanJobCallbacks.add(callback)
    return () => {
      this.scanJobCallbacks.delete(callback)
    }
  }

  // Subscribe to move job updates
  onMoveJobUpdate(
    callback: (job: {
      jobId: string
      audiobookId?: number
      status: string
      progress?: number
      phase?: string
      target?: string
      error?: string
      sourceRetained?: boolean
    }) => void,
  ): () => void {
    this.moveJobCallbacks.add(callback)
    return () => {
      this.moveJobCallbacks.delete(callback)
    }
  }

  onRootFolderRelocationUpdate(callback: (update: RootFolderPathChangeResult) => void): () => void {
    this.rootFolderRelocationCallbacks.add(callback)
    return () => {
      this.rootFolderRelocationCallbacks.delete(callback)
    }
  }

  // Subscribe to search progress messages (from server-side search operations)
  // By default clients do NOT receive automatic background search messages. To receive them,
  // pass `includeAutomatic=true`.
  onSearchProgress(
    callback: (payload: {
      message: string
      asin?: string | null
      type?: string
      audiobookId?: number
    }) => void,
    includeAutomatic = false,
  ): () => void {
    this.searchProgressCallbacks.set(callback, includeAutomatic)
    return () => {
      this.searchProgressCallbacks.delete(callback)
    }
  }

  // Subscribe to server-sent toast messages
  onToast(
    callback: (payload: {
      level: string
      title: string
      message: string
      timeoutMs?: number
    }) => void,
  ): () => void {
    this.toastCallbacks.add(callback)
    return () => {
      this.toastCallbacks.delete(callback)
    }
  }

  // Subscribe to notifications (for dropdown/bell icon)
  onNotification(
    callback: (notification: {
      id: string
      title: string
      message: string
      icon?: string
      timestamp: string
      dismissed?: boolean
    }) => void,
  ): () => void {
    this.notificationCallbacks.add(callback)
    return () => {
      this.notificationCallbacks.delete(callback)
    }
  }

  // Subscribe to indexer updates (triggered when indexers are added/modified externally)
  onIndexersUpdated(
    callback: (payload?: {
      created?: number
      skipped?: number
      indexers?: Array<{ id: number; name: string; baseUrl: string }>
    }) => void,
  ): () => void {
    this.indexersUpdatedCallbacks.add(callback)
    return () => {
      this.indexersUpdatedCallbacks.delete(callback)
    }
  }

  // Subscribe to unmatched scan completion notifications
  onUnmatchedScanComplete(
    callback: (payload: { jobId: string; count: number; error?: string }) => void,
  ): () => void {
    this.unmatchedScanCompleteCallbacks.add(callback)
    return () => {
      this.unmatchedScanCompleteCallbacks.delete(callback)
    }
  }

  // Subscribe to files removed notifications
  onFilesRemoved(
    callback: (payload: {
      audiobookId: number
      removed: Array<{ id: number; path: string }>
    }) => void,
  ): () => void {
    this.filesRemovedCallbacks.add(callback)
    return () => {
      this.filesRemovedCallbacks.delete(callback)
    }
  }

  // Request current downloads from server
  requestDownloadsUpdate() {
    const message = {
      type: 1, // Invocation
      invocationId: String(++this.messageId),
      target: 'RequestDownloadsUpdate',
      arguments: [],
    }
    this.send(JSON.stringify(message) + '\x1e')
  }

  get isConnected(): boolean {
    return this.connection?.readyState === WebSocket.OPEN
  }
}

// Singleton instance
export const signalRService = new SignalRService()

// Expose the service on window for E2E tests in development only
if (import.meta.env.DEV) {
  try {
    ;(window as unknown as { signalRService?: SignalRService }).signalRService = signalRService
  } catch {}
}

// Auto-connect when module is imported
signalRService.connect()
