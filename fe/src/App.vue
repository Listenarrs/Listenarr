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
  <div id="app" :style="appShellCssVars">
    <div
      v-if="showSecurityWarningBanner"
      class="security-warning-banner"
      role="status"
      aria-live="polite"
    >
      <span class="security-warning-text">
        Authentication is disabled. This mode is intended for trusted local networks only. If this
        app is exposed to the internet, enable Listenarr authentication or protect it with reverse
        proxy authentication.
      </span>
      <button
        class="security-warning-dismiss"
        type="button"
        @click="dismissSecurityWarning"
        aria-label="Dismiss security warning"
        title="Dismiss"
      >
        <PhX />
      </button>
    </div>

    <div
      v-if="showFilesystemInitializationBanner"
      class="filesystem-initialization-banner"
      :class="{ failed: filesystemReadinessStore.filesystemFailed }"
      role="status"
      aria-live="polite"
    >
      <span>{{ filesystemInitializationMessage }}</span>
    </div>

    <!-- Top Navigation Bar -->
    <header
      v-if="!hideLayout"
      class="top-nav"
      :class="{ 'auth-warning-visible': showSecurityWarningBanner }"
    >
      <!-- Mobile menu button -->
      <button
        class="nav-btn mobile-menu-btn"
        @click="toggleMobileMenu"
        aria-label="Toggle navigation menu"
      >
        <PhList class="mobile-menu-icon" />
      </button>
      <div class="nav-brand">
        <RouterLink to="/" class="brand-link" @click="closeMobileMenu">
          <div class="brand-logo-wrap" aria-hidden="true"><BrandLogo /></div>
          <h1>Listenarr</h1>
        </RouterLink>
      </div>
      <div class="nav-actions">
        <!-- Backend connection indicator moved to System view -->
        <!-- Mobile backdrop (real DOM element so clicks reliably close the search) -->
        <div v-if="searchOpen" class="mobile-search-backdrop" @click="closeSearch" />

        <div class="nav-search-inline" ref="navSearchRef" :class="{ open: searchOpen }">
          <input
            v-model="searchQuery"
            @input="onSearchInput"
            @keydown.enter="applyFirstResult"
            @keydown.escape.prevent="closeSearch"
            ref="searchInputRef"
            class="search-input-inline"
            type="search"
            placeholder="Search your library..."
            aria-label="Search your audiobooks"
          />
          <button
            class="nav-btn"
            aria-hidden="true"
            role="button"
            tabindex="0"
            @click="toggleSearch"
            @keydown.enter.prevent="toggleSearch"
          >
            <PhMagnifyingGlass class="search-inline-icon" />
          </button>
          <div class="inline-spinner" v-if="searching" aria-hidden="true"></div>
          <!-- Results overlay: shows suggestions, or a searching/no-results state with spinner -->
          <div
            class="search-results-inline"
            v-if="searching || suggestions.length > 0 || searchQuery.length > 0"
          >
            <ul v-if="suggestions.length > 0" class="search-list">
              <li
                v-for="s in suggestions"
                :key="s.id"
                class="search-result"
                @click="selectSuggestion(s)"
              >
                <div style="display: flex; align-items: center; gap: 10px">
                  <img
                    v-if="s.imageUrl"
                    :src="getProtectedImageSrc(s.imageUrl, getPlaceholderUrl())"
                    @error="handleImageError"
                    alt="cover"
                    class="result-thumb"
                    loading="lazy"
                    decoding="async"
                  />
                  <img v-else :src="getPlaceholderUrl()" alt="cover" class="result-thumb" />
                  <div>
                    <div class="result-title">{{ s.title }}</div>
                    <div class="result-sub">{{ s.author }}</div>
                  </div>
                </div>
              </li>
            </ul>

            <div v-else class="search-empty-overlay">
              <div class="overlay-spinner" v-if="searching" aria-hidden="true"></div>
              <div class="search-empty" v-if="searching">Searching...</div>
              <div class="search-empty" v-else-if="searchQuery.length > 0">No matches</div>
            </div>
          </div>
        </div>
        <div class="notification-wrapper" ref="notificationRef">
          <button
            class="nav-btn"
            @click="toggleNotifications"
            aria-haspopup="true"
            :aria-expanded="notificationsOpen"
          >
            <PhBell class="notification-inline-icon" />
            <span class="notification-badge" v-if="notificationCount > 0">{{
              notificationCount
            }}</span>
          </button>
          <div v-if="notificationsOpen" class="notification-dropdown" role="menu">
            <div class="dropdown-header">
              <strong>Notifications</strong>
              <button class="clear-btn" @click.stop="clearNotifications" title="Clear">
                Clear
              </button>
            </div>
            <ul class="notification-list">
              <li v-for="item in visibleNotifications" :key="item.id" class="notification-item">
                <div class="notif-icon">
                  <component
                    v-if="notificationIconComponent(item.icon)"
                    :is="notificationIconComponent(item.icon)"
                  />
                  <i v-else :class="item.icon"></i>
                </div>
                <div class="notif-content">
                  <div class="notif-title">{{ item.title }}</div>
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
                  <div v-if="item.timestamp" class="notif-time">
                    {{ formatTime(item.timestamp) }}
                  </div>
                </div>
                <div class="notif-actions">
                  <button
                    v-if="!item.active"
                    class="dismiss-btn"
                    @click.stop="dismissNotification(item.id)"
                    title="Dismiss"
                  >
                    <PhX />
                  </button>
                </div>
              </li>
              <li v-if="visibleNotifications.length === 0" class="notification-empty">
                No recent activity
              </li>
            </ul>
            <div class="dropdown-footer">
              <RouterLink to="/activity" class="view-all-link" @click="notificationsOpen = false"
                >View activity</RouterLink
              >
            </div>
          </div>
        </div>
        <template v-if="authEnabled">
          <template v-if="auth.user.authenticated">
            <div class="nav-user" ref="navUserRef">
              <button
                class="nav-btn nav-user-btn"
                @click="toggleUserMenu"
                :aria-expanded="userMenuOpen"
                aria-haspopup="true"
                title="Account"
              >
                <PhUsers class="nav-user-icon" />
              </button>

              <div v-if="userMenuOpen" class="user-menu" role="menu">
                <button class="user-menu-item" role="menuitem" @click="logout">Logout</button>
              </div>
            </div>
          </template>
          <template v-else>
            <RouterLink to="/login" class="nav-btn">Login</RouterLink>
          </template>
        </template>
      </div>
    </header>

    <div
      :class="[
        'app-layout',
        {
          'no-top': hideLayout,
          'auth-warning-visible': showSecurityWarningBanner,
        },
      ]"
    >
      <!-- Sidebar Navigation -->
      <aside
        v-if="!hideLayout"
        class="sidebar"
        :class="{ open: mobileMenuOpen, 'auth-warning-visible': showSecurityWarningBanner }"
        ref="sidebarRef"
      >
        <nav class="sidebar-nav" @click.capture="onNavCapture">
          <div class="nav-section">
            <RouterLink
              :to="{ path: '/audiobooks', query: { group: 'books' } }"
              class="nav-item"
              :class="{
                'router-link-active':
                  route.name === 'home' ||
                  route.name === 'audiobooks' ||
                  pendingNavPath?.startsWith('/audiobooks') ||
                  pendingNavPath === '/',
              }"
              @mouseenter="onPrimaryNavMouseEnter('home', 'audiobooks')"
              @mouseleave="onNavMouseLeave('audiobooks')"
              @focus="onPrimaryNavFocus('home', 'audiobooks')"
              @blur="onNavBlur('audiobooks')"
              @touchstart.passive="preload('home')"
              @click="onPrimaryNavClick('audiobooks')"
            >
              <PhBooks />
              <span>Audiobooks</span>
            </RouterLink>
            <!-- Sub-navigation for Audiobooks grouping (stacked under Audiobooks) -->
            <div
              class="nav-sub"
              @mouseenter="onNavMouseEnter('audiobooks')"
              @mouseleave="onNavMouseLeave('audiobooks')"
              @focusin="onNavFocus('audiobooks')"
              @focusout="onNavBlur('audiobooks')"
              :class="{
                open:
                  hoverNav === 'audiobooks' ||
                  persistentNav === 'audiobooks' ||
                  route.path.startsWith('/audiobooks') ||
                  route.name === 'home' ||
                  route.name === 'audiobooks',
              }"
            >
              <RouterLink
                :to="{ path: '/audiobooks', query: { group: 'books' } }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.query.group === 'books' }"
              >
                <span>Books</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/audiobooks', query: { group: 'authors' } }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.query.group === 'authors' }"
              >
                <span>Authors</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/audiobooks', query: { group: 'series' } }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.query.group === 'series' }"
              >
                <span>Series</span>
              </RouterLink>
            </div>
            <RouterLink
              to="/add-new"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/add-new' }"
              @mouseenter="preload('add-new')"
              @focus="preload('add-new')"
              @touchstart.passive="preload('add-new')"
              @click="closeMobileMenu"
            >
              <PhPlus />
              <span>Add New</span>
            </RouterLink>
            <RouterLink
              to="/calendar"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/calendar' }"
              @mouseenter="preload('calendar')"
              @focus="preload('calendar')"
              @touchstart.passive="preload('calendar')"
              @click="closeMobileMenu"
            >
              <PhCalendar />
              <span>Calendar</span>
            </RouterLink>
            <RouterLink
              to="/library-import"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/library-import' }"
              @mouseenter="preload('library-import')"
              @focus="preload('library-import')"
              @touchstart.passive="preload('library-import')"
              @click="closeMobileMenu"
            >
              <PhFolderOpen />
              <span>Library Import</span>
            </RouterLink>
          </div>

          <div class="nav-section">
            <RouterLink
              to="/activity"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/activity' }"
              @mouseenter="preload('activity')"
              @focus="preload('activity')"
              @touchstart.passive="preload('activity')"
              @click="closeMobileMenu"
            >
              <PhActivity />
              <span>Activity</span>
              <Pill variant="count" v-if="activityCount > 0">{{ activityCount }}</Pill>
            </RouterLink>
            <RouterLink
              to="/wanted"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/wanted' }"
              @mouseenter="preload('wanted')"
              @focus="preload('wanted')"
              @touchstart.passive="preload('wanted')"
              @click="closeMobileMenu"
            >
              <PhHeart />
              <span>Wanted</span>
              <Pill variant="count" v-if="wantedCount > 0">{{ wantedCount }}</Pill>
            </RouterLink>
          </div>

          <div class="nav-section">
            <RouterLink
              to="/settings"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/settings' }"
              @mouseenter="onPrimaryNavMouseEnter('settings', 'settings')"
              @mouseleave="onNavMouseLeave('settings')"
              @focus="onPrimaryNavFocus('settings', 'settings')"
              @blur="onNavBlur('settings')"
              @touchstart.passive="preload('settings')"
              @click="onPrimaryNavClick('settings')"
            >
              <PhGear />
              <span>Settings</span>
            </RouterLink>
            <!-- Sub-navigation for Settings tabs -->
            <div
              class="nav-sub"
              @mouseenter="onNavMouseEnter('settings')"
              @mouseleave="onNavMouseLeave('settings')"
              @focusin="onNavFocus('settings')"
              @focusout="onNavBlur('settings')"
              :class="{
                open:
                  hoverNav === 'settings' ||
                  persistentNav === 'settings' ||
                  route.path === '/settings',
              }"
            >
              <RouterLink
                :to="{ path: '/settings', hash: '#rootfolders' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#rootfolders' }"
              >
                <span>Root Folders</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#indexers' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#indexers' }"
              >
                <span>Indexers</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#clients' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#clients' }"
              >
                <span>Clients</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#quality-profiles' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#quality-profiles' }"
              >
                <span>Quality Profiles</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#notifications' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#notifications' }"
              >
                <span>Notifications</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#bot' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#bot' }"
              >
                <span>Discord Bot</span>
              </RouterLink>
              <RouterLink
                :to="{ path: '/settings', hash: '#general' }"
                class="nav-subitem"
                @click="closeMobileMenu"
                :class="{ active: route.hash === '#general' }"
              >
                <span>General</span>
              </RouterLink>
            </div>
            <RouterLink
              to="/system"
              class="nav-item"
              :class="{ 'router-link-active': pendingNavPath === '/system' }"
              @mouseenter="preload('system')"
              @focus="preload('system')"
              @touchstart.passive="preload('system')"
              @click="closeMobileMenu"
            >
              <PhMonitor />
              <span>System</span>
              <Pill variant="error" v-if="systemIssues > 0">{{ systemIssues }}</Pill>
            </RouterLink>
          </div>
        </nav>
        <div v-if="version && version.length > 0" class="sidebar-footer">
          <span class="sidebar-version-text">v{{ version }}</span>
          <a
            href="https://github.com/Listenarrs/Listenarr"
            target="_blank"
            rel="noopener noreferrer"
            class="sidebar-source-link"
            title="Source code (AGPLv3)"
            aria-label="Source code on GitHub (AGPLv3)"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 24 24"
              width="16"
              height="16"
              aria-hidden="true"
            >
              <path
                d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0 0 24 12c0-6.63-5.37-12-12-12z"
              />
            </svg>
          </a>
        </div>
      </aside>
      <div
        v-if="mobileMenuOpen"
        class="sidebar-backdrop"
        @click="closeMobileMenu"
        aria-hidden="true"
      ></div>

      <!-- Main Content Area -->
      <main :class="['main-content', { 'full-page': hideLayout }]">
        <div v-if="hideLayout" class="fullpage-wrapper">
          <RouterView />
        </div>
        <RouterView v-else v-slot="{ Component }">
          <Transition name="page-fade">
            <component :is="Component" :key="route.name as string" />
          </Transition>
        </RouterView>
      </main>
    </div>

    <!-- Global Notification Modal -->
    <!-- Global Confirm Dialog (centralized) -->
    <ConfirmDialog
      v-model="confirmVisible"
      :title="confirmTitle"
      :message="confirmMessage"
      :confirmText="confirmConfirmText"
      :cancelText="confirmCancelText"
      :danger="confirmDanger"
      @confirm="confirm.confirm"
    />
    <NotificationModal
      :visible="notification.visible"
      :message="notification.message"
      :title="notification.title"
      :type="notification.type"
      :auto-close="notification.autoClose"
      @close="closeNotification"
    />

    <!-- Global toast notifications -->
    <GlobalToast />
  </div>
