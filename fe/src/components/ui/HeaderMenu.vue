<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <!-- A header button and its dropdown list (the notification bell, the jobs cog) -->
  <div ref="rootRef" class="notification-wrapper" :data-testid="testId">
    <button
      class="nav-btn"
      :title="label"
      :aria-label="label"
      aria-haspopup="true"
      :aria-expanded="open"
      @click="open = !open"
    >
      <component
        :is="icon"
        class="notification-inline-icon header-menu-icon"
        :class="{ 'ph-spin': iconSpinning }"
      />
      <span v-if="unreadCount > 0" class="notification-badge">{{ unreadCount }}</span>
    </button>
    <div v-if="open" class="notification-dropdown" role="menu">
      <div class="dropdown-header">
        <strong>{{ label }}</strong>
        <button class="clear-btn" title="Clear" @click.stop="emit('clear')">Clear</button>
      </div>
      <ul class="notification-list">
        <li
          v-for="item in items"
          :key="item.id"
          class="notification-item"
          :data-testid="item.testId"
        >
          <div class="notif-icon">
            <PhSpinner v-if="item.spinner" class="ph-spin" />
            <component :is="iconComponent(item.icon)" v-else-if="iconComponent(item.icon)" />
            <i v-else :class="item.icon"></i>
          </div>
          <div class="notif-content">
            <!-- An item's title leads back to where it was started -->
            <RouterLink
              v-if="item.to"
              :to="item.to"
              class="notif-title notif-title-link"
              @click="open = false"
              >{{ item.title }}</RouterLink
            >
            <div v-else class="notif-title">{{ item.title }}</div>
            <div class="notif-message">{{ item.message }}</div>
            <ProgressBar
              v-if="item.progress != null"
              :value="item.progress"
              variant="activity"
              height="small"
              :show-percentage="item.showProgressPercentage !== false"
              :show-size="false"
              :animating="item.active === true && item.indeterminate !== true"
              :indeterminate="item.indeterminate === true"
            />
            <div v-if="item.timestamp" class="notif-time">{{ formatTime(item.timestamp) }}</div>
          </div>
          <div class="notif-actions">
            <button
              v-if="item.dismissible ?? !item.active"
              class="dismiss-btn"
              title="Dismiss"
              @click.stop="emit('dismiss', item.id)"
            >
              <PhX />
            </button>
          </div>
        </li>
        <li v-if="items.length === 0" class="notification-empty">{{ emptyText }}</li>
      </ul>
      <div class="dropdown-footer">
        <RouterLink :to="footer.to" class="view-all-link" @click="open = false">{{
          footer.label
        }}</RouterLink>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch, type Component } from 'vue'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import { useEventListener } from '@vueuse/core'
import {
  PhCheckCircle,
  PhDownload,
  PhFileMinus,
  PhProhibit,
  PhSpinner,
  PhWarning,
  PhX,
} from '@phosphor-icons/vue'
import { ProgressBar } from '@/components/base'

export interface HeaderMenuItem {
  id: string
  title: string
  message: string
  icon?: string
  timestamp?: string
  // Running: no dismiss button, unless dismissible says otherwise
  active?: boolean
  dismissible?: boolean
  // A spinning wheel instead of the icon, while the item is pending
  spinner?: boolean
  progress?: number
  showProgressPercentage?: boolean
  indeterminate?: boolean
  // Where clicking the title leads (the page the item was started from)
  to?: RouteLocationRaw
  testId?: string
}

const props = defineProps<{
  label: string
  icon: Component
  items: HeaderMenuItem[]
  // Where the items read are kept for the browser session: one key per menu
  readStorageKey: string
  footer: { to: RouteLocationRaw; label: string }
  emptyText: string
  iconSpinning?: boolean
  testId?: string
}>()

const emit = defineEmits<{
  (e: 'dismiss', id: string): void
  (e: 'clear'): void
}>()

const open = ref(false)
const rootRef = ref<HTMLElement | null>(null)

useEventListener(document, 'click', (event: MouseEvent) => {
  if (open.value && rootRef.value && !rootRef.value.contains(event.target as Node))
    open.value = false
})

// ─── Read items ──────────────────────────────────────────────────────────────
// What the list showed while open is read. An item is read under its title, so a new title (a
// job that ends) counts again while a progress or message update doesn't.
const READ_KEYS_KEPT = 200

function readKey(item: HeaderMenuItem): string {
  return `${item.id}\n${item.title}`
}

function loadReadKeys(): string[] {
  try {
    const saved = JSON.parse(sessionStorage.getItem(props.readStorageKey) ?? '[]')
    return Array.isArray(saved) ? saved.filter((key) => typeof key === 'string') : []
  } catch {
    return []
  }
}

const readKeys = ref<string[]>(loadReadKeys())

