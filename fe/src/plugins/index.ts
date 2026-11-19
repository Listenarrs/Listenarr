/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { ref, markRaw, type Component } from 'vue'
import type { RouteRecordRaw, Router } from 'vue-router'

/**
 * A frontend plugin contributes an optional always-mounted root component (e.g. a floating
 * player bar) and/or routes. Plugins are loaded at runtime (see runtime.ts) from packages
 * dropped into the host's plugins/ folder — no rebuild required.
 */
export interface PluginNavItem {
  label: string
  to: string
  icon?: Component
}

export interface ListenarrPlugin {
  id: string
  root?: Component
  routes?: RouteRecordRaw[]
  /** Sidebar entries contributed by the plugin. */
  nav?: PluginNavItem[]
}

/** Reactive registry; App.vue renders each plugin's root, router gets each plugin's routes. */
export const plugins = ref<ListenarrPlugin[]>([])

let router: Router | null = null

/** Called once by the app after the router is created, so runtime registrations can add routes. */
export function setPluginRouter(r: Router): void {
  router = r
}

/** Register a plugin at runtime. Idempotent by id. Adds routes to the live router. */
export function registerPlugin(plugin: ListenarrPlugin): void {
  if (!plugin?.id || plugins.value.some((p) => p.id === plugin.id)) {
    return
  }

  const nav = plugin.nav?.map((n) => ({ ...n, icon: n.icon ? markRaw(n.icon) : undefined }))
  plugins.value = [
    ...plugins.value,
    {
      id: plugin.id,
      root: plugin.root ? markRaw(plugin.root) : undefined,
      routes: plugin.routes,
      nav,
    },
  ]

  if (router && plugin.routes) {
    for (const route of plugin.routes) {
      const name = route.name as string | undefined
      if (!name || !router.hasRoute(name)) {
        router.addRoute(route)
      }
    }
  }
}