</template>

<script setup lang="ts">
import { RouterLink, RouterView } from 'vue-router'
import {
  PhMagnifyingGlass,
  PhBell,
  PhX,
  PhUsers,
  PhBooks,
  PhPlus,
  PhActivity,
  PhCalendar,
  PhHeart,
  PhGear,
  PhMonitor,
  PhFileMinus,
  PhDownload,
  PhCheckCircle,
  PhList,
  PhFolderOpen,
} from '@phosphor-icons/vue'
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { useEventListener } from '@vueuse/core'
import { preloadRoute } from '@/router'
// SignalR indicator moved to System view; session token handled where needed
import { useRoute, useRouter } from 'vue-router'
import NotificationModal from '@/components/feedback/NotificationModal.vue'
import ConfirmDialog from '@/components/feedback/ConfirmDialog.vue'
import { useConfirmService } from '@/composables/confirmService'
import { useNotification } from '@/composables/useNotification'
import { useDownloadsStore } from '@/stores/downloads'
import { useLibraryStore } from '@/stores/library'
import { useMoveJobsStore } from '@/stores/moveJobs'
import { useLibraryDeleteOperationsStore } from '@/stores/libraryDeleteOperations'
import { useScanNotificationsStore } from '@/stores/scanNotifications'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { useAuthStore } from '@/stores/auth'
import { apiService } from '@/services/api'
import { getStartupConfigCached } from '@/services/startupConfigCache'
import { handleImageError } from '@/utils/imageFallback'
import { Pill, ProgressBar } from '@/components/base'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { logSessionState, clearAllAuthData } from '@/utils/sessionDebug'
import { signalRService } from '@/services/signalr'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'
import type { QueueItem } from '@/types'
import { ref as vueRef, ref as vueRef2, reactive } from 'vue'
import GlobalToast from '@/components/ui/GlobalToast.vue'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'
import BrandLogo from '@/components/base/BrandLogo.vue'
import {
  SECURITY_WARNING_BANNER_PREF_EVENT,
  SECURITY_WARNING_BANNER_PREF_KEY,
  getSecurityWarningBannerHiddenPreference,
} from '@/utils/securityWarningBannerPreference'

const STARTUP_CONFIG_UPDATED_EVENT = 'listenarr-startup-config-updated'

const { notification, close: closeNotification } = useNotification()
const { getProtectedImageSrc } = useProtectedImages()
const downloadsStore = useDownloadsStore()
const libraryStore = useLibraryStore()
const moveJobsStore = useMoveJobsStore()
const deleteOperationsStore = useLibraryDeleteOperationsStore()
const scanNotificationsStore = useScanNotificationsStore()
const filesystemReadinessStore = useFilesystemReadinessStore()
const auth = useAuthStore()
const authEnabled = ref(false)
const startupConfigLoaded = ref(false)
const securityWarningDismissed = ref(false)
const securityWarningPermanentlyHidden = ref(getSecurityWarningBannerHiddenPreference())
// Hover and persistence state for sidebar subnavs
const hoverNav = ref<string | null>(null)
const persistentNav = ref<string | null>(null)
// Optimistic active state: set immediately on click, cleared after navigation resolves
const pendingNavPath = ref<string | null>(null)
const hoverTimeout = ref<number | null>(null)
const HOVER_CLOSE_DELAY = 200
const sidebarRef = ref<HTMLElement | null>(null)
const hoverSupported = ref(false)
const isTouchDevice = ref(false)

function onPrimaryNavMouseEnter(routeName: string, navName: string) {
  preload(routeName)
  onNavMouseEnter(navName)
}

function onPrimaryNavFocus(routeName: string, navName: string) {
  preload(routeName)
  onNavFocus(navName)
}