const unreadCount = computed(() => {
  const read = new Set(readKeys.value)
  return props.items.filter((item) => !read.has(readKey(item))).length
})

function markShownItemsRead() {
  const keys = props.items.map(readKey)
  if (keys.every((key) => readKeys.value.includes(key))) return
  const others = readKeys.value.filter((key) => !keys.includes(key))
  readKeys.value = [...others, ...keys].slice(-READ_KEYS_KEPT)
  try {
    sessionStorage.setItem(props.readStorageKey, JSON.stringify(readKeys.value))
  } catch {
    /* non-fatal */
  }
}

// What the open list shows is read, including what arrives while it is open
watch(
  () => (open.value ? props.items.map(readKey).join('\0') : null),
  (shown) => {
    if (shown !== null) markShownItemsRead()
  },
)

// ─── Display ─────────────────────────────────────────────────────────────────

function formatTime(timestamp: string) {
  try {
    return new Date(timestamp).toLocaleString()
  } catch {
    return timestamp
  }
}

// Icon class names to Phosphor components, when one exists
function iconComponent(icon?: string): Component | null {
  switch (icon) {
    case 'ph ph-file-remove':
      return PhFileMinus
    case 'ph ph-download':
      return PhDownload
    case 'ph ph-check-circle':
      return PhCheckCircle
    case 'ph ph-warning':
      return PhWarning
    case 'ph ph-prohibit':
      return PhProhibit
    default:
      return null
  }
}
</script>

<style scoped>
.notification-wrapper {
  position: relative;
}

.nav-btn {
  background: none;
  border: none;
  color: #ccc;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  position: relative;
  transition: background-color 0.2s;
}

.nav-btn:hover {
  background-color: #3a3a3a;
  color: white;
}

.notification-inline-icon {
  color: #c7cfd6;
  font-size: 20px;
  cursor: pointer;
  border-radius: 6px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.notification-inline-icon:hover {
  background-color: #3a3a3a;
  color: #fff;
}

.notification-badge {
  background-color: #f39c12;
  color: white;
  border-radius: 6px;
  padding: 0.1rem 0.3rem;
  font-size: 0.65rem;
  font-weight: 500;
  position: absolute;
  top: -2px;
  right: -2px;
  min-width: 16px;
  height: 16px;
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 10;
}

.notification-dropdown {
  position: absolute;
  top: 48px;
  right: 0;
  background: #252525;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  min-width: 320px;
  max-width: 400px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.6);
  z-index: 1300;
  max-height: 400px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.dropdown-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid #3a3a3a;
  background: #2a2a2a;
}

.dropdown-header strong {
  color: #fff;
  font-size: 14px;
  font-weight: 500;
}

.clear-btn {
  background: none;
  border: none;
  color: #ccc;
  font-size: 12px;
  cursor: pointer;
  padding: 4px 8px;
  border-radius: 6px;
  transition: background-color 0.2s;
}

.clear-btn:hover {
  background-color: #3a3a3a;
  color: #fff;
}

.notification-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 280px;
  overflow-y: auto;
}

.notification-item {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 12px 16px;
  border-bottom: 1px solid #333;
  transition: background-color 0.2s;
}

.notification-item:hover {
  background-color: #2a2a2a;
}

.notification-item:last-child {
  border-bottom: none;
}

.notif-icon {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #2196f3;
  font-size: 16px;
}

.notif-content {
  flex: 1;
  min-width: 0;
}

.notif-title {
  font-size: 13px;
  font-weight: 500;
  color: #fff;
  margin-bottom: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.notif-title-link {
  display: block;
  text-decoration: none;
}

.notif-title-link:hover {
  color: #2196f3;
  text-decoration: underline;
}

.notif-message {
  font-size: 12px;
  color: #ccc;
  line-height: 1.4;
  overflow: hidden;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  line-clamp: 2;
}

.notif-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.dismiss-btn {
  background: none;
  border: none;
  color: #888;
  font-size: 12px;
  cursor: pointer;
  padding: 2px;
  border-radius: 6px;
  transition: all 0.2s;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
}

.dismiss-btn:hover {
  background-color: #3a3a3a;
  color: #ccc;
}

.notif-time {
  font-size: 10px;
  color: #888;
  flex-shrink: 0;
}

.notification-empty {
  padding: 24px 16px;
  text-align: center;
  color: #888;
  font-size: 13px;
  font-style: italic;
}

.dropdown-footer {
  border-top: 1px solid #3a3a3a;
  background: #2a2a2a;
  padding: 8px 16px;
}

.view-all-link {
  display: inline-block;
  color: #2196f3;
  text-decoration: none;
  font-size: 12px;
  font-weight: 500;
  transition: color 0.2s;
}

.view-all-link:hover {
  color: #42a5f5;
  text-decoration: underline;
}

@media (max-width: 768px) {
  .nav-btn {
    padding: 0.35rem;
  }
}
</style>
