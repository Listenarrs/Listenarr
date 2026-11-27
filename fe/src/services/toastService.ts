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
import { reactive } from 'vue'

type Toast = { id: string; level: string; title: string; message: string; timeoutMs?: number }

const state = reactive<{ toasts: Toast[]; callbacks: Set<(t: Toast[]) => void> }>({
  toasts: [],
  callbacks: new Set(),
})

function genId() {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}

function notifySubscribers() {
  for (const cb of state.callbacks) {
    try {
      cb(state.toasts)
    } catch {
      /* swallow subscriber errors */
    }
  }
}

function push(level: string, title: string, message: string, timeoutMs = 5000) {
  const t: Toast = { id: genId(), level, title, message, timeoutMs }
  state.toasts.unshift(t)
  if (state.toasts.length > 6) state.toasts.pop()
  notifySubscribers()
  if (timeoutMs && timeoutMs > 0) {
    setTimeout(() => {
      dismiss(t.id)
    }, timeoutMs)
  }
  return t.id
}

function dismiss(id: string) {
  const idx = state.toasts.findIndex((t) => t.id === id)
  if (idx >= 0) {
    state.toasts.splice(idx, 1)
    notifySubscribers()
  }
}

function subscribe(cb: (toasts: Toast[]) => void) {
  state.callbacks.add(cb)
  // Invoke immediately with current state
  try {
    cb(state.toasts)
  } catch {}
  return () => {
    state.callbacks.delete(cb)
  }
}

export function useToast() {
  return {
    toasts: state.toasts,
    push,
    dismiss,
    subscribe,
    info: (title: string, message: string, timeoutMs?: number) =>
      push('info', title, message, timeoutMs),
    success: (title: string, message: string, timeoutMs?: number) =>
      push('success', title, message, timeoutMs),
    // Warnings and errors are sticky by default (timeoutMs = 0): they carry
    // information the user needs to act on — a 5s auto-dismiss too often vanishes
    // before it can be read. They're dismissed via the toast's close button.
    // Callers can still pass an explicit timeout to opt back into auto-dismiss.
    warning: (title: string, message: string, timeoutMs = 0) =>
      push('warning', title, message, timeoutMs),
    error: (title: string, message: string, timeoutMs = 0) =>
      push('error', title, message, timeoutMs),
  }
}