function onPrimaryNavClick(navName: string) {
  onNavClick(navName)
  closeMobileMenu()
}

onMounted(() => {
  try {
    hoverSupported.value = !!(
      window.matchMedia && window.matchMedia('(hover: hover) and (pointer: fine)').matches
    )
  } catch {
    hoverSupported.value = false
  }
  try {
    isTouchDevice.value =
      'ontouchstart' in window ||
      ((navigator as unknown as { maxTouchPoints?: number }).maxTouchPoints ?? 0) > 0
  } catch {
    isTouchDevice.value = false
  }

  refreshSecurityWarningBannerPreference()
})

function onNavMouseEnter(name: string) {
  // Only use hover behavior on pointer-capable devices (prevents touch-only devices from triggering)
  if (!hoverSupported.value) return
  if (hoverTimeout.value) {
    clearTimeout(hoverTimeout.value)
    hoverTimeout.value = null
  }
  hoverNav.value = name
}

function onNavMouseLeave(name: string) {
  if (!hoverSupported.value) return
  if (hoverTimeout.value) clearTimeout(hoverTimeout.value)
  hoverTimeout.value = window.setTimeout(() => {
    // if this nav is persistently open, keep it open
    if (persistentNav.value === name) {
      hoverNav.value = name
    } else {
      hoverNav.value = null
    }
    hoverTimeout.value = null
  }, HOVER_CLOSE_DELAY)
}

function onNavFocus(name: string) {
  // Focus should open immediately for keyboard users
  hoverNav.value = name
}

function onNavBlur(name: string) {
  // Blur should behave like mouseleave
  onNavMouseLeave(name)
}

function onNavClick(name: string) {
  // Toggle persistent open state
  persistentNav.value = persistentNav.value === name ? null : name
  hoverNav.value = persistentNav.value || null
}

// Capture nav-item clicks at the nav level to set an optimistic active state immediately,
// before the router guard (which may await async work) resolves.
function onNavCapture(e: MouseEvent) {
  const link = (e.target as HTMLElement).closest('a.nav-item') as HTMLAnchorElement | null
  if (link) {
    pendingNavPath.value = new URL(link.href, window.location.origin).pathname
  }
}

// Close persistent nav when clicking outside sidebar
useEventListener(document, 'click', (e: MouseEvent) => {
  const target = e.target as Node
  if (!sidebarRef.value) return
  if (!sidebarRef.value.contains(target)) {
    persistentNav.value = null
    hoverNav.value = null
  }
})

// Version from API
const version = ref('')

function updateSidebarVersion(health: { version?: string } | null | undefined) {
  const nextVersion = typeof health?.version === 'string' ? health.version.trim() : ''
  if (nextVersion.length > 0) {
    version.value = nextVersion
  }
}

// Global confirm service (app-level modal)
const confirm = useConfirmService()
// Template-safe computed wrappers (unpack refs so Vue/TS typechecks correctly)
const confirmVisible = computed<boolean>({
  get: () => confirm.visible.value,
  set: (v: boolean) => {
    // when consumer sets visible=false via v-model, treat as cancel
    if (!v) confirm.cancel()
  },
})
const confirmTitle = computed(() => confirm.title.value)
const confirmMessage = computed(() => confirm.message.value)
const confirmConfirmText = computed(() => confirm.confirmText.value)
const confirmCancelText = computed(() => confirm.cancelText.value)
const confirmDanger = computed(() => confirm.danger.value)

// Preload helper for route components on user intent (hover/focus/touch)
function preload(name: string) {
  try {
    preloadRoute(name)
  } catch {}
}

// Idle prefetch: warm up non-critical routes when the browser is idle.
// Respects Data Saver and slow connections to avoid wasting bandwidth.
function scheduleIdlePrefetch(names: string[]) {
  try {
    const connection = (
      navigator as unknown as { connection?: { saveData?: boolean; effectiveType?: string } }
    ).connection
    if (connection && (connection.saveData || /2g/.test(connection.effectiveType || ''))) {
      // Device on data-saver or very slow network: skip prefetch
      return
    }
  } catch {
    // ignore
  }

  const doPrefetch = () => {
    for (const n of names) {
      try {
        preload(n)
      } catch {}
    }
  }

  const ric = (
    window as unknown as {
      requestIdleCallback?: (cb: () => void, opts?: { timeout?: number }) => void
    }
  ).requestIdleCallback
  if (typeof ric === 'function') {
    try {
      ric(doPrefetch, { timeout: 3000 })
    } catch {
      setTimeout(doPrefetch, 1500)
    }
  } else {
    setTimeout(doPrefetch, 1500)
  }
}

// User menu (people icon) state
const userMenuOpen = ref(false)
const navUserRef = ref<HTMLElement | null>(null)
const toggleUserMenu = () => {
  userMenuOpen.value = !userMenuOpen.value
}

const handleDocumentClick = (e: MouseEvent) => {
  const el = navUserRef.value
  if (!el) return
  const target = e.target as Node
  if (!el.contains(target)) {
    userMenuOpen.value = false
  }
}

// Mobile menu state
const mobileMenuOpen = ref(false)
const toggleMobileMenu = () => {
  mobileMenuOpen.value = !mobileMenuOpen.value
}
const closeMobileMenu = () => {
  mobileMenuOpen.value = false
}

// Reactive state for badges and counters
const queueItems = ref<QueueItem[]>([])
const wantedCount = computed(
  () => libraryStore.audiobooks.filter((book) => book.wanted === true).length,
)
const systemIssues = ref(0)

// Activity count: Optimized with memoized intermediate computations
// Breaks down complex logic into cacheable steps for 3-5x performance improvement

// Step 1: Use the downloads store's pre-filtered active downloads
// The store already normalizes status casing and returns only active items
// (queued, downloading, paused, processing) so re-filtering here led to
// casing bugs (e.g. 'downloading' !== 'Downloading'). Reuse the store value
// directly and make sure to unwrap the ref in case the store exposes a
// computed/ref instead of a raw array.
import { unref } from 'vue'
const activeDownloads = computed(() => {
  const raw = unref(downloadsStore.activeDownloads)
  return Array.isArray(raw) ? raw : []
})

// Step 2: Count active queue items (memoized)
const activeQueueCount = computed(
  () =>
    queueItems.value.filter((item) => {
      const status = (item.status || '').toString().toLowerCase()
      return status === 'downloading' || status === 'paused' || status === 'queued'
    }).length,
)

// Step 3: Count DDL downloads separately (memoized)
// Treat downloadClientId case-insensitively to be robust against lower/upper-cased values
const ddlDownloadsCount = computed(
  () =>
    activeDownloads.value.filter(
      (d) => ((d && d.downloadClientId) || '').toString().toUpperCase() === 'DDL',
    ).length,
)

// Step 4: Count external client downloads (memoized)
const externalDownloadsCount = computed(
  () => activeDownloads.value.length - ddlDownloadsCount.value,
)

// Step 5: Final activity count (uses cached intermediate results)
const activityCount = computed(() => {
  // Total = DDL (unique) + max(external in downloads, external in queue)
  // This avoids double-counting external clients that appear in both places
  const count =
    ddlDownloadsCount.value + Math.max(externalDownloadsCount.value, activeQueueCount.value)

  logger.debug('App Badge - Activity count calculated', {
    ddl: ddlDownloadsCount.value,
    external: externalDownloadsCount.value,
    queue: activeQueueCount.value,
    total: count,
  })

  return count
})

// Notification dropdown state
const notificationsOpen = vueRef2(false)
const notificationRef = vueRef<HTMLElement | null>(null)
const handleNotificationDocumentClick = (e: MouseEvent) => {
  const el = notificationRef.value
  if (!el) return
  const target = e.target as Node
  if (!el.contains(target)) {
    notificationsOpen.value = false
  }
}

type HistoryNotification = {
  id: string
  title: string
  message: string
  icon?: string
  timestamp?: string
  dismissed?: boolean
  progress?: number
  phase?: string
  active?: boolean
  showProgressPercentage?: boolean
  indeterminate?: boolean
}

const recentNotifications = reactive<HistoryNotification[]>([])
const recentDownloadTitles = ref<Set<string>>(new Set()) // Track recent download titles to avoid spam

const activeMoveNotifications = computed<HistoryNotification[]>(() =>
  moveJobsStore.trackedJobs.map((job) => {
    const audiobookTitle = job.audiobookId
      ? libraryStore.audiobooks.find((book) => book.id === job.audiobookId)?.title
      : undefined
    const target = job.target ? ` to ${job.target}` : ''
    return {
      id: `move-${job.jobId}`,
      title: audiobookTitle ? `Moving ${audiobookTitle}` : 'Moving audiobook',
      message: `${job.phase || 'Preparing move'}${target}`,
      icon: 'ph ph-folder-open',
      progress: job.progress,
      phase: job.phase,
      active: true,
    }
  }),
)

