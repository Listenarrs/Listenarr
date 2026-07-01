/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * Runtime plugin loader. Exposes the host SDK on window.LISTENARR (the stable contract a
 * plugin builds against), fetches the installed-plugin manifest, and injects each plugin's
 * prebuilt bundle. Plugins self-register via window.LISTENARR.registerPlugin.
 */
import * as Vue from 'vue'
import * as VueRouter from 'vue-router'
import * as Pinia from 'pinia'
import { registerPlugin } from './index'
import { apiService } from '@/services/api'
import { buildApiPath } from '@/services/apiBase'
import { useLibraryStore } from '@/stores/library'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { getPlaceholderUrl } from '@/utils/placeholder'

interface ManifestEntry {
  id: string
  name: string
  version: string
  frontend: string | null
  styles: string[]
}

/**
 * Install the host SDK as window globals. Plugin bundles externalize vue/vue-router/pinia to
 * the LISTENARR_* globals (one shared framework instance) and read host services + the
 * register hook from window.LISTENARR.
 */
export function installPluginSdk(): void {
  const w = window as unknown as Record<string, unknown>
  w.LISTENARR_VUE = Vue
  w.LISTENARR_VUE_ROUTER = VueRouter
  w.LISTENARR_PINIA = Pinia
  w.LISTENARR = {
    version: '1.0',
    registerPlugin,
    // Curated host services a plugin may use, so it never bundles a second copy.
    api: {
      request: <T>(endpoint: string, options?: RequestInit) =>
        apiService.pluginRequest<T>(endpoint, options),
      buildApiPath,
    },
    stores: { useLibraryStore },
    ui: { useProtectedImages, getPlaceholderUrl },
  }
}

function injectStyle(href: string): void {
  if (document.querySelector(`link[data-plugin-style="${href}"]`)) return
  const link = document.createElement('link')
  link.rel = 'stylesheet'
  link.href = href
  link.dataset.pluginStyle = href
  document.head.appendChild(link)
}

function injectScript(src: string): Promise<void> {
  return new Promise((resolve) => {
    if (document.querySelector(`script[data-plugin-src="${src}"]`)) {
      resolve()
      return
    }
    const script = document.createElement('script')
    script.src = src
    script.dataset.pluginSrc = src
    script.onload = () => resolve()
    script.onerror = () => {
      console.error('[plugins] failed to load', src)
      resolve() // one bad plugin must not block the rest
    }
    document.body.appendChild(script)
  })
}

/** Fetch the manifest and load every installed plugin's bundle. Best-effort. */
export async function loadInstalledPlugins(): Promise<void> {
  let manifest: ManifestEntry[]
  try {
    const url = new URL('plugins/manifest', document.baseURI).toString()
    const res = await fetch(url, { credentials: 'include' })
    if (!res.ok) return
    manifest = await res.json()
  } catch {
    return
  }

  for (const entry of manifest) {
    // Version-stamp asset URLs so a plugin update busts the browser cache (otherwise the
    // fixed /plugins/<id>/ui/*.js URL serves the stale bundle after an in-place update).
    for (const href of entry.styles ?? []) injectStyle(withVersion(href, entry.version))
    if (entry.frontend) await injectScript(withVersion(entry.frontend, entry.version))
  }
}

function withVersion(url: string, version: string): string {
  return `${url}${url.includes('?') ? '&' : '?'}v=${encodeURIComponent(version)}`
}
