/**
 * Session token management utilities
 */

class SessionTokenManager {
  private static readonly STORAGE_KEY = 'listenarr_session_token'
  private token: string | null = null
  private subscribers: Set<(token: string | null) => void> = new Set()

  constructor() {
    this.loadFromStorage()
    // Keep a listener for legacy localStorage migration scenarios.
    if (typeof window !== 'undefined' && window.addEventListener) {
      window.addEventListener('storage', this.handleStorageEvent)
    }
  }

  private loadFromStorage(): void {
    try {
      const sessionToken = sessionStorage.getItem(SessionTokenManager.STORAGE_KEY)
      if (sessionToken) {
        this.token = sessionToken
        return
      }

      // One-time migration from older localStorage persistence.
      const legacyToken = localStorage.getItem(SessionTokenManager.STORAGE_KEY)
      if (legacyToken) {
        this.token = legacyToken
        sessionStorage.setItem(SessionTokenManager.STORAGE_KEY, legacyToken)
        localStorage.removeItem(SessionTokenManager.STORAGE_KEY)
        return
      }

      this.token = null
    } catch {
      this.token = null
    }
  }

  getToken(): string | null {
    return this.token
  }

  setToken(token: string | null): void {
    this.token = token
    try {
      if (token) {
        sessionStorage.setItem(SessionTokenManager.STORAGE_KEY, token)
        localStorage.removeItem(SessionTokenManager.STORAGE_KEY)
      } else {
        sessionStorage.removeItem(SessionTokenManager.STORAGE_KEY)
        localStorage.removeItem(SessionTokenManager.STORAGE_KEY)
      }
    } catch {
      // Storage might be unavailable
    }
    // Notify subscribers synchronously
    try {
      for (const cb of Array.from(this.subscribers)) cb(this.token)
    } catch {}
  }

  clearToken(): void {
    this.setToken(null)
  }

  hasToken(): boolean {
    return !!this.token
  }

  // Subscribe to token changes.
  onTokenChange(cb: (token: string | null) => void): () => void {
    this.subscribers.add(cb)
    // Call immediately with current value so subscribers have initial state
    try {
      cb(this.token)
    } catch {}
    return () => {
      this.subscribers.delete(cb)
    }
  }

  private handleStorageEvent = (ev: StorageEvent) => {
    try {
      if (!ev) return
      if (ev.key !== SessionTokenManager.STORAGE_KEY) return

      // If newValue is null, token was removed in another tab; update internal
      // token value and notify subscribers.
      try {
        this.token = ev.newValue
      } catch {
        this.token = null
      }

      for (const cb of Array.from(this.subscribers)) {
        try {
          cb(this.token)
        } catch {}
      }
    } catch {}
  }
}

export const sessionTokenManager = new SessionTokenManager()