const scanNotifications = computed<HistoryNotification[]>(() =>
  scanNotificationsStore.jobs
    .filter((job) => job.visible && !job.dismissed)
    .sort((left, right) => right.timestamp.localeCompare(left.timestamp))
    .map((job) => {
      const normalizedStatus = job.status.toLowerCase()
      const active = normalizedStatus === 'queued' || normalizedStatus === 'processing'
      const audiobookTitle = job.audiobookId
        ? libraryStore.audiobooks.find((book) => book.id === job.audiobookId)?.title
        : undefined
      const subject = audiobookTitle || 'audiobook folder'
      const title =
        normalizedStatus === 'queued'
          ? `Scan queued: ${subject}`
          : normalizedStatus === 'processing'
            ? `Scanning ${subject}`
            : normalizedStatus === 'completed'
              ? `Scan complete: ${subject}`
              : normalizedStatus === 'superseded'
                ? `Scan stopped: ${subject}`
                : `Scan failed: ${subject}`
      const message =
        normalizedStatus === 'queued'
          ? 'Waiting to scan folder'
          : normalizedStatus === 'processing'
            ? 'Scanning folder'
            : normalizedStatus === 'completed'
              ? job.found != null
                ? `${job.found} file${job.found === 1 ? '' : 's'} found${job.created != null ? ` · ${job.created} added` : ''}`
                : 'Folder scan completed'
              : job.error || 'The folder scan did not complete'

      return {
        id: `scan-${job.jobId}`,
        title,
        message,
        icon: 'ph ph-folder-open',
        timestamp: job.timestamp,
        progress: active ? 0 : undefined,
        active,
        showProgressPercentage: false,
        indeterminate: active,
      }
    }),
)

const deleteNotifications = computed<HistoryNotification[]>(() =>
  deleteOperationsStore.operations
    .filter((operation) => !operation.dismissed)
    .map((operation) => {
      const active = operation.status === 'deleting'
      const isBulk = operation.kind === 'bulk'
      const title = active
        ? isBulk
          ? operation.title
          : `Deleting ${operation.title}`
        : operation.status === 'completed'
          ? isBulk
            ? `Deleted ${operation.deleted} audiobook${operation.deleted === 1 ? '' : 's'}`
            : `Deleted ${operation.title}`
          : isBulk
            ? `Delete incomplete: ${operation.deleted}/${operation.total} audiobooks`
            : `Delete failed: ${operation.title}`
      const message = isBulk
        ? active
          ? operation.currentTitle
            ? `${operation.processed}/${operation.total} · ${operation.currentTitle}`
            : `${operation.processed}/${operation.total}`
          : operation.status === 'completed'
            ? `${operation.deleted}/${operation.total} deleted`
            : `${operation.deleted}/${operation.total} deleted · ${operation.failed} failed${operation.error ? ` · ${operation.error}` : ''}`
        : active
          ? 'Removing audiobook from library'
          : operation.status === 'completed'
            ? 'Removed from library'
            : operation.error || 'Could not remove audiobook from library'

      return {
        id: operation.id,
        title,
        message,
        icon: 'ph ph-file-remove',
        timestamp: operation.startedAt,
        progress: active ? operation.progress : undefined,
        active,
        showProgressPercentage: isBulk,
        indeterminate: active && !isBulk,
      }
    }),
)

const visibleNotifications = computed(() => [
  ...activeMoveNotifications.value,
  ...scanNotifications.value,
  ...deleteNotifications.value,
  ...recentNotifications.filter((notification) => !notification.dismissed),
])

const notificationCount = computed(() => visibleNotifications.value.length)

function pushNotification(n: HistoryNotification) {
  // Ensure new notifications are not dismissed
  const notification = { ...n, dismissed: false }
  // Keep a max of 10 items
  recentNotifications.unshift(notification)
  if (recentNotifications.length > 10) recentNotifications.pop()
}

function clearNotifications() {
  recentNotifications.length = 0
  recentDownloadTitles.value.clear()
  deleteOperationsStore.clearFinished()
  scanNotificationsStore.clearFinished()
}

function dismissNotification(id: string) {
  deleteOperationsStore.dismiss(id)
  if (id.startsWith('scan-')) {
    scanNotificationsStore.dismiss(id.slice('scan-'.length))
  }
  const notification = recentNotifications.find((n) => n.id === id)
  if (notification) {
    notification.dismissed = true
  }
}

function toggleNotifications() {
  notificationsOpen.value = !notificationsOpen.value
}

// Format timestamp for display - reuse the same formatTime helper used elsewhere
function formatTime(ts: string) {
  try {
    const d = new Date(ts)
    return d.toLocaleString()
  } catch {
    return ts
  }
}

// Map legacy notification icon class strings to Ph components when possible.
function notificationIconComponent(icon?: string) {
  if (!icon) return null
  switch (icon) {
    case 'ph ph-file-remove':
      return PhFileMinus
    case 'ph ph-download':
      return PhDownload
    case 'ph ph-check-circle':
      return PhCheckCircle
    default:
      return null
  }
}

let unsubscribeQueue: (() => void) | null = null
let unsubscribeFilesRemoved: (() => void) | null = null
let unsubscribeScanJobs: (() => void) | null = null
let unsubscribeSignalRConnected: (() => void) | null = null
let scanStatusReconcileTimer: ReturnType<typeof setInterval> | null = null
let scanStatusReconcileInFlight = false

const hasActiveVisibleScan = () =>
  auth.user.authenticated &&
  scanNotificationsStore.jobs.some((job) => {
    const status = job.status.toLowerCase()
    return job.visible && !job.dismissed && (status === 'queued' || status === 'processing')
  })

const stopScanStatusReconciliation = () => {
  if (scanStatusReconcileTimer != null) {
    window.clearInterval(scanStatusReconcileTimer)
    scanStatusReconcileTimer = null
  }
}

const reconcileActiveScanStatuses = async () => {
  if (scanStatusReconcileInFlight) return

  const activeJobs = scanNotificationsStore.jobs.filter((job) => {
    const status = job.status.toLowerCase()
    return job.visible && !job.dismissed && (status === 'queued' || status === 'processing')
  })
  if (activeJobs.length === 0) {
    stopScanStatusReconciliation()
    return
  }

  scanStatusReconcileInFlight = true
  try {
    await Promise.all(
      activeJobs.map(async (job) => {
        try {
          const status = await apiService.getScanJobStatus(job.jobId)
          scanNotificationsStore.applyUpdate({
            jobId: job.jobId,
            audiobookId: status.audiobookId,
            status: status.status,
            error: status.error,
          })
        } catch (error) {
          const status =
            error && typeof error === 'object' && 'status' in error
              ? Number((error as { status?: unknown }).status)
              : undefined
          if (status === 404) {
            scanNotificationsStore.applyUpdate({
              jobId: job.jobId,
              audiobookId: job.audiobookId,
              status: 'Failed',
              error:
                'Scan status is no longer available. Refresh the audiobook to verify the current files.',
            })
            return
          }

          logger.debug('Unable to reconcile scan job status', { jobId: job.jobId, error })
        }
      }),
    )
  } finally {
    scanStatusReconcileInFlight = false
    if (!hasActiveVisibleScan()) {
      stopScanStatusReconciliation()
    }
  }
}

const syncScanStatusReconciliation = () => {
  if (!hasActiveVisibleScan()) {
    stopScanStatusReconciliation()
    return
  }

  if (scanStatusReconcileTimer == null) {
    void reconcileActiveScanStatuses()
    scanStatusReconcileTimer = window.setInterval(() => {
      void reconcileActiveScanStatuses()
    }, 1500)
  }
}

watch(
  () =>
    scanNotificationsStore.jobs
      .map((job) => `${job.jobId}:${job.status}:${job.visible}:${job.dismissed === true}`)
      .join('|'),
  syncScanStatusReconciliation,
  { flush: 'post' },
)

const syncLibrarySnapshot = async () => {
  try {
    await libraryStore.fetchLibrary()
  } catch (err) {
    logger.error('Failed to sync library snapshot:', err)
  }
}

// Methods for nav actions
// Inline search is always visible in header; focus on mount if needed

// --- Header search implementation ---
const router = useRouter()
// Clear the optimistic pending nav state once navigation fully resolves
router.afterEach(() => {
  pendingNavPath.value = null
})
const searchQuery = vueRef('')
const suggestions = vueRef<
  Array<{ id: number; title: string; author?: string; imageUrl?: string }>
>([])
const searching = vueRef(false)
const searchInputRef = vueRef<HTMLInputElement | null>(null)

// Slide-out search state and refs
const navSearchRef = vueRef<HTMLElement | null>(null)
const searchOpen = vueRef(false)

const toggleSearch = () => {
  searchOpen.value = !searchOpen.value
  if (searchOpen.value) {
    // Wait for DOM update then focus input
    nextTick(() => searchInputRef.value?.focus())
  }
}

const closeSearch = () => {
  if (searchOpen.value) {
    searchOpen.value = false
  }
}

const handleSearchDocumentClick = (e: MouseEvent) => {
  const el = navSearchRef.value
  if (!el) return
  const target = e.target as Node
  // Close search if clicking outside the search container (on mobile overlay)
  if (!el.contains(target)) {
    searchOpen.value = false
  }
}

let searchDebounceTimer: number | undefined
const onSearchInput = async () => {
  if (searchDebounceTimer) clearTimeout(searchDebounceTimer)
  const q = searchQuery.value.trim()
  if (q.length === 0) {
    suggestions.value = []
    return
  }
  searchDebounceTimer = window.setTimeout(async () => {
    searching.value = true
    try {
      if (libraryStore.audiobooks.length === 0) {
        await libraryStore.fetchLibrary()
      }
      const lib = libraryStore.audiobooks
      const lower = q.toLowerCase()
      const localMatches = lib.filter(
        (b) =>
          (b.title || '').toLowerCase().includes(lower) ||
          (Array.isArray(b.authors) ? b.authors.join(' ').toLowerCase() : '').includes(lower),
      )
      if (localMatches.length > 0) {
        // Only show local library matches in the header search
        suggestions.value = localMatches.slice(0, 8).map((b) => ({
          id: b.id!,
          title: b.title || 'Unknown',
          author: Array.isArray(b.authors) ? b.authors[0] || '' : '',
          imageUrl: b.imageUrl || '',
        }))
      } else {
        // No fallback to indexers from header search; leave suggestions empty
        suggestions.value = []
      }
    } catch (err) {
      logger.error('Header search failed', err)
      suggestions.value = []
    } finally {
      searching.value = false
    }
  }, 250)
}

const selectSuggestion = (s: { id: number; title: string; author?: string }) => {
  // Navigate to audiobook detail if local (id > 0), else open search view
  if (!s) return
  searchQuery.value = ''
  suggestions.value = []
  if (s.id && s.id > 0) {
    // Navigate to audiobook detail page (router name: 'audiobook-detail')
    void router.push({ name: 'audiobook-detail', params: { id: String(s.id) } })
  } else {
    // Use the general search page for indexer results
    void router.push({ name: 'search', query: { q: s.title } })
  }
}

const applyFirstResult = () => {
  if (suggestions.value.length > 0) selectSuggestion(suggestions.value[0]!)
}

watch(
  () => suggestions.value.length,
  () => {
    // Native lazy loading covers search suggestions automatically
  },
)

const parseAuthEnabledFromStartupConfig = (raw: unknown): boolean | null => {
  if (typeof raw === 'boolean') return raw
  if (typeof raw === 'string') {
    const normalized = raw.toLowerCase().trim()
    if (
      normalized === 'enabled' ||
      normalized === 'true' ||
      normalized === 'yes' ||
      normalized === '1'
    )
      return true
    if (
      normalized === 'disabled' ||
      normalized === 'false' ||
      normalized === 'no' ||
      normalized === '0'
    )
      return false
  }
  return null
}

const refreshAuthPresentationFromStartupConfig = async (force: boolean = false) => {
  try {
    // Use cached startup-config helper so unauthenticated 401 is interpreted as
    // "authentication required" instead of forcing authEnabled=false.
    let cfg = await getStartupConfigCached(force ? 0 : 5000)
    // If cache currently holds a transient failure (`null`), force a direct fetch once
    // so we don't pin authEnabled=false for the whole session.
    if (!cfg) {
      try {
        cfg = await apiService.getBootstrapConfig()
      } catch (err) {
        throw err
      }
    }
    const obj = cfg as Record<string, unknown> | null
    const raw = obj ? (obj['authenticationRequired'] ?? obj['AuthenticationRequired']) : undefined
    const parsedAuthEnabled = parseAuthEnabledFromStartupConfig(raw)
    // Only show the "auth disabled" banner when startup config explicitly says auth is off.
    // Unknown/missing/transient states should not be treated as disabled.
    authEnabled.value = parsedAuthEnabled ?? true
    logger.debug('Startup config refreshed', { authEnabled: authEnabled.value, cfg, force })
  } catch {
    // Avoid false-positive no-auth warning banner when startup config fetch is transiently unavailable.
    authEnabled.value = true
  } finally {
    startupConfigLoaded.value = true
  }
}

watch(
  () => auth.user.authenticated,
  () => {
    void refreshAuthPresentationFromStartupConfig(true)
    syncScanStatusReconciliation()
  },
)

// (notificationRef and click-outside handler are declared earlier)

// Initialize: Subscribe to SignalR for real-time updates (NO POLLING!)
onMounted(async () => {
  filesystemReadinessStore.start()
  logger.debug('Initializing real-time updates via SignalR...')

  // Session debugging utilities
  logSessionState('App Mount - Initial State')

  // Verify session is valid before proceeding
  logger.debug('Verifying session state...')
  try {
    // Check if we have valid session/authentication
    const sessionCheck = await apiService.getServiceHealth()
    updateSidebarVersion(sessionCheck)
    logger.debug('Session verification successful:', sessionCheck)
  } catch (sessionError) {
    logger.warn('Session verification failed:', String(sessionError))
    // If we get 401/403, clear any stale auth state
    const status =
      sessionError && typeof sessionError === 'object' && 'status' in sessionError
        ? sessionError.status
        : 0
    if (status === 401 || status === 403) {
      logger.debug('Clearing stale authentication state due to session error')
      auth.user.authenticated = false
      // Use the comprehensive clear function
      clearAllAuthData()
    }
  }

  // Load current auth state before touching protected endpoints
  await auth.loadCurrentUser()

  // Ensure SignalR connects (or reconnects) after auth state is loaded so any
  // session cookie or API key can be applied to the handshake.
  try {
    await signalRService.connect()
  } catch (e) {
    logger.debug('SignalR connect after auth failed (will retry):', e)
  }

  // Log session state after authentication attempt
  logSessionState('App Mount - After Auth Load')

  // If authenticated, load protected resources and enable real-time updates
  if (auth.user.authenticated) {
    // Keep durable move jobs globally visible so the notification dropdown can
    // show progress even when the Activity page is not mounted.
    moveJobsStore.start()

    // Hydrate the app once, then keep it current from SignalR updates.
    await Promise.all([downloadsStore.loadDownloads(), syncLibrarySnapshot()])

    unsubscribeSignalRConnected = signalRService.onConnected(() => {
      if (auth.user.authenticated) {
        void syncLibrarySnapshot()
        void moveJobsStore.loadActiveJobs()
      }
    })

    // Subscribe to queue updates via SignalR (real-time, no polling!)
    unsubscribeQueue = signalRService.onQueueUpdate((queue) => {
      const queueSnapshot = normalizeQueueSnapshot(queue)
      logger.debug('Received queue update via SignalR:', queueSnapshot.items.length, 'items')
      queueItems.value = queueSnapshot.items
    })

    unsubscribeScanJobs = signalRService.onScanJobUpdate((job) => {
      scanNotificationsStore.applyUpdate(job)
    })

    // Prepare toast helper for this mounted scope
    const toast = useToast()

    // Subscribe to files-removed notifications so we can inform the user
    unsubscribeFilesRemoved = signalRService.onFilesRemoved((payload) => {
      try {
        const removed = Array.isArray(payload?.removed) ? payload.removed.map((r) => r.path) : []
        const display =
          removed.length > 0 ? removed.join(', ') : 'Files were removed from a library item.'
        toast.info('Files removed', display, 6000)
        // Push into recent notifications
        pushNotification({
          id: `files-removed-${Date.now()}`,
          title: 'Files removed',
          message: display,
          icon: 'ph ph-file-remove',
          timestamp: new Date().toISOString(),
        })
      } catch (err) {
        logger.error('Error handling FilesRemoved payload', err)
      }
    })

    // Subscribe to server-sent toast messages and forward to toastService
    signalRService.onToast((payload) => {
      try {
        const lvl = (payload?.level || 'info').toLowerCase()
        const title = payload?.title || ''
        const msg = payload?.message || ''
        const timeout = payload?.timeoutMs
        if (lvl === 'success') toast.success(title, msg, timeout)
        else if (lvl === 'warning') toast.warning(title, msg, timeout)
        else if (lvl === 'error') toast.error(title, msg, timeout)
        else toast.info(title, msg, timeout)
      } catch (e) {
        logger.error('Toast dispatch error', e)
      }
    })

    // Subscribe to notifications (for dropdown/bell icon)
    signalRService.onNotification((notification) => {
      try {
        pushNotification(notification)
      } catch (e) {
        logger.error('Notification dispatch error', e)
      }
    })

    // Subscribe to download updates for notification purposes.
    // Only create notifications for meaningful lifecycle events: start (Queued)
    // and completion (Completed). Do not create notifications for continuous
    // progress updates to avoid flooding the notification list.
    signalRService.onDownloadUpdate((downloads) => {
      try {
        if (!downloads || downloads.length === 0) return
        for (const d of downloads) {
          // Normalize status (some backends may use different casing)
          const status = (d.status || '').toString().toLowerCase()
          const title = d.title || 'Unknown'

          if (status === 'queued') {
            pushNotification({
              id: `dl-start-${d.id}-${Date.now()}`,
              title: title || 'Download started',
              message: `Download started: ${title}`,
              icon: 'ph ph-download',
              timestamp: new Date().toISOString(),
            })
          } else if (status === 'completed' || status === 'ready') {
            // Avoid spamming notifications for the same title
            // Only notify if we haven't notified about this title recently
            if (!recentDownloadTitles.value.has(title)) {
              pushNotification({
                id: `dl-complete-${d.id}-${Date.now()}`,
                title: title || 'Download complete',
                message: `Download completed: ${title}`,
                icon: 'ph ph-check-circle',
                timestamp: new Date().toISOString(),
              })
              // Track this title and clear it after 30 seconds
              recentDownloadTitles.value.add(title)
              setTimeout(() => {
                recentDownloadTitles.value.delete(title)
              }, 30000)
            }
          } else {
            // Ignore progress/other transient updates
          }
        }
      } catch (err) {
        logger.error('DownloadUpdate notif error', err)
      }
    })

    // Fetch initial queue state
    try {
      const initialQueue = await apiService.getQueue()
      queueItems.value = normalizeQueueSnapshot(initialQueue).items
    } catch (err) {
      logger.error('Failed to fetch initial queue:', err)
    }
  } else {
    logger.debug('User not authenticated; skipping protected resource loads')
  }

  // Fallback: if queueItems still empty after the protected load above (tests or edge cases), try a direct fetch.
  try {
    if (!queueItems.value || queueItems.value.length === 0) {
      const fallbackQueue = await apiService.getQueue()
      const fallbackSnapshot = normalizeQueueSnapshot(fallbackQueue)
      if (fallbackSnapshot.items.length > 0) {
        queueItems.value = fallbackSnapshot.items
        logger.debug('Fallback fetched initial queue items', {
          count: fallbackSnapshot.items.length,
        })
      }
    }
  } catch (err) {
    logger.debug('Fallback queue fetch failed (non-fatal)', err)
  }

  logger.info('✅ Real-time updates enabled - Activity badge updates automatically via SignalR!')
  await refreshAuthPresentationFromStartupConfig(true)

  // If the initial health check did not provide a version, fall back to one
  // late fetch instead of making the sidebar wait on the entire bootstrap path.
  if (!version.value) {
    try {
      const health = await apiService.getServiceHealth()
      updateSidebarVersion(health)
    } catch (err) {
      logger.warn('Failed to fetch version from API:', err)
    }
  }

  // Schedule idle-time prefetch for non-critical routes (low-priority)
  try {
    // Prefetch settings and system plus downloads and activity which are common
    scheduleIdlePrefetch(['settings', 'system', 'downloads', 'activity'])
  } catch {
    /* noop */
  }

  // Use VueUse for automatic event listener cleanup
  useEventListener(document, 'click', handleDocumentClick)
  useEventListener(document, 'click', handleSearchDocumentClick)
  useEventListener(document, 'click', handleNotificationDocumentClick)
  useEventListener(window, 'storage', (event: StorageEvent) => {
    if (event.key === SECURITY_WARNING_BANNER_PREF_KEY) {
      refreshSecurityWarningBannerPreference()
    }
  })
  useEventListener(window, SECURITY_WARNING_BANNER_PREF_EVENT, () => {
    refreshSecurityWarningBannerPreference()
  })
  useEventListener(window, STARTUP_CONFIG_UPDATED_EVENT, () => {
    void refreshAuthPresentationFromStartupConfig(true)
  })
})

onUnmounted(() => {
  // Clean up subscriptions
  if (unsubscribeQueue) {
    unsubscribeQueue()
  }
  if (unsubscribeFilesRemoved) {
    unsubscribeFilesRemoved()
  }
  if (unsubscribeScanJobs) {
    unsubscribeScanJobs()
  }
  stopScanStatusReconciliation()
  if (unsubscribeSignalRConnected) {
    unsubscribeSignalRConnected()
  }
  moveJobsStore.stop()
  filesystemReadinessStore.stop()
  // Event listeners are automatically cleaned up by VueUse
})

const logout = async () => {
  try {
    logger.debug('Logout button clicked')
    await auth.logout()
    logger.debug('Auth logout completed, redirecting to login')
    // Instead of reloading, redirect to login - the router guard will handle authentication
    await router.push({ name: 'login' })
  } catch (error) {
    logger.error('Error during logout:', error)
    // Force redirect to login even if logout fails
    await router.push({ name: 'login' })
  }
}

const route = useRoute()
const hideLayout = computed(() => {
  const meta = route.meta as Record<string, unknown> | undefined
  return !!(meta && meta.hideLayout)
})

const refreshSecurityWarningBannerPreference = () => {
  const nextValue = getSecurityWarningBannerHiddenPreference()
  const wasPermanentlyHidden = securityWarningPermanentlyHidden.value
  securityWarningPermanentlyHidden.value = nextValue

  if (wasPermanentlyHidden && !nextValue) {
    securityWarningDismissed.value = false
  }
}

const showSecurityWarningBanner = computed(
  () =>
    !hideLayout.value &&
    startupConfigLoaded.value &&
    !authEnabled.value &&
    !securityWarningDismissed.value &&
    !securityWarningPermanentlyHidden.value,
)

const dismissSecurityWarning = () => {
  securityWarningDismissed.value = true
}

const showFilesystemInitializationBanner = computed(
  () =>
    !hideLayout.value &&
    (filesystemReadinessStore.filesystemInitializing || filesystemReadinessStore.filesystemFailed),
)

const filesystemInitializationMessage = computed(() => {
  if (filesystemReadinessStore.filesystemFailed) {
    return (
      filesystemReadinessStore.readiness?.filesystemErrorMessage ||
      'Library filesystem initialization failed. Browsing remains available, but file operations are disabled.'
    )
  }

  return 'Library filesystem is initializing. Browsing is available, but file operations are temporarily disabled.'
})

const appShellCssVars = computed(() => {
  const topNavHeightPx = 60
  const securityBannerHeightPx = showSecurityWarningBanner.value ? 44 : 0
  const filesystemBannerHeightPx = showFilesystemInitializationBanner.value ? 38 : 0
  const bannerHeightPx = securityBannerHeightPx + filesystemBannerHeightPx
  const topOffsetPx = hideLayout.value ? 0 : topNavHeightPx + bannerHeightPx

  return {
    '--top-nav-height': `${topNavHeightPx}px`,
    '--security-banner-height': `${securityBannerHeightPx}px`,
    '--filesystem-banner-height': `${filesystemBannerHeightPx}px`,
    '--app-banner-height': `${bannerHeightPx}px`,
    '--app-top-offset': `${topOffsetPx}px`,
  } as Record<string, string>
})

// Note: Backend connection indicator was moved to the System view.
</script>

/* Self-hosted Figtree @font-face declarations. Place font files in `fe/public/fonts/`. Recommended
files: Figtree-VariableFont_wght.woff2 (preferred), Figtree-Regular.woff, Figtree-SemiBold.woff If
these are not present, the Google Fonts import in `fe/index.html` will be used as a fallback. */
<style>
@font-face {
  font-family: 'Figtree';
  /* Only include font formats that are present in repo to avoid unresolved asset warnings during build */
  src: url('/fonts/Figtree-VariableFont_wght.ttf') format('truetype');
  font-weight: 100 900;
  font-style: normal;
  font-display: swap;
}
</style>

<style scoped>
#app {
  --top-nav-height: 60px;
  --security-banner-height: 0px;
  --filesystem-banner-height: 0px;
  --app-banner-height: 0px;
  --app-top-offset: var(--top-nav-height);
  font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 100dvh;
  background-color: #1a1a1a;
  color: white;
}

/* Top Navigation */
.top-nav {
  background-color: #2a2a2a;
  border-bottom: 1px solid #3a3a3a;
  padding: 0 1rem;
  height: var(--top-nav-height);
  display: flex;
  justify-content: space-between;
  align-items: center;
  position: fixed;
  top: var(--app-banner-height);
  left: 0;
  right: 0;
  z-index: 1000;
}

.top-nav.auth-warning-visible {
  top: var(--app-banner-height);
}

.security-warning-banner {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  z-index: 1002;
  height: 44px;
  display: flex;
  align-items: center;
  gap: 0.75rem;
  background: linear-gradient(180deg, #5f2a1b 0%, #4a2116 100%);
  border-bottom: 1px solid rgba(255, 183, 77, 0.28);
  color: #ffd8a8;
  padding: 0 1rem;
  font-size: 0.9rem;
  line-height: 1.3;
}

.security-warning-text {
  flex: 1 1 auto;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.security-warning-dismiss {
  flex: 0 0 auto;
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid rgba(255, 216, 168, 0.22);
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.04);
  color: inherit;
  cursor: pointer;
  padding: 0;
}

.security-warning-dismiss:hover {
  background: rgba(255, 255, 255, 0.08);
  border-color: rgba(255, 216, 168, 0.35);
}

.security-warning-dismiss:focus-visible {
  outline: 2px solid rgba(255, 216, 168, 0.5);
  outline-offset: 1px;
}

.filesystem-initialization-banner {
  position: fixed;
  top: var(--security-banner-height);
  left: 0;
  right: 0;
  z-index: 1001;
  height: var(--filesystem-banner-height);
  display: flex;
  align-items: center;
  padding: 0 1rem;
  background: #263548;
  border-bottom: 1px solid rgba(144, 202, 249, 0.28);
  color: #d7ebff;
  font-size: 0.875rem;
  line-height: 1.3;
}

.filesystem-initialization-banner.failed {
  background: #4a2116;
  border-bottom-color: rgba(255, 183, 77, 0.28);
  color: #ffd8a8;
}

.nav-brand {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.brand-link {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  text-decoration: none;
  color: inherit;
}

.brand-link,
.brand-link:visited {
  background-color: transparent;
  padding: 0; /* avoid the global link padding showing hover bg */
}

.brand-link:hover {
  background-color: transparent;
}

.brand-logo {
  width: 40px;
  height: 40px;
  transition:
    transform 220ms cubic-bezier(0.2, 0.8, 0.2, 1),
    filter 220ms;
  transform-origin: center center;
  filter: brightness(0) saturate(100%) invert(51%) sepia(56%) saturate(3237%) hue-rotate(184deg)
    brightness(97%) contrast(97%);
}

/* Animate the headphones when hovering the brand (logo or H1) */
.brand-link:hover .brand-logo,
.brand-link:focus .brand-logo {
  transform: rotate(6deg) scale(1.06);
}

/* Respect reduced motion preferences */
@media (prefers-reduced-motion: reduce) {
  .brand-logo,
  .brand-link:hover .brand-logo,
  .brand-link:focus .brand-logo {
    transition: none !important;
    transform: none !important;
  }
}

.nav-brand h1 {
  margin: 0;
  font-size: 1.5rem;
  font-weight: 500;
  color: #fff;
  /* Use Figtree for the brand heading when available */
  font-family:
    'Figtree',
    -apple-system,
    BlinkMacSystemFont,
    'Segoe UI',
    Roboto,
    'Helvetica Neue',
    Arial,
    sans-serif;
}

.nav-actions {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.nav-user {
  position: relative;
}

.nav-user-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
}

.nav-user-icon {
  font-size: 18px;
}

.user-menu {
  position: absolute;
  right: 0;
  top: 48px;
  background: #252525;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  min-width: 160px;
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.5);
  z-index: 1200;
  padding: 0.25rem 0;
}

.user-menu-item {
  display: block;
  width: 100%;
  padding: 0.5rem 1rem;
  background: transparent;
  border: none;
  color: #ddd;
  text-align: left;
  cursor: pointer;
}

.user-menu-item.username {
  font-weight: 500;
  color: #fff;
}

.user-menu-item:hover {
  background: #333;
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

/* SignalR indicator styles */
.signalr-indicator {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 6px;
  background: transparent;
  color: #c7cfd6;
  font-size: 12px;
}
.signalr-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  display: inline-block;
  box-shadow: 0 0 6px rgba(0, 0, 0, 0.6);
}
.signalr-dot.connected {
  background: #4caf50;
  box-shadow: 0 0 6px rgba(76, 175, 80, 0.4);
}
.signalr-dot.disconnected {
  background: #9e9e9e;
  opacity: 0.6;
}
.signalr-text {
  font-size: 12px;
  color: #bfc8cf;
}
.signalr-auth {
  font-size: 11px;
  color: #9aa0a6;
  margin-left: 6px;
}

.avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  cursor: pointer;
}

/* App Layout */
.app-layout {
  display: flex;
  flex: 1 1 auto;
  min-width: 0;
  margin-top: var(--app-top-offset);
  min-height: calc(100dvh - var(--app-top-offset));
}

.app-layout.no-top {
  margin-top: 0;
  min-height: 100dvh;
}

/* Sidebar */
.sidebar {
  width: 200px;
  background-color: #2a2a2a;
  border-right: 1px solid #3a3a3a;
  position: fixed;
  left: 0;
  top: var(--app-top-offset);
  bottom: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.sidebar.auth-warning-visible {
  top: var(--app-top-offset);
}

.sidebar-nav {
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
  box-sizing: border-box;
  padding: 1rem 0;
  overflow-y: auto;
}

.nav-section {
  margin-bottom: 1.5rem;
}

.nav-section:last-of-type {
  margin-bottom: 0;
}

.nav-item {
  display: flex;
  align-items: center;
  padding: 0.75rem 1rem;
  color: #ccc;
  text-decoration: none;
  transition: all 0.2s;
  position: relative;
  gap: 0.75rem;
}

/* Push count pills to the end of sidebar nav items */
.sidebar .nav-item .pill-count,
.sidebar .nav-item .pill.pill-count {
  margin-left: auto;
}

.nav-item:hover {
  background-color: #3a3a3a;
  color: white;
}

.nav-item.router-link-active {
  background-color: var(--brand-500);
  color: white;
}

.sidebar .nav-item.router-link-active svg,
.sidebar .nav-item.router-link-active .ph {
  color: white;
}

.sidebar-footer {
  position: sticky;
  bottom: 0;
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.5rem 1rem 0.5rem;
  border-top: 1px solid #3a3a3a;
  background-color: #2a2a2a;
}

.sidebar-source-link {
  display: flex;
  align-items: center;
  color: #9aa0a6;
  text-decoration: none;
}

.sidebar-source-link svg {
  fill: #9aa0a6;
  transition: fill 0.15s;
}

.sidebar-source-link:hover {
  background-color: transparent;
}

@media (hover: hover) {
  .sidebar-source-link:hover {
    background-color: transparent;
  }
}

.sidebar-source-link:hover svg {
  fill: #ffffff;
}

.sidebar-version-text {
  display: inline-block;
  font-size: 0.8rem;
  font-weight: 500;
  line-height: 1;
  color: #9aa0a6;
  letter-spacing: 0.02em;
}

.nav-item.router-link-active::before {
  content: '';
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 3px;
  background-color: var(--brand-500);
}

/* Icons */
.icon-audiobooks::before {
  content: '�';
}
.icon-plus::before {
  content: '+';
}
.icon-import::before {
  content: '📁';
}
.icon-calendar::before {
  content: '📅';
}
.icon-activity::before {
  content: '⏱️';
}
.icon-wanted::before {
  content: '⚠️';
}
.icon-settings::before {
  content: '⚙️';
}
.icon-system::before {
  content: '💻';
}
.icon-search::before {
  content: '🔍';
}
.icon-bell::before {
  content: '🔔';
}

/* Badges - Now using Pill component from @/components/base */
/* Legacy badge styles kept only for notification-badge positioning */
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

/* Page transition: new view fades in; old view leaves instantly to avoid blank flash */
.page-fade-enter-active {
  transition: opacity 150ms ease;
}
.page-fade-enter-from {
  opacity: 0;
}
.page-fade-leave-active {
  position: absolute;
  transition: none;
  opacity: 0;
}

/* Main Content */
.main-content {
  flex: 1;
  margin-left: 200px;
  background-color: #1a1a1a;
  min-width: 0;
  min-height: calc(100dvh - var(--app-top-offset));
  width: calc(100vw - 217px);
}

.main-content.full-page {
  margin-left: 0;
  margin-top: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  /* Account for the current fixed top chrome (nav + optional security banner). */
  min-height: calc(100dvh - var(--app-top-offset));
}

.fullpage-wrapper {
  width: 100%;
  max-width: 480px;
  padding: 1.25rem 1rem;
  box-sizing: border-box;
}

/* Responsive adjustments for login/full-page wrapper */
@media (max-width: 768px) {
  .fullpage-wrapper {
    padding: 1rem 0.75rem;
    max-width: 440px;
    margin: 0 12px;
  }
}

@media (max-width: 480px) {
  .fullpage-wrapper {
    padding: 0.75rem 0.5rem;
    max-width: 360px;
    margin: 0 8px;
  }
}

/* Responsive */
@media (max-width: 768px) {
  .sidebar {
    transform: translateX(-100%);
    transition: transform 0.3s;
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .main-content {
    margin-left: 0;
    width: 100%;
  }

  .nav-brand h1 {
    font-size: 1.2rem;
  }

  .top-nav .nav-btn.mobile-menu-btn {
    display: block !important;
  }

  .mobile-menu-icon {
    font-size: 20px;
  }

  /* Ensure nav stays above all content on mobile */
  .top-nav {
    z-index: 2000 !important;
    background-color: #2a2a2a !important;
    backdrop-filter: none !important;
    -webkit-backdrop-filter: none !important;
  }

  /* Ensure sidebar stays above images and is completely opaque on mobile */
  .sidebar {
    z-index: 1500 !important;
    background-color: #2a2a2a !important;
    backdrop-filter: none !important;
    -webkit-backdrop-filter: none !important;
  }

  /* Backdrop for slide-out sidebar on mobile */
  .sidebar-backdrop {
    position: fixed;
    top: var(--app-top-offset); /* below fixed top chrome */
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.34);
    z-index: 1400; /* below sidebar (1500) but above main content */
    transition: opacity 180ms ease;
  }
}
/* Header search styles */
.nav-search {
  position: relative;
  display: flex;
  align-items: center;
}

.nav-search .nav-btn {
  width: 44px;
  height: 44px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
}

.nav-search-inline {
  position: relative;
  display: flex;
  align-items: center;
  gap: 8px;
}

/* Collapsible slide-out search: input is absolutely positioned so it doesn't affect layout */
.nav-search-inline {
  min-width: 44px; /* reserve space for the icon */
}

.search-input-inline {
  transition:
    transform 220ms cubic-bezier(0.2, 0.9, 0.2, 1),
    width 220ms ease,
    opacity 180ms ease;
  position: absolute;
  top: 50%;
  right: 40px; /* leave space for the icon */
  transform: translateX(15%);
  width: 0;
  opacity: 0;
  pointer-events: none;
}

.nav-search-inline.open .search-input-inline {
  transform: translateX(40px);
  width: 340px;
  max-width: 50vw;
  opacity: 1;
  pointer-events: auto;
}

/* Keep the results dropdown aligned to the input when open */
.search-results-inline {
  position: absolute;
  top: 56px; /* slightly below the input */
  right: 40px;
  left: auto;
  width: 340px;
  max-width: 50vw;
}

.search-inline-icon {
  color: #c7cfd6;
  font-size: 20px;
  cursor: pointer;
  border-radius: 6px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.search-inline-icon:hover {
  background-color: #3a3a3a;
  color: #fff;
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

/* Standardize header/nav icons: size, alignment, color, and hit area */
.top-nav .ph {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  /* Increased size for better visibility and proportion */
  width: 48px;
  height: 48px;
  font-size: 32px;
  color: #c7cfd6; /* slightly brighter than default */
  border-radius: 6px;
}

.top-nav .nav-btn.mobile-menu-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

/* Mobile menu button should be hidden on desktop and only shown via media query on small screens */
.top-nav .nav-btn.mobile-menu-btn {
  display: none;
}

.top-nav .nav-user-btn .ph,
.top-nav .nav-btn .ph {
  font-size: 32px; /* ensure consistent glyph size */
}

@media (max-width: 768px) {
  .top-nav {
    padding: 0 0.75rem;
    gap: 0.75rem;
    justify-content: flex-start;
  }

  .nav-brand {
    flex: 1 1 auto;
    min-width: 0;
    gap: 0.5rem;
  }

  .brand-link {
    min-width: 0;
    gap: 0.4rem;
  }

  .nav-brand h1 {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .nav-actions {
    flex: 0 0 auto;
    gap: 0.5rem;
    margin-left: auto;
  }

  .top-nav .nav-btn {
    padding: 0.35rem;
  }

  .top-nav .ph,
  .top-nav .nav-user-btn .ph,
  .top-nav .nav-btn .ph {
    width: 40px;
    height: 40px;
    font-size: 26px;
  }
}

@media (max-width: 420px) {
  .top-nav {
    padding: 0 0.5rem;
    gap: 0.5rem;
  }

  .nav-actions {
    gap: 0.35rem;
  }

  .top-nav .ph,
  .top-nav .nav-user-btn .ph,
  .top-nav .nav-btn .ph {
    width: 36px;
    height: 36px;
    font-size: 24px;
  }
}

/* Sidebar navigation icons (Phosphor icons render as SVG) */
.sidebar .nav-item svg,
.sidebar .nav-item .ph {
  width: 28px;
  height: 28px;
  font-size: 20px;
  flex-shrink: 0;
  color: #c7cfd6;
}

/* Sub-navigation under main nav items */
.sidebar .nav-sub {
  display: flex;
  flex-direction: column;
  padding-left: 36px;
  margin-bottom: 0.5rem;
  /* collapse layout space when closed */
  max-height: 0;
  overflow: hidden;
  /* Use transform-scale for smooth animation */
  transform-origin: top;
  transform: scaleY(0);
  opacity: 0;
  pointer-events: none;
  transition:
    max-height 220ms ease,
    transform 160ms cubic-bezier(0.2, 0.9, 0.3, 1),
    opacity 120ms ease;
}

.sidebar .nav-sub.open {
  max-height: 400px; /* large enough to contain items */
  transform: scaleY(1);
  opacity: 1;
  pointer-events: auto;
}

@media (prefers-reduced-motion: reduce) {
  .sidebar .nav-sub,
  .sidebar .nav-sub.open {
    transition: none !important;
    transform: none !important;
    opacity: 1 !important;
    max-height: none !important;
  }
}

.sidebar .nav-subitem {
  display: block;
  font-size: 0.9rem;
  color: #cfcfcf;
  padding: 6px 0;
  text-decoration: none;
  border-left: 3px solid rgba(255, 255, 255, 0.1); /* Muted border for all */
  padding-left: 8px; /* Adjust for border */
}

.sidebar .nav-subitem.active {
  color: #ffffff;
  font-weight: 500;
  border-left: 3px solid #2196f3; /* Highlighted border for active */
}

.inline-spinner {
  width: 14px;
  height: 14px;
  border-radius: 50%;
  border: 2px solid rgba(255, 255, 255, 0.08);
  border-top-color: #2196f3;
  animation: spin 800ms linear infinite;
  margin-left: 6px;
}

.search-input-inline {
  width: 340px;
  max-width: 50vw;
  padding: 8px 12px 8px 12px;
  border-radius: 6px;
  border: 1px solid #424242;
  background: #222;
  color: #fff;
  outline: none;
  font-size: 0.95rem;
  position: relative;
}

.search-input::placeholder {
  color: #9aa0a6;
}

.search-input:focus {
  border-color: #2196f3;
  box-shadow: 0 4px 14px rgba(33, 150, 243, 0.12);
}

.search-results-inline {
  list-style: none;
  margin: 8px 0 0 0;
  padding: 0;
  max-height: 300px;
  overflow-y: auto;
}

.nav-search-inline {
  position: relative;
}

.search-results-inline {
  position: absolute;
  top: 44px;
  left: 0;
  right: 0;
  background: #1f1f1f;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 6px;
  z-index: 1400;
}

.search-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.result-thumb {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 6px;
  flex-shrink: 0;
  background: #2a2a2a;
}

.search-empty-overlay {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px;
  justify-content: flex-start;
}

/* Small spinner */
.overlay-spinner {
  width: 16px;
  height: 16px;
  border-radius: 50%;
  border: 2px solid rgba(255, 255, 255, 0.08);
  border-top-color: #2196f3;
  animation: spin 800ms linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */

.search-result {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 8px 10px;
  border-radius: 6px;
  cursor: pointer;
  color: #e6eef6;
}

.search-result:hover {
  background: rgba(255, 255, 255, 0.03);
}

.result-title {
  font-weight: 500;
  color: #fff;
  font-size: 0.95rem;
}

.result-sub {
  font-size: 0.82rem;
  color: #bfc8cf;
}

.search-empty {
  padding: 8px 10px;
  color: #9aa0a6;
  font-size: 0.9rem;
}

/* Mobile search overlay */
@media (max-width: 768px) {
  .nav-search-inline {
    position: fixed;
    top: 1rem;
    left: 50%;
    transform: translateX(-50%);
    width: 90%;
    max-width: 400px;
    z-index: 2001;
    background-color: #1e1e1e;
    border-radius: 6px;
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.5);
    border: 1px solid #3a3a3a;
  }

  /* Backdrop element (renders in DOM) - clicks close the search reliably */
  .mobile-search-backdrop {
    position: fixed;
    inset: 0;
    background-color: rgba(0, 0, 0, 0.9);
    z-index: 2000;
    cursor: pointer;
  }

  .search-input-inline {
    position: static;
    transform: none;
    width: 100% !important;
    opacity: 1;
    pointer-events: auto;
    background: #1e1e1e;
    border: 0px solid transparent;
    transform: translateX(0) !important;
  }

  .search-results-inline {
    position: absolute;
    top: 100%;
    left: 0;
    right: 0;
    width: 100%;
    max-width: none;
    margin-top: 0.5rem;
  }

  .nav-search-inline .nav-btn {
    position: absolute;
    right: 0.75rem;
    top: 50%;
    transform: translateY(-50%);
    background: none;
    border: none;
    color: #ccc;
    padding: 0.5rem;
    border-radius: 6px;
    z-index: 1;
    width: 44px;
    height: 44px;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .nav-search-inline .nav-btn:hover {
    background-color: #3a3a3a;
    color: white;
  }

  .search-input-inline {
    padding-right: 3rem; /* Make room for the close button */
    height: 44px;
    box-sizing: border-box;
  }

  /* Hide search input and results when not open, but keep button visible */
  .nav-search-inline:not(.open) .search-input-inline,
  .nav-search-inline:not(.open) .search-results-inline,
  .nav-search-inline:not(.open) .inline-spinner {
    display: none;
  }

  /* Show search button in nav when search is not open */
  .nav-search-inline:not(.open) {
    position: static;
    transform: none;
    width: auto;
    background: none;
    border: none;
    box-shadow: none;
    padding: 0;
    z-index: auto;
  }

  .nav-search-inline:not(.open)::before {
    display: none;
  }

  .nav-search-inline:not(.open) .nav-btn {
    position: static;
    transform: none;
    width: 44px;
    height: 44px;
  }
}

/* Notification dropdown styles */
.notification-wrapper {
  position: relative;
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
</style>
