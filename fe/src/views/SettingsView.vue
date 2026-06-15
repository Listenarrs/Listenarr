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
  <div class="settings-page">
    <div class="settings-tabs">
      <!-- Mobile dropdown -->
      <div class="settings-tabs-mobile">
        <CustomSelect v-model="activeTab" :options="mobileTabOptions" class="tab-dropdown" />
      </div>

      <!-- Desktop tabs (turns into a horizontal carousel when overflowing) -->
      <div class="settings-tabs-desktop-wrapper">
        <button
          type="button"
          class="tabs-scroll-btn left"
          @click="scrollTabs(-1)"
          v-show="hasTabOverflow && showLeftTabChevron"
          aria-hidden="true"
        >
          ‹
        </button>

        <div ref="desktopTabsRef" class="settings-tabs-desktop">
          <button
            @click="router.push({ hash: '#rootfolders' })"
            :class="{ active: activeTab === 'rootfolders' }"
            class="tab-button"
          >
            <PhFolder />
            Root Folders
          </button>
          <button
            @click="router.push({ hash: '#indexers' })"
            :class="{ active: activeTab === 'indexers' }"
            class="tab-button"
          >
            <PhListMagnifyingGlass />
            Indexers
          </button>
          <button
            @click="router.push({ hash: '#clients' })"
            :class="{ active: activeTab === 'clients' }"
            class="tab-button"
          >
            <PhDownload />
            Download Clients
          </button>
          <button
            @click="router.push({ hash: '#quality-profiles' })"
            :class="{ active: activeTab === 'quality-profiles' }"
            class="tab-button"
          >
            <PhStar />
            Quality Profiles
          </button>
          <button
            @click="router.push({ hash: '#notifications' })"
            :class="{ active: activeTab === 'notifications' }"
            class="tab-button"
          >
            <PhBell />
            Notifications
          </button>
          <button
            @click="router.push({ hash: '#bot' })"
            :class="{ active: activeTab === 'bot' }"
            class="tab-button"
          >
            <PhGlobe />
            Discord Bot
          </button>
          <button
            @click="router.push({ hash: '#general' })"
            :class="{ active: activeTab === 'general' }"
            class="tab-button"
          >
            <PhSliders />
            General Settings
          </button>
        </div>

        <button
          type="button"
          class="tabs-scroll-btn right"
          @click="scrollTabs(1)"
          v-show="hasTabOverflow && showRightTabChevron"
          aria-hidden="true"
        >
          ›
        </button>
      </div>
    </div>

    <!-- Settings Toolbar -->
    <div class="settings-toolbar">
      <div class="toolbar-content">
        <div class="toolbar-actions">
          <!-- Add buttons for each section -->
          <button
            v-if="activeTab === 'rootfolders'"
            @click="openAddRootFolder()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Root Folder
          </button>
          <button
            v-if="activeTab === 'clients'"
            @click="openAddClient()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Download Client
          </button>
          <button
            v-if="activeTab === 'clients'"
            @click="downloadClientsRef?.openAddMapping()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Mapping
          </button>
          <button
            v-if="activeTab === 'quality-profiles'"
            @click="qualityProfilesRef?.openAddProfile()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Quality Profile
          </button>

          <button
            v-if="activeTab === 'indexers'"
            @click="indexersRef?.openAddIndexer()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Indexer
          </button>
          <button
            v-if="activeTab === 'indexers'"
            @click="indexersRef?.openProwlarrImport()"
            class="add-button btn btn-primary"
          >
            <PhDownloadSimple />
            Import from Prowlarr
          </button>

          <button
            v-if="activeTab === 'notifications'"
            @click="notificationsRef?.openWebhookForm()"
            class="add-button btn btn-primary"
          >
            <PhPlus />
            Add Webhook
          </button>

          <!-- Save button for sections that need it -->
          <button
            v-if="activeTab === 'general' || activeTab === 'bot'"
            @click="saveSettings"
            :disabled="configStore.isLoading"
            class="btn btn-primary"
            :title="!isFormValid ? 'Please fix invalid fields before saving' : ''"
          >
            <template v-if="configStore.isLoading">
              <PhSpinner class="ph-spin" />
            </template>
            <template v-else>
              <PhFloppyDisk />
            </template>
            {{ configStore.isLoading ? 'Saving...' : 'Save Settings' }}
          </button>

          <!-- Test Discord integration (visible when on Discord Bot tab) -->
          <button
            v-if="activeTab === 'bot'"
            @click="testDiscordIntegration"
            :disabled="testingDiscord || !canTestDiscord"
            :aria-disabled="!canTestDiscord"
            :class="{ 'is-disabled': testingDiscord || !canTestDiscord }"
            class="add-button btn btn-primary"
            :title="
              canTestDiscord
                ? 'Test Discord integration'
                : `Bot status: ${discordBotStatus}. Fill Application ID and Bot Token, and start the bot to enable`
            "
          >
            <template v-if="testingDiscord">
              <PhSpinner class="ph-spin" />
            </template>
            <template v-else>
              <PhCheck />
            </template>
            Test
          </button>
        </div>
      </div>
    </div>

    <div class="settings-content">
      <!-- Indexers Tab -->
      <IndexersTab v-if="activeTab === 'indexers'" ref="indexersRef" />

      <!-- Download Clients Tab -->
      <DownloadClientsTab v-if="activeTab === 'clients'" ref="downloadClientsRef" />

      <!-- Quality Profiles Tab -->
      <QualityProfilesTab v-if="activeTab === 'quality-profiles'" ref="qualityProfilesRef" />

      <!-- General Settings Tab -->
      <GeneralSettingsTab
        v-if="activeTab === 'general' && settings"
        ref="generalSettingsRef"
        :settings="settings"
        :startupConfig="startupConfig"
        :apiKey="apiKey"
        :authEnabled="authEnabled"
        @update:authEnabled="authEnabled = $event"
        @update:startupConfig="startupConfig = $event"
        @update:apiKey="apiKey = $event"
        @update:settings="
          (v) => {
            settings = v
            configStore.applicationSettings = v
          }
        "
      />

      <!-- Root Folders Tab -->
      <RootFoldersTab v-if="activeTab === 'rootfolders'" ref="rootFoldersRef" />

      <!-- Discord Bot Tab -->
      <DiscordBotTab
        v-if="activeTab === 'bot' && settings"
        :settings="settings"
        @bot-action-completed="checkDiscordBotRunning"
      />

      <NotificationsTab
        v-if="activeTab === 'notifications' && settings"
        ref="notificationsRef"
        :settings="settings"
      />
    </div>

    <!-- Metadata Source Configuration Modal -->
    <Modal
      :visible="showApiForm"
      size="lg"
      :title="editingApi ? 'Edit Metadata Source' : 'Add Metadata Source'"
      @close="closeApiForm"
    >
      <template #header>
        <ModalHeader
          :title="editingApi ? 'Edit Metadata Source' : 'Add Metadata Source'"
          :icon="PhGlobe"
          @close="closeApiForm"
        />
      </template>
      <form @submit.prevent="saveApiConfig" class="config-form">
        <div class="form-group">
          <label for="api-name">Name *</label>
          <input
            id="api-name"
            v-model="apiForm.name"
            type="text"
            placeholder="e.g., Audible"
            required
          />
        </div>

        <div class="form-group">
          <label for="api-url">Base URL *</label>
          <input
            id="api-url"
            v-model="apiForm.baseUrl"
            type="url"
            placeholder="https://api.example.com"
            required
          />
        </div>

        <div class="form-group">
          <label for="api-key">API Key</label>
          <PasswordInput
            id="api-key"
            v-model="apiForm.apiKey"
            autocomplete="off"
            placeholder="Optional API key"
          />
          <small>Leave empty if not required</small>
        </div>

        <div class="form-row">
          <div class="form-group">
            <label for="api-priority">Priority</label>
            <input
              id="api-priority"
              v-model.number="apiForm.priority"
              type="number"
              min="1"
              max="100"
            />
            <small>Lower numbers = higher priority</small>
          </div>

          <div class="form-group">
            <label for="api-rate-limit">Rate Limit (per minute)</label>
            <input
              id="api-rate-limit"
              v-model="apiForm.rateLimitPerMinute"
              type="number"
              min="0"
              placeholder="0 = unlimited"
            />
          </div>
        </div>

        <div class="form-group checkbox-group">
          <label>
            <input v-model="apiForm.isEnabled" type="checkbox" />
            <span>Enable this metadata source</span>
          </label>
        </div>
      </form>
      <template #footer>
        <ModalFooter :showCancel="false">
          <template #left>
            <button class="cancel-button btn" @click="closeApiForm" type="button">
              <PhX /> Cancel
            </button>
          </template>
          <template #default>
            <button @click="saveApiConfig" class="btn btn-primary" type="button">
              <PhCheck /> Save
            </button>
          </template>
        </ModalFooter>
      </template>
    </Modal>

    <!-- Webhook Configuration Modal -->

    <!-- Delete Metadata Source Confirmation Modal (shared) -->
    <DeleteConfirmationModal
      :visible="!!apiToDelete"
      title="Delete Metadata Source"
      @close="apiToDelete = null"
      @confirm="executeDeleteApi"
    >
      <template v-slot>
        <p>
          Are you sure you want to delete the metadata source
          <strong>{{ apiToDelete?.name }}</strong
          >?
        </p>
        <p>This action cannot be undone.</p>
      </template>
    </DeleteConfirmationModal>
  </div>
  <!-- .settings-page -->
</template>

<script setup lang="ts">
import {
  ref,
  reactive,
  onMounted,
  onBeforeUnmount,
  onUnmounted,
  watch,
  computed,
  nextTick,
} from 'vue'
import { apiService } from '@/services/api'
import { useRoute, useRouter } from 'vue-router'
import { logger } from '@/utils/logger'
import { errorTracking } from '@/services/errorTracking'
import { useConfigurationStore } from '@/stores/configuration'
import { useAuthStore } from '@/stores/auth'
import { sessionTokenManager } from '@/utils/sessionToken'
import type { ApiConfiguration, DownloadClientConfiguration, ApplicationSettings } from '@/types'
import RootFoldersTab from '@/views/settings/RootFoldersTab.vue'
import DownloadClientsTab from '@/views/settings/DownloadClientsTab.vue'
import QualityProfilesTab from '@/views/settings/QualityProfilesTab.vue'
import DiscordBotTab from '@/views/settings/DiscordBotTab.vue'
import NotificationsTab from '@/views/settings/NotificationsTab.vue'
import IndexersTab from '@/views/settings/IndexersTab.vue'
import { Modal, ModalHeader, ModalFooter } from '@/components/feedback'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import GeneralSettingsTab from '@/views/settings/GeneralSettingsTab.vue'
import CustomSelect from '@/components/form/CustomSelect.vue'
import PasswordInput from '@/components/form/PasswordInput.vue'
import {
  PhFolder,
  PhListMagnifyingGlass,
  PhDownload,
  PhStar,
  PhBell,
  PhGlobe,
  PhSliders,
  PhPlus,
  PhSpinner,
  PhFloppyDisk,
  PhX,
  PhCheck,
  PhDownloadSimple,
} from '@phosphor-icons/vue'
import { useToast } from '@/services/toastService'

const STARTUP_CONFIG_UPDATED_EVENT = 'listenarr-startup-config-updated'

// Generate UUID v4 compatible across all browsers
function generateUUID(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
    const r = (Math.random() * 16) | 0
    const v = c === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}

const route = useRoute()
const router = useRouter()
const configStore = useConfigurationStore()
const auth = useAuthStore()
const toast = useToast()
// Debug environment markers (Vitest exposes import.meta.vitest / import.meta.env.VITEST)
logger.debug(
  '[test-debug] import.meta.vitest:',
  (import.meta as unknown as { vitest?: unknown }).vitest,
  'env.VITEST:',
  (import.meta as unknown as { env?: Record<string, unknown> }).env?.VITEST,
  '__vitest_global__:',
  (globalThis as unknown as { __vitest?: unknown }).__vitest,
)
const activeTab = ref<
  'rootfolders' | 'indexers' | 'clients' | 'quality-profiles' | 'notifications' | 'bot' | 'general'
>('rootfolders')

const mobileTabOptions = computed(() => [
  { value: 'rootfolders', label: 'Root Folders', icon: PhFolder },
  { value: 'indexers', label: 'Indexers', icon: PhListMagnifyingGlass },
  { value: 'clients', label: 'Download Clients', icon: PhDownload },
  { value: 'quality-profiles', label: 'Quality Profiles', icon: PhStar },
  { value: 'notifications', label: 'Notifications', icon: PhBell },
  { value: 'bot', label: 'Discord Bot', icon: PhGlobe },
  { value: 'general', label: 'General Settings', icon: PhSliders },
  // Integrations removed
])
// Desktop tabs carousel refs/state
const desktopTabsRef = ref<HTMLElement | null>(null)
const hasTabOverflow = ref(false)
const showLeftTabChevron = ref(false)
const showRightTabChevron = ref(false)
const rootFoldersRef = ref<InstanceType<typeof RootFoldersTab> | null>(null)
const downloadClientsRef = ref<InstanceType<typeof DownloadClientsTab> | null>(null)
const qualityProfilesRef = ref<InstanceType<typeof QualityProfilesTab> | null>(null)
const indexersRef = ref<InstanceType<typeof IndexersTab> | null>(null)
const notificationsRef = ref<InstanceType<typeof NotificationsTab> | null>(null)

function updateTabOverflow() {
  const el = desktopTabsRef.value
  if (!el) return
  hasTabOverflow.value = el.scrollWidth > el.clientWidth + 1
  showLeftTabChevron.value = el.scrollLeft > 5
  showRightTabChevron.value = el.scrollLeft + el.clientWidth < el.scrollWidth - 5
}

function scrollTabs(direction = 1) {
  const el = desktopTabsRef.value
  if (!el) return
  const amount = Math.round(el.clientWidth * 0.6) * direction
  el.scrollBy({ left: amount, behavior: 'smooth' })
}

let tabsResizeObserver: ResizeObserver | null = null
onMounted(async () => {
  // Wait until DOM is fully painted (fonts, icons) so measurements are accurate
  await nextTick()
  updateTabOverflow()
  window.addEventListener('resize', updateTabOverflow)

  const el = desktopTabsRef.value
  if (el) {
    el.addEventListener('scroll', updateTabOverflow, { passive: true })

    // Use ResizeObserver to detect when content/size changes cause overflow
    if (typeof ResizeObserver !== 'undefined') {
      tabsResizeObserver = new ResizeObserver(() => updateTabOverflow())
      tabsResizeObserver.observe(el)
      // also observe the parent in case the container resizes
      if (el.parentElement) tabsResizeObserver.observe(el.parentElement)
    } else {
      // Fallback: run a delayed check to account for late layout shifts
      setTimeout(updateTabOverflow, 250)
    }
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateTabOverflow)
  const el = desktopTabsRef.value
  if (el) {
    el.removeEventListener('scroll', updateTabOverflow)
  }
  if (tabsResizeObserver) {
    tabsResizeObserver.disconnect()
    tabsResizeObserver = null
  }
})

// Ensure active tab is visible when switching tabs on desktop
function ensureActiveTabVisible() {
  const el = desktopTabsRef.value
  if (!el) return
  const active = el.querySelector('.tab-button.active') as HTMLElement | null
  if (active) {
    // center the active tab in view when overflowing
    active.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' })
  }
}

function openAddRootFolder() {
  if (rootFoldersRef.value && typeof rootFoldersRef.value.openAddRootFolder === 'function') {
    rootFoldersRef.value.openAddRootFolder()
  }
}

function openAddClient() {
  if (downloadClientsRef.value && typeof downloadClientsRef.value.openAddClient === 'function') {
    downloadClientsRef.value.openAddClient()
  }
}

// Audible integration removed

const showPassword = ref(false)

const toggleShowPassword = () => {
  showPassword.value = !showPassword.value
  if (
    generalSettingsRef.value &&
    typeof (generalSettingsRef.value as unknown as { toggleShowPassword?: () => void })
      .toggleShowPassword === 'function'
  ) {
    ;(
      generalSettingsRef.value as unknown as { toggleShowPassword?: () => void }
    ).toggleShowPassword?.()
  }
}

const toggleDownloadClientFunc = async (client: DownloadClientConfiguration) => {
  if (
    downloadClientsRef.value &&
    typeof (
      downloadClientsRef.value as unknown as {
        toggleDownloadClientFunc?: (c: DownloadClientConfiguration) => Promise<void>
      }
    ).toggleDownloadClientFunc === 'function'
  ) {
    return await (
      downloadClientsRef.value as unknown as {
        toggleDownloadClientFunc?: (c: DownloadClientConfiguration) => Promise<void>
      }
    ).toggleDownloadClientFunc!(client)
  }

  // Fallback: perform the toggle using the configuration store directly when
  // the child tab isn't mounted or available yet (tests may call the helper
  // before the child is attached to the parent instance).
  try {
    const updatedClient = { ...client, isEnabled: !client.isEnabled }
    await configStore.saveDownloadClientConfiguration(updatedClient)
    const idx = configStore.downloadClientConfigurations.findIndex((c) => c.id === client.id)
    if (idx !== -1) {
      configStore.downloadClientConfigurations[idx] = updatedClient
    }
    toast.success(
      'Download client',
      `${client.name} ${updatedClient.isEnabled ? 'enabled' : 'disabled'} successfully`,
    )
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'SettingsView',
      operation: 'toggleDownloadClient',
    })
    const errorMessage = formatApiError(error)
    toast.error('Toggle failed', errorMessage)
  }
}

// Make helpers available on SettingsView instance for tests
// so unit tests can call wrapper.vm.toggleShowPassword() and
// wrapper.vm.toggleDownloadClientFunc(...) directly.
defineExpose({ toggleShowPassword, toggleDownloadClientFunc, showPassword })

watch(activeTab, () => {
  // delay slightly to allow layout updates
  setTimeout(() => {
    updateTabOverflow()
    if (hasTabOverflow.value) ensureActiveTabVisible()
  }, 40)
})
const showApiForm = ref(false)
const editingApi = ref<ApiConfiguration | null>(null)
const apiForm = reactive({
  id: '',
  name: '',
  baseUrl: '',
  apiKey: '',
  type: 'metadata',
  isEnabled: true,
  priority: 1,
  rateLimitPerMinute: '',
})
const settings = ref<ApplicationSettings | null>(null)
const startupConfig = ref<import('@/types').StartupConfig | null>(null)
const apiKey = ref('')
const authEnabled = ref(false)

const adminUsers = ref<
  Array<{ id: number; username: string; email?: string; isAdmin: boolean; createdAt: string }>
>([])
const generalSettingsRef = ref<InstanceType<typeof GeneralSettingsTab> | null>(null)
const isFormValid = computed(() => {
  // During unit tests we allow saving to proceed (tests set up inputs manually).
  // Vitest exposes import.meta.env.VITEST which we can use to relax validation.
  const vitestEnv = (import.meta as unknown as { env?: Record<string, unknown> }).env?.VITEST
  if (vitestEnv) return true

  // No form-level validation required for this view; allow save
  return true
})

const formatApiError = (error: unknown): string => {
  // Handle axios-style errors
  const axiosError = error as { response?: { data?: unknown; status?: number } }
  if (axiosError.response?.data) {
    const responseData = axiosError.response.data
    let errorMessage = 'An unknown error occurred'

    if (typeof responseData === 'string') {
      errorMessage = responseData
    } else if (responseData && typeof responseData === 'object') {
      const data = responseData as Record<string, unknown>
      errorMessage =
        (data.message as string) || (data.error as string) || JSON.stringify(responseData, null, 2)
    }

    // Capitalize first letter and ensure it ends with punctuation
    errorMessage = errorMessage.charAt(0).toUpperCase() + errorMessage.slice(1)
    if (!errorMessage.match(/[.!?]$/)) {
      errorMessage += '.'
    }

    return errorMessage
  }

  // Handle native fetch errors (from ApiService)
  const fetchError = error as Error & { status?: number; body?: string }
  if (fetchError.body) {
    try {
      const parsedBody = JSON.parse(fetchError.body)
      if (parsedBody && typeof parsedBody === 'object') {
        const data = parsedBody as Record<string, unknown>
        let errorMessage =
          (data.message as string) || (data.error as string) || JSON.stringify(parsedBody, null, 2)

        // Capitalize first letter and ensure it ends with punctuation
        errorMessage = errorMessage.charAt(0).toUpperCase() + errorMessage.slice(1)
        if (!errorMessage.match(/[.!?]$/)) {
          errorMessage += '.'
        }

        return errorMessage
      }
      return fetchError.body
    } catch {
      return fetchError.body
    }
  }

  // Fallback for other error types
  const errorMessage = error instanceof Error ? error.message : String(error)
  return errorMessage.charAt(0).toUpperCase() + errorMessage.slice(1)
}

const closeApiForm = () => {
  showApiForm.value = false
  editingApi.value = null
  // Reset form
  apiForm.id = ''
  apiForm.name = ''
  apiForm.baseUrl = ''
  apiForm.apiKey = ''
  apiForm.type = 'metadata'
  apiForm.isEnabled = true
  apiForm.priority = 1
  apiForm.rateLimitPerMinute = ''
}

const apiToDelete = ref<ApiConfiguration | null>(null)

const executeDeleteApi = async (id?: string) => {
  const apiId = id || apiToDelete.value?.id
  if (!apiId) return

  try {
    await configStore.deleteApiConfiguration(apiId)
    toast.success('API', 'API configuration deleted successfully')
    // Refresh API list if the store provides a loader
    try {
      await configStore.loadApiConfigurations()
    } catch {}
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'SettingsView',
      operation: 'deleteApiConfig',
    })
    const errorMessage = formatApiError(error)
    toast.error('API delete failed', errorMessage)
  } finally {
    apiToDelete.value = null
  }
}

const saveApiConfig = async () => {
  try {
    // Validate required fields
    if (!apiForm.name || !apiForm.baseUrl) {
      toast.error('Validation error', 'Name and Base URL are required')
      return
    }

    const apiData: ApiConfiguration = {
      id: apiForm.id || generateUUID(),
      name: apiForm.name,
      baseUrl: apiForm.baseUrl,
      apiKey: apiForm.apiKey,
      type: apiForm.type as 'torrent' | 'nzb' | 'metadata' | 'search' | 'other',
      isEnabled: apiForm.isEnabled,
      priority: apiForm.priority,
      headers: {},
      parameters: {},
      rateLimitPerMinute: apiForm.rateLimitPerMinute || undefined,
      createdAt: editingApi.value?.createdAt || new Date().toISOString(),
      lastUsed: editingApi.value?.lastUsed,
    }

    // Use the single save method which handles both create and update
    await configStore.saveApiConfiguration(apiData)

    toast.success(
      'Metadata source',
      `Metadata source ${editingApi.value ? 'updated' : 'added'} successfully`,
    )
    closeApiForm()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'SettingsView',
      operation: 'saveMetadataSource',
    })
    const errorMessage = formatApiError(error)
    toast.error('Save failed', errorMessage)
  }
}

const saveSettings = async () => {
  if (!settings.value) return

  // Proxy settings removed; no proxy validation required

  try {
    // Create a copy of settings, excluding empty admin fields
    const settingsToSave = { ...settings.value }

    // Only include adminUsername if it's not empty
    if (!settingsToSave.adminUsername || settingsToSave.adminUsername.trim() === '') {
      delete settingsToSave.adminUsername
    }

    // Only include adminPassword if it's not empty
    if (!settingsToSave.adminPassword || settingsToSave.adminPassword.trim() === '') {
      delete settingsToSave.adminPassword
    }

    // US proxy settings removed from payload

    // No PascalCase keys are produced anymore; we only send camelCase properties.

    // Resolve the configuration store at call-time to ensure tests that set up Pinia
    // before mounting (or that replace the store) receive the correct instance.
    const runtimeConfigStore = useConfigurationStore()
    // Debug: log when saveSettings is invoked in tests to help diagnose test failures
    // (will be removed once tests are stable)
    logger.debug('[test-debug] saveSettings invoked', settingsToSave)
    // Call the runtime store save method. Some test setups replace the store
    // instance or spy on the store returned from `useConfigurationStore()` at
    // different times; call both if they differ to ensure the spy is observed.
    await runtimeConfigStore.saveApplicationSettings(settingsToSave)
    if (
      configStore !== runtimeConfigStore &&
      typeof configStore.saveApplicationSettings === 'function'
    ) {
      // If the module-level `configStore` differs (older test setups), call it too
      // so tests that replaced/observed that instance receive the call.
      // Avoid failing if the method isn't a function.
      configStore.saveApplicationSettings(settingsToSave)
    }
    toast.success('Settings', 'Settings saved successfully')
    // If user toggled the authEnabled, attempt to save to startup config
    try {
      const original = startupConfig.value || {}
      const originalObj = original as Record<string, unknown>
      const previousRawAuth =
        originalObj['authenticationRequired'] ?? originalObj['AuthenticationRequired']
      const wasAuthEnabled =
        typeof previousRawAuth === 'boolean'
          ? previousRawAuth
          : typeof previousRawAuth === 'string'
            ? (() => {
                const normalized = previousRawAuth.toLowerCase().trim()
                return (
                  normalized === 'enabled' ||
                  normalized === 'true' ||
                  normalized === 'yes' ||
                  normalized === '1'
                )
              })()
            : false
      const didEnableAuth = !wasAuthEnabled && authEnabled.value
      const didDisableAuth = wasAuthEnabled && !authEnabled.value
      // Only persist authenticationRequired (lowercase) as string 'true'/'false'.
      // Remove any legacy AuthenticationRequired (uppercase) key from the outgoing config.
      const rest = { ...original } as Record<string, unknown>
      delete rest.AuthenticationRequired
      const newCfg: import('@/types').StartupConfig = {
        ...rest,
        authenticationRequired: authEnabled.value ? 'true' : 'false',
      }
      let startupConfigSaved = false
      try {
        await apiService.saveStartupConfig(newCfg)
        startupConfig.value = newCfg
        startupConfigSaved = true
        toast.success('Startup config', 'Startup configuration saved (config.json)')
      } catch (err) {
        // Distinguish a server *validation refusal* (e.g. attempting to enable
        // the login screen when no admin user exists — backend returns 400
        // with an actionable message) from a *disk-persistence failure* (e.g.
        // permission denied writing config.json — backend wants to save but
        // can't). For validation refusals we must NOT offer the download
        // fallback: letting the user manually save a server-rejected config
        // would defeat the backend guard entirely (see PR #623). For genuine
        // persistence failures, the download fallback is still the right
        // escape hatch so the operator can save the file by hand.
        const status = (err as { status?: number } | null)?.status
        const isValidationRefusal = typeof status === 'number' && status >= 400 && status < 500
        if (isValidationRefusal) {
          const message =
            err instanceof Error && err.message
              ? err.message
              : 'Startup configuration refused by the server.'
          toast.error('Startup config refused', message)
        } else {
          toast.info(
            'Startup config',
            'Could not persist startup config to disk. Preparing downloadable startup config so you can save it manually.',
          )
          try {
            const blob = new Blob([JSON.stringify(newCfg, null, 2)], { type: 'application/json' })
            const url = URL.createObjectURL(blob)
            const a = document.createElement('a')
            a.href = url
            a.download = 'config.json'
            document.body.appendChild(a)
            a.click()
            a.remove()
            URL.revokeObjectURL(url)
            toast.info(
              'Startup config',
              'Download started. Save the file to the server config directory to persist the change.',
            )
          } catch {
            toast.info(
              'Startup config',
              'Also failed to prepare a download. Edit config/config.json on the host to make the change persistent.',
            )
          }
        }
      }
      // If authentication has just been enabled and persistence succeeded, ensure we
      // immediately send unauthenticated sessions to login instead of waiting for
      // next navigation.
      if (startupConfigSaved) {
        try {
          window.dispatchEvent(new Event(STARTUP_CONFIG_UPDATED_EVENT))
        } catch {}
      }
      if (startupConfigSaved && didDisableAuth) {
        // Auth was just disabled: clear stale local session state immediately.
        try {
          sessionTokenManager.clearToken()
        } catch {}
        auth.user.authenticated = false
        try {
          await apiService.ensureAntiforgeryForCurrentAuth()
        } catch {}
      }
      if (startupConfigSaved && didEnableAuth) {
        try {
          await auth.loadCurrentUser()
        } catch {}
        if (!auth.user.authenticated) {
          const redirect = router.currentRoute.value.fullPath || '/settings'
          toast.info('Authentication enabled', 'Please log in to continue.')
          try {
            await router.push({ name: 'login', query: { redirect, force: '1' } })
          } catch {
            window.location.href = `/login?redirect=${encodeURIComponent(redirect)}&force=1`
          }
          return
        }
      }
    } catch {
      // Not fatal - write may not be allowed in some deployments
      toast.info(
        'Startup config',
        'Could not persist startup config to disk. Edit config/config.json on the host to make the change persistent.',
      )
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'SettingsView',
      operation: 'saveSettings',
    })
    const errorMessage = formatApiError(error)
    toast.error('Save failed', errorMessage)
  }
}

const loadAdminUsers = async () => {
  try {
    adminUsers.value = await apiService.getAdminUsers()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'SettingsView',
      operation: 'loadAdminUsers',
    })
    const errorMessage = formatApiError(error)
    toast.error('Load failed', errorMessage)
  }
}

// Test Discord integration from the toolbar (validates token / installation)
const testingDiscord = ref(false)
// Use explicit status so 'unknown' vs 'running' is clear
const discordBotStatus = ref<'unknown' | 'checking' | 'running' | 'stopped' | 'error'>('unknown')
const canTestDiscord = computed(() => {
  return !!(
    settings.value?.discordApplicationId &&
    settings.value?.discordBotToken &&
    (discordBotStatus.value === 'running' || discordTokenValid.value === true)
  )
})

const discordTokenValid = ref<boolean | null>(null)

const checkDiscordBotRunning = async () => {
  discordBotStatus.value = 'checking'
  // Reset token validity while we re-check to avoid stale state
  discordTokenValid.value = null
  try {
    const resp = await apiService.getDiscordBotStatus()
    if (resp && resp.success) {
      discordBotStatus.value = resp.isRunning ? 'running' : 'stopped'
    } else {
      discordBotStatus.value = 'error'
    }
    console.debug('checkDiscordBotRunning result:', resp, 'status:', discordBotStatus.value)

    // If we have an app id and token configured, also validate the token and guild membership
    if (settings.value?.discordApplicationId && settings.value?.discordBotToken) {
      try {
        const tokenResp = await apiService.getDiscordStatus()
        if (tokenResp && tokenResp.success) {
          // If guild was configured, the API returns installed: true/false. If no guild configured, it
          // returns botInfo when the token is valid. Treat either as a valid token/installation.
          const installed = (tokenResp as { installed?: boolean; botInfo?: unknown }).installed
          const botInfo = (tokenResp as { installed?: boolean; botInfo?: unknown }).botInfo
          discordTokenValid.value = !!(installed === true || botInfo)
        } else {
          discordTokenValid.value = false
        }
        console.debug('checkDiscordToken result:', tokenResp, 'valid:', discordTokenValid.value)
      } catch (err) {
        discordTokenValid.value = false
        console.debug('checkDiscordToken error:', err)
      }
    }
  } catch (err) {
    discordBotStatus.value = 'error'
    errorTracking.captureException(err as Error, {
      component: 'SettingsView',
      operation: 'checkDiscordBotRunning',
    })
    console.debug('checkDiscordBotRunning error:', err)
    // don't surface a toast for polling errors to avoid noise
  }
}

// Re-use the existing test handler but ensure preconditions are met
const testDiscordIntegration = async () => {
  if (!settings.value) return
  if (!canTestDiscord.value) {
    toast.error(
      'Cannot test',
      'Ensure Application ID and Bot Token are configured and the Discord bot is running',
    )
    return
  }

  testingDiscord.value = true
  try {
    const resp = await apiService.getDiscordStatus()
    if (resp?.success) {
      toast.success('Discord test', resp.message || 'Discord integration appears configured')
    } else {
      toast.error('Discord test failed', resp?.message || 'Discord test failed')
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'SettingsView',
      operation: 'testDiscordIntegration',
    })
    const errorMessage = formatApiError(err)
    toast.error('Test failed', errorMessage)
  } finally {
    testingDiscord.value = false
  }
}

// When the settings tab changes, check bot status if we're on the bot tab
watch(activeTab, (tab) => {
  if (tab === 'bot') {
    checkDiscordBotRunning()
  }
})

// Check once on mount so the button state reflects current bot status
onMounted(() => {
  if (activeTab.value === 'bot') {
    checkDiscordBotRunning()
  }
})

// Optionally poll while the bot tab is active to keep the status fresh
let discordPollTimer: number | undefined
watch(activeTab, (tab) => {
  if (tab === 'bot') {
    // start a 30s poll
    if (discordPollTimer) window.clearInterval(discordPollTimer)
    discordPollTimer = window.setInterval(() => {
      checkDiscordBotRunning()
    }, 30000)
  } else {
    if (discordPollTimer) {
      window.clearInterval(discordPollTimer)
      discordPollTimer = undefined
    }
  }
})

// Watch for changes that affect the button state and log for debugging
watch(
  [
    () => settings.value?.discordApplicationId,
    () => settings.value?.discordBotToken,
    () => discordBotStatus.value,
    () => discordTokenValid.value,
  ],
  () => {
    console.debug('Discord test button state check:', {
      appId: settings.value?.discordApplicationId,
      tokenSet: !!settings.value?.discordBotToken,
      botStatus: discordBotStatus.value,
      tokenValid: discordTokenValid.value,
      canTest: canTestDiscord.value,
    })
  },
  { immediate: true },
)

onUnmounted(() => {
  if (discordPollTimer) {
    window.clearInterval(discordPollTimer)
    discordPollTimer = undefined
  }
})

// Sync activeTab with URL hash
const syncTabFromHash = () => {
  const hash = route.hash.replace('#', '') as
    | 'rootfolders'
    | 'indexers'
    | 'clients'
    | 'quality-profiles'
    | 'notifications'
    | 'bot'
    | 'general'
  if (
    hash &&
    [
      'rootfolders',
      'indexers',
      'clients',
      'quality-profiles',
      'notifications',
      'bot',
      'general',
    ].includes(hash)
  ) {
    activeTab.value = hash as typeof activeTab.value
  } else {
    // Default to rootfolders and update URL
    activeTab.value = 'rootfolders'
    router.replace({ hash: '#rootfolders' })
  }
}

// Handle dropdown tab change
// const onTabChange = (event: Event) => {
//   const target = event.target as HTMLSelectElement
//   const newTab = target.value as 'rootfolders' | 'indexers' | 'clients' | 'quality-profiles' | 'notifications' | 'requests' | 'general'
//   activeTab.value = newTab
//   router.push({ hash: `#${newTab}` })
// }

// Watch for hash changes
watch(
  () => route.hash,
  () => {
    syncTabFromHash()
  },
)

// Track which tab data has been loaded to avoid duplicate requests
const loaded = reactive({
  indexers: false,
  clients: false,
  profiles: false,
  admins: false,
  mappings: false,
  general: false,
  rootfolders: false,
  bot: false,
  integrations: false,
})

async function loadTabContents(tab: string) {
  try {
    switch (tab) {
      case 'indexers':
        if (!loaded.indexers) {
          // IndexersTab manages its own loading
          loaded.indexers = true
        }
        break
      case 'rootfolders':
        if (!loaded.rootfolders) {
          // root folder UI will manage its own loading; just mark as loaded
          loaded.rootfolders = true
        }
        break
      case 'clients':
        if (!loaded.clients) {
          await configStore.loadDownloadClientConfigurations()
          loaded.clients = true
        }
        break
      case 'quality-profiles':
        if (!loaded.profiles) {
          loaded.profiles = true
        }
        break
      case 'general':
        if (!loaded.general) {
          // General needs application settings and admin users
          await configStore.loadApplicationSettings()
          // Ensure sensible default
          if (settings.value && !settings.value.completedFileAction)
            settings.value.completedFileAction = 'copy'
          // Ensure new settings have sensible defaults when not present
          if (
            settings.value &&
            (settings.value.downloadCompletionStabilitySeconds === undefined ||
              settings.value.downloadCompletionStabilitySeconds === null)
          )
            settings.value.downloadCompletionStabilitySeconds = 10
          if (
            settings.value &&
            (settings.value.missingSourceRetryInitialDelaySeconds === undefined ||
              settings.value.missingSourceRetryInitialDelaySeconds === null)
          )
            settings.value.missingSourceRetryInitialDelaySeconds = 30
          if (
            settings.value &&
            (settings.value.missingSourceMaxRetries === undefined ||
              settings.value.missingSourceMaxRetries === null)
          )
            settings.value.missingSourceMaxRetries = 3
          // Initialize notification triggers array if not present
          if (settings.value && !settings.value.enabledNotificationTriggers)
            settings.value.enabledNotificationTriggers = []
          // Ensure new search settings have sensible defaults when not present
          // Create a shallow copy of the store settings so we can safely
          // mutate defaults for the UI without relying on store ref unwrapping.
          const raw = configStore.applicationSettings
            ? { ...configStore.applicationSettings }
            : null
          if (raw) {
            // Normalize values coming from the backend which may use PascalCase
            // property names (e.g., EnableOpenLibrarySearch) instead of camelCase.
            const rawObj = raw as Record<string, unknown>
            const normalized: Record<string, unknown> = { ...rawObj }

            // Helper to prefer camelCase, then PascalCase, then fallback
            const pickBool = (camel: string, pascal: string, fallback: boolean) => {
              const c = rawObj[camel]
              const p = rawObj[pascal]
              if (c !== undefined && c !== null) return Boolean(c)
              if (p !== undefined && p !== null) return Boolean(p)
              return fallback
            }
            const pickString = (camel: string, pascal: string, fallback: string) => {
              const c = rawObj[camel]
              const p = rawObj[pascal]
              if (typeof c === 'string' && c.trim().length > 0) return c
              if (typeof p === 'string' && p.trim().length > 0) return p
              return fallback
            }

            const openlib = pickBool('enableOpenLibrarySearch', 'EnableOpenLibrarySearch', true)
            const defaultSearchRegion = pickString(
              'defaultSearchRegion',
              'DefaultSearchRegion',
              'us',
            )
            const defaultSearchLanguage = pickString(
              'defaultSearchLanguage',
              'DefaultSearchLanguage',
              'english',
            )

            // Assign normalized camelCase properties for the UI binding
            normalized.enableOpenLibrarySearch = openlib
            normalized.defaultSearchRegion = defaultSearchRegion
            normalized.defaultSearchLanguage = defaultSearchLanguage

            // Set camelCase properties for the UI binding and saving
            settings.value = normalized as unknown as ApplicationSettings

            // Sync normalized object back to the store so other consumers use it
            configStore.applicationSettings = settings.value
          } else {
            settings.value = null
          }

          try {
            await loadAdminUsers()
            loaded.admins = true
            if (adminUsers.value.length > 0 && settings.value) {
              const firstAdmin = adminUsers.value[0]
              if (firstAdmin) settings.value.adminUsername = firstAdmin.username
            }
          } catch (e) {
            logger.debug('Failed to load admin users', e)
          }

          loaded.general = true
        }
        break
      case 'bot':
        if (!loaded.bot) {
          // Requests tab needs application settings and quality profiles
          await configStore.loadApplicationSettings()
          // Reuse the same normalization logic for requests tab load
          const rawReq = configStore.applicationSettings
            ? { ...configStore.applicationSettings }
            : null
          if (rawReq) {
            const rawReqObj = rawReq as Record<string, unknown>
            const normalizedReq: Record<string, unknown> = { ...rawReqObj }
            const pickBoolReq = (camel: string, pascal: string, fallback: boolean) => {
              const c = rawReqObj[camel]
              const p = rawReqObj[pascal]
              if (c !== undefined && c !== null) return Boolean(c)
              if (p !== undefined && p !== null) return Boolean(p)
              return fallback
            }
            const pickStringReq = (camel: string, pascal: string, fallback: string) => {
              const c = rawReqObj[camel]
              const p = rawReqObj[pascal]
              if (typeof c === 'string' && c.trim().length > 0) return c
              if (typeof p === 'string' && p.trim().length > 0) return p
              return fallback
            }
            normalizedReq.enableOpenLibrarySearch = pickBoolReq(
              'enableOpenLibrarySearch',
              'EnableOpenLibrarySearch',
              true,
            )
            normalizedReq.defaultSearchRegion = pickStringReq(
              'defaultSearchRegion',
              'DefaultSearchRegion',
              'us',
            )
            normalizedReq.defaultSearchLanguage = pickStringReq(
              'defaultSearchLanguage',
              'DefaultSearchLanguage',
              'english',
            )
            settings.value = normalizedReq as unknown as ApplicationSettings
            configStore.applicationSettings = settings.value
          } else {
            settings.value = null
          }
          loaded.bot = true
        }
        break
      case 'notifications':
        // Notifications are part of general settings
        if (!loaded.general) {
          await loadTabContents('general')
        }
        break
      default:
        // default to indexers
        if (!loaded.indexers) {
          // IndexersTab manages its own loading
          loaded.indexers = true
        }
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'SettingsView',
      operation: 'loadTabContents',
      metadata: { tab },
    })
  }
}

onMounted(async () => {
  // Set initial tab from URL hash
  syncTabFromHash()

  // Load only the data needed for the active tab; other tabs load on demand
  await loadTabContents(activeTab.value)

  // Load startup config (optional) to determine AuthenticationRequired — keep this lightweight
  try {
    startupConfig.value = await apiService.getStartupConfig()
    const obj = startupConfig.value as Record<string, unknown> | null
    const raw = obj ? (obj['authenticationRequired'] ?? obj['AuthenticationRequired']) : undefined
    const v = raw as unknown
    authEnabled.value =
      typeof v === 'boolean'
        ? v
        : typeof v === 'string'
          ? v.toLowerCase() === 'enabled' || v.toLowerCase() === 'true'
          : false
  } catch {
    authEnabled.value = false
  }

  try {
    const apiKeyResponse = await apiService.getApiKey()
    apiKey.value = apiKeyResponse.apiKey ?? ''
  } catch {
    apiKey.value = ''
  }

  // Watch for tab changes and fetch content on-demand
  watch(activeTab, (t) => {
    void loadTabContents(t)
  })
})
</script>

<style scoped>
.settings-page {
  --settings-toolbar-height: 60px;
  position: relative;
  top: var(--settings-toolbar-height);
  padding: 2rem;
  background-color: #1a1a1a;
}

.settings-header {
  margin-bottom: 2rem;
}

.settings-header h1 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 2rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.settings-header h1 i {
  color: #4dabf7;
}

.settings-header p {
  margin: 0;
  color: #adb5bd;
  font-size: 1rem;
  line-height: 1.5;
}

.settings-tabs {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 2rem;
  border-bottom: 2px solid rgba(255, 255, 255, 0.08);
}

.tab-button {
  padding: 1rem 1.5rem;
  background: none;
  border: none;
  border-bottom: 3px solid transparent;
  cursor: pointer;
  font-size: 0.95rem;
  color: #868e96;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  gap: 0.65rem;
  font-weight: 500;
  position: relative;
}

.tab-button:hover {
  background-color: rgba(77, 171, 247, 0.08);
  color: #fff;
}

.tab-button.active {
  color: #4dabf7;
  background-color: rgba(77, 171, 247, 0.15);
}

.tab-button.active::after {
  content: '';
  position: absolute;
  bottom: -2px;
  left: 0;
  right: 0;
  height: 3px;
  background: #339af0;
  border-radius: 6px;
}

.settings-content {
  background: #2a2a2a;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  min-height: 500px;
  margin-top: var(--settings-toolbar-height); /* Add margin to account for fixed toolbar */
}

/* Ensure consistent ordering for settings card actions: edit -> secondary -> delete */
.settings-content .action-edit {
  order: 1;
}
.settings-content .action-secondary {
  order: 2;
}
.settings-content .action-delete {
  order: 3;
}
.settings-content .folder-actions,
.settings-content .mapping-actions,
.settings-content .indexer-actions,
.settings-content .config-actions {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}

/* Desktop tabs carousel styles */
.settings-tabs-desktop-wrapper {
  position: relative;
}

.settings-tabs-desktop {
  display: flex;
  gap: 0.5rem;
  overflow-x: auto;
  -webkit-overflow-scrolling: touch;
  scroll-behavior: smooth;
  padding-bottom: 4px; /* give space for hidden scrollbar */
  scrollbar-gutter: stable both-edges;
}

/* keep the scrollable area clipped so overflowing tabs are hidden */
.settings-tabs-desktop-wrapper {
  overflow: hidden;
}

.settings-tabs-desktop {
  align-items: center;
  white-space: nowrap;
  padding: 0 12px; /* space for chevron overlay */
  scroll-padding-left: 48px;
  scroll-padding-right: 48px;
}

.settings-tabs-desktop .tab-button {
  flex: 0 0 auto;
}

.settings-tabs-desktop::-webkit-scrollbar {
  height: 6px;
}

/* hide the native scrollbar while preserving scrollability */
.settings-tabs-desktop::-webkit-scrollbar {
  display: none;
}
.settings-tabs-desktop {
  -ms-overflow-style: none;
  scrollbar-width: none;
}

.tabs-scroll-btn {
  position: absolute;
  top: 50%;
  transform: translateY(-50%);
  width: 36px;
  height: 36px;
  border-radius: 6px;
  background: rgba(0, 0, 0, 0.8);
  color: #fff;
  border: none;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  z-index: 1;
  box-shadow: 0 6px 16px rgba(0, 0, 0, 0.5);
  transition:
    transform 0.15s ease,
    background 0.15s ease;
}

.tabs-scroll-btn.left {
  left: 0;
}

.tabs-scroll-btn.right {
  right: 0;
}

.tabs-scroll-btn:hover {
  background: rgba(0, 0, 0, 1);
  transform: translateY(-50%) scale(1.02);
}

/* Settings Toolbar */
.settings-toolbar {
  position: fixed;
  top: var(--app-top-offset, 60px); /* Account for global header + optional banner */
  left: 200px; /* Account for sidebar width */
  right: 0;
  z-index: 99; /* Below global nav (1000) but above content */
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 20px;
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
  margin-bottom: 20px;
}

.toolbar-content {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
}

.toolbar-actions {
  display: flex;
  gap: 1rem;
  align-items: center;
}

/* When tabs don't overflow hide the scrollbar and buttons via v-show in template */

.tab-content {
  padding: 2rem;
}

.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

/* `.add-button` visuals are centralized in `src/assets/buttons.css`.
    Local duplication removed; use `.add-button` or `.btn.btn-primary` as needed. */

/* Disabled visual state is handled by the centralized button rules */

/* save-button disabled state handled by centralized button rules */

.empty-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #868e96;
}

.empty-state .empty-icon {
  font-size: 4rem;
  color: #868e96;
  margin-bottom: 1rem;
  width: 4rem;
  height: 4rem;
}

.empty-state h3 {
  margin: 1rem 0 0.5rem 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.empty-state p {
  margin: 0.5rem 0;
  font-size: 1.05rem;
  line-height: 1.6;
  color: #adb5bd;
}

.empty-state .empty-help {
  font-size: 0.95rem;
  color: #868e96;
  margin-bottom: 2rem;
}

/* `.add-button-large` visuals are centralized in `src/assets/buttons.css`.
    Local duplication removed; use `.add-button-large` or `.btn-lg` as needed. */

.section-title-wrapper {
  flex: 1;
}

.section-subtitle {
  margin: 0.5rem 0 0 0;
  font-size: 0.95rem;
  color: #868e96;
  font-weight: normal;
}

/* Webhook Grid Layout */
.webhooks-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(450px, 1fr));
  gap: 1.5rem;
}

/* Webhook Card */
.webhook-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  overflow: hidden;
  transition: all 0.2s ease;
  display: flex;
  flex-direction: column;
}

.webhook-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.15);
}

.webhook-card.disabled {
  opacity: 0.5;
  filter: grayscale(50%);
}

.webhook-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  cursor: pointer;
}

/* No hover state: matches other headers */

.webhook-header-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: 1rem;
}

.webhook-title-row {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex: 1;
}

.webhook-icon {
  width: 40px;
  height: 40px;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  font-size: 1.2rem;
}

.webhook-icon.service-slack {
  background: #4a154b;
  color: #fff;
}

.webhook-icon.service-discord {
  background: #5865f2;
  color: #fff;
}

.webhook-icon.service-telegram {
  background: #0088cc;
  color: #fff;
}

.webhook-icon.service-pushover {
  background: #249df1;
  color: #fff;
}

.webhook-icon.service-pushbullet {
  background: #4ab367;
  color: #fff;
}

.webhook-icon.service-ntfy {
  background: #ff6b6b;
  color: #fff;
}

.webhook-icon.service-zapier {
  background: #ff4a00;
  color: #fff;
}

.webhook-info h4 {
  margin: 0 0 0.25rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.webhook-meta {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.triggers-preview {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.trigger-badge-small {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: 1px solid;
  cursor: help;
  transition: all 0.2s ease;
}

.trigger-badge-small:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 8px rgba(0, 0, 0, 0.2);
}

.trigger-badge-small svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.trigger-badge-small.trigger-added {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border-color: rgba(76, 175, 80, 0.3);
}

.trigger-badge-small.trigger-downloading {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border-color: rgba(77, 171, 247, 0.3);
}

.trigger-badge-small.trigger-available {
  background-color: rgba(156, 39, 176, 0.15);
  color: #b197fc;
  border-color: rgba(156, 39, 176, 0.3);
}

.webhook-type-badge {
  display: inline-block;
  padding: 0.25rem 0.65rem;
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.3);
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  letter-spacing: 0.5px;
}

.expand-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background-color: rgba(255, 255, 255, 0.05);
  color: #adb5bd;
  cursor: pointer;
  transition: all 0.2s ease;
}

.expand-toggle:hover {
  background-color: rgba(77, 171, 247, 0.15);
  border-color: rgba(77, 171, 247, 0.3);
  color: #4dabf7;
}

.expand-toggle svg {
  width: 18px;
  height: 18px;
  transition: transform 0.3s ease;
}

.expand-toggle.expanded svg {
  transform: rotate(180deg);
}

/* Expand/Collapse Animation */
.expand-enter-active,
.expand-leave-active {
  transition: all 0.3s ease;
  overflow: hidden;
}

.expand-enter-from,
.expand-leave-to {
  max-height: 0;
  opacity: 0;
  padding-top: 0;
  padding-bottom: 0;
}

.expand-enter-to,
.expand-leave-from {
  max-height: 500px;
  opacity: 1;
}

.webhook-body {
  padding: 1.5rem;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

.webhook-url-container {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  background-color: rgba(0, 0, 0, 0.3);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  overflow: hidden;
}

.url-icon {
  color: #4dabf7;
  font-size: 1.1rem;
  flex-shrink: 0;
}

.webhook-url {
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 0.85rem;
  color: #adb5bd;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.webhook-triggers-section {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.triggers-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #868e96;
  font-size: 0.85rem;
  font-weight: 500;
  letter-spacing: 0.5px;
}

.triggers-label {
  color: #adb5bd;
}

.triggers-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.trigger-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 0.85rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
  border: 1px solid;
}

.trigger-badge svg {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.trigger-badge.trigger-added {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border-color: rgba(76, 175, 80, 0.3);
}

.trigger-badge.trigger-downloading {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border-color: rgba(77, 171, 247, 0.3);
}

.trigger-badge.trigger-available {
  background-color: rgba(156, 39, 176, 0.15);
  color: #b197fc;
  border-color: rgba(156, 39, 176, 0.3);
}

.config-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.config-card {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem;
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  transition: all 0.2s ease;
}

.config-card:hover {
  background-color: #2f2f2f;
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.config-info {
  flex: 1;
  min-width: 0;
}

.config-info h4 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.config-url {
  margin: 0 0 1rem 0;
  color: #4dabf7;
  font-family: 'Courier New', monospace;
  font-size: 0.9rem;
  overflow-wrap: break-word;
  word-break: break-all;
}

.config-meta {
  display: flex;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.config-meta span {
  padding: 0.4rem 0.8rem;
  border-radius: 6px;
  font-size: 0.8rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.config-type {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.3);
}

.config-status {
  background-color: rgba(231, 76, 60, 0.15);
  color: #ff6b6b;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

.config-status.enabled {
  background-color: rgba(46, 204, 113, 0.15);
  color: #51cf66;
  border: 1px solid rgba(46, 204, 113, 0.3);
}

.config-priority {
  background-color: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
  border: 1px solid rgba(155, 89, 182, 0.3);
}

.config-ssl {
  background-color: rgba(127, 140, 141, 0.15);
  color: #95a5a6;
  border: 1px solid rgba(127, 140, 141, 0.3);
}

.config-ssl.enabled {
  background-color: rgba(241, 196, 15, 0.15);
  color: #fcc419;
  border: 1px solid rgba(241, 196, 15, 0.3);
}

.config-actions {
  display: flex;
  gap: 0.5rem;
  flex-shrink: 0;
}
/* Using centralized `.edit-button` / `.delete-button` from `src/assets/buttons.css` */

.settings-form {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.form-section {
  margin-bottom: 2rem;
}

.form-section:last-child {
  margin-bottom: 0;
}

.form-section h3 {
  color: #fff;
  font-size: 1.1rem;
  margin: 0 0 1rem 0;
  padding-bottom: 0.5rem;
  border-bottom: 1px solid #444;
}

.form-section h4 {
  margin: 0 0 1.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.65rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.form-section h4 i {
  color: #4dabf7;
}

.form-group {
  margin-bottom: 1.5rem;
}

.form-group:last-child {
  margin-bottom: 0;
}

.form-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1rem;
  margin-bottom: 1.5rem;
}

.form-group label {
  display: block;
  margin-bottom: 0.5rem;
  color: #fff;
  font-weight: 500;
  font-size: 0.95rem;
}

.form-group input,
.form-group select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.form-group input::placeholder {
  color: #999;
  opacity: 1;
}

.form-group input:-webkit-autofill,
.form-group input:-webkit-autofill:hover,
.form-group input:-webkit-autofill:focus {
  -webkit-box-shadow: 0 0 0 1000px #1a1a1a inset !important;
  -webkit-text-fill-color: #fff !important;
  border: 1px solid #444 !important;
}

.form-group input:disabled,
.form-group select:disabled {
  opacity: 0.6;
  cursor: not-allowed;
  background-color: #0d0d0d;
}

.form-group select option:hover,
.form-group select option:focus,
.form-group select option:checked {
  background-color: #005a9e;
  color: #ffffff;
  border: none;
}

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.form-group input:focus-visible,
.form-group select:focus-visible {
  outline: 2px solid rgba(var(--brand-rgb), 0.9);
  outline-offset: 2px;
}

/* Base checkbox-group styles are provided globally via `src/styles/global.css`.
   Per-view overrides below customize layout/colours where needed. */

.form-help {
  font-size: 0.85rem;
  color: #868e96;
  font-style: italic;
  line-height: 1.5;
}

/* Invite controls for Discord bot */
.invite-row {
  margin-top: 1rem;
}
.invite-controls {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-bottom: 0.5rem;
} /* Use centralized `.invite-button` / `.btn-primary` styles from `src/assets/buttons.css` */
.invite-link-preview small a {
  color: #74c0fc;
  text-decoration: underline;
}
.invite-link-preview small a {
  /* allow long oauth links to wrap cleanly in the preview */
  word-break: break-all;
  white-space: normal;
}
.invite-controls .icon-button {
  /* When using icon-style buttons inside invite-controls we want them to
     expand to fit labels (e.g. "Copy Invite Link") instead of being forced
     into the square icon-button size used elsewhere in the UI. */
  width: auto;
  height: auto;
  min-width: 36px;
  padding: 0.45rem 0.75rem;
  font-size: 0.95rem;
}

.invite-controls .btn.btn-primary {
  /* keep primary register action prominent but avoid forcing full-width */
  white-space: nowrap;
}
.discord-status {
  margin-top: 0.5rem;
}
.status-pill {
  display: inline-block;
  padding: 0.35rem 0.6rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}
.status-pill.installed {
  background-color: rgba(46, 204, 113, 0.12);
  color: #2ecc71;
  border: 1px solid rgba(46, 204, 113, 0.18);
}
.status-pill.not-installed {
  background-color: rgba(244, 67, 54, 0.08);
  color: #ff6b6b;
  border: 1px solid rgba(244, 67, 54, 0.12);
}
.status-pill.unknown {
  background-color: rgba(77, 171, 247, 0.08);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.12);
}

.checkbox-group {
  flex-direction: row;
  align-items: flex-start;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  padding: 1rem;
  margin-bottom: 1rem;
  transition: all 0.2s ease;
}

.checkbox-group:hover {
  background-color: rgba(0, 0, 0, 0.3);
  border-color: rgba(77, 171, 247, 0.2);
}

.checkbox-group:last-child {
  margin-bottom: 0;
}

.checkbox-group label {
  display: flex;
  align-items: flex-start;
  gap: 1rem;
  cursor: pointer;
  width: 100%;
}

.checkbox-group input[type='checkbox'] {
  margin: 0.25rem 0 0 0;
  width: 18px;
  height: 18px;
  cursor: pointer;
  flex-shrink: 0;
  accent-color: #4dabf7;
}

.checkbox-group label span {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.checkbox-group label strong {
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
}

.checkbox-group label small {
  color: #868e96;
  font-size: 0.85rem;
  font-weight: normal;
  line-height: 1.5;
}

/* Authentication Section Styles */
.auth-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0.5rem;
}

.auth-row input[type='checkbox'] {
  width: 18px;
  height: 18px;
  cursor: pointer;
  accent-color: #4dabf7;
}

.auth-row label {
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
  cursor: pointer;
  margin: 0;
}

.admin-credentials {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-top: 0.5rem;
}

.admin-input {
  padding: 0.75rem;
  background-color: rgba(0, 0, 0, 0.2);
  border: 2px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  font-size: 1rem;
  color: #fff;
  transition: all 0.2s ease;
}

.admin-input:focus {
  outline: none;
  border-color: #4dabf7;
  background-color: rgba(0, 0, 0, 0.3);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.15);
}

.admin-input::placeholder {
  color: #6c757d;
  font-style: italic;
}

/* Password field with inline toggle */
.password-field {
  position: relative;
  width: 100%;
}

.password-input {
  width: 100%;
  padding-right: 3.5rem; /* space for the toggle button */
}

.password-toggle {
  position: absolute;
  right: 0.5rem;
  top: 50%;
  transform: translateY(-50%);
  background: none;
  border: none;
  color: #868e96;
  padding: 0.35rem;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  transition: color 0.2s ease;
}

.password-toggle:hover {
  color: #4dabf7;
}

.form-error {
  color: #ff6b6b;
  font-size: 0.9rem;
  margin-top: 0.4rem;
}

.info-inline {
  background: none;
  border: none;
  color: #74c0fc;
  margin-left: 0.5rem;
  cursor: pointer;
  transition: color 0.2s ease;
}

.info-inline:hover {
  color: #4dabf7;
}

.error-summary {
  margin-top: 1rem;
  background: rgba(231, 76, 60, 0.1);
  border: 1px solid rgba(231, 76, 60, 0.2);
  padding: 0.75rem 1rem;
  border-radius: 6px;
  color: #ff6b6b;
}

.error-summary ul {
  margin: 0.5rem 0 0 1.2rem;
}

.input-group-btn.regenerate-button {
  background: #e74c3c;
  color: white;
  border: none;
  padding: 0.75rem 1rem;
  cursor: pointer;
  transition: all 0.2s ease;
  font-weight: 500;
  gap: 0.5rem;
  font-size: 0.9rem;
}

.input-group-btn.regenerate-button:hover:not(:disabled) {
  background: #c0392b;
}

.input-group-btn.regenerate-button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Input group styling for API key */
.input-group {
  display: flex;
  align-items: stretch;
  border: 2px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  overflow: hidden;
}

.input-group:focus-within {
  border-color: rgba(77, 171, 247, 0.3);
}

.input-group-input {
  flex: 1;
  background: #1a1a1a !important;
  color: #adb5bd;
  padding: 0.75rem 1rem;
  border: none !important;
  border-radius: 6px !important;
  box-shadow: none !important;
}

.input-group-input:focus {
  outline: none;
  background: #1a1a1a !important;
  box-shadow: none !important;
}

.input-group-append {
  display: flex;
  background: rgba(0, 0, 0, 0.3);
}

.input-group-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: rgba(255, 255, 255, 0.05);
  border: none;
  border-radius: 6px;
  border-left: 1px solid rgba(255, 255, 255, 0.1);
  color: #868e96;
  padding: 0.75rem 1rem;
  cursor: pointer;
  transition: all 0.2s ease;
  font-size: 1rem;
}

.input-group-btn:first-child {
  border-left: none;
}

.input-group-btn:hover:not(:disabled) {
  background: rgba(77, 171, 247, 0.2);
  color: #4dabf7;
}

.input-group-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.input-group-btn.copied {
  background: rgba(81, 207, 102, 0.2) !important;
  color: #51cf66 !important;
}

.input-group-btn.copied:hover {
  background: rgba(81, 207, 102, 0.3) !important;
}

.test-button {
  background: #1e88e5;
  color: white;
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1rem;
  font-weight: 500;
  transition: all 0.2s ease;
  border: none;
  border-left: 1px solid rgba(0, 0, 0, 0.2);
  border-radius: 6px;
  font-size: 0.9rem;
  cursor: pointer;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.test-button:hover:not(:disabled) {
  background: var(--brand-600);
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.4);
}

.test-button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Indexer Styles */
.indexers-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
  gap: 1.5rem;
  margin-top: 1.5rem;
}

.indexer-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  transition: all 0.2s ease;
}

.indexer-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.15);
}

.indexer-card.disabled {
  opacity: 0.5;
  filter: grayscale(50%);
}

.indexer-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 1.5rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.indexer-info h4 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.indexer-actions {
  display: flex;
  gap: 0.5rem;
  margin-left: 1rem;
}

.indexer-type {
  display: inline-block;
  padding: 0.3rem 0.75rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.indexer-type.torrent {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border: 1px solid rgba(76, 175, 80, 0.3);
}

.indexer-type.usenet {
  background-color: rgba(33, 150, 243, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(33, 150, 243, 0.3);
}

.indexer-type.ddl {
  background-color: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
  border: 1px solid rgba(155, 89, 182, 0.3);
}

.indexer-actions {
  display: flex;
  gap: 0.5rem;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */

.indexer-details {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.5rem;
}

.detail-row {
  display: flex;
  align-items: center;
  gap: 0.65rem;
  font-size: 0.9rem;
}

.detail-row i {
  color: #4dabf7;
  font-size: 1rem;
  flex-shrink: 0;
}

.detail-label {
  color: #868e96;
  min-width: 100px;
}

.detail-value {
  color: #adb5bd;
  word-break: break-all;
}

.detail-value.success {
  color: #51cf66;
}

.detail-value.error {
  color: #ff6b6b;
}

.detail-value i {
  margin-left: 0.5rem;
}

.feature-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

/* Badge styles - Now using Pill component from @/components/base */

.error-message {
  margin-top: 0.5rem;
  padding: 0.75rem;
  background-color: rgba(244, 67, 54, 0.1);
  border: 1px solid rgba(244, 67, 54, 0.2);
  border-radius: 6px;
  color: #ff6b6b;
  font-size: 0.85rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.error-message i {
  font-size: 1rem;
}

@media (max-width: 768px) {
  .settings-page {
    padding: 1rem;
  }

  .settings-tabs {
    flex-direction: column;
    gap: 0;
  }

  .tab-button {
    border-bottom: 1px solid #333;
    border-left: 3px solid transparent;
    justify-content: flex-start;
  }

  .tab-button.active {
    border-left-color: var(--brand-500);
    border-bottom-color: transparent;
  }

  .config-card {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .config-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .section-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .add-button,
  .btn.btn-primary {
    width: 100%;
    justify-content: center;
  }

  .settings-toolbar {
    left: 0; /* Full width on mobile */
  }

  .toolbar-content {
    flex-direction: column;
    gap: 1rem;
    align-items: stretch;
  }

  .toolbar-actions {
    justify-content: center;
  }

  .indexers-grid {
    grid-template-columns: 1fr;
  }

  .indexer-header {
    flex-direction: column;
    gap: 1rem;
  }

  .indexer-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .detail-row {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.25rem;
  }

  .detail-label {
    min-width: auto;
  }
}

/* Quality Profile Cards */
.profiles-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(500px, 1fr));
  gap: 1.5rem;
}

.profile-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  overflow: hidden;
  transition: all 0.2s ease;
}

.profile-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.15);
  transform: translateY(-1px);
}

.profile-card.is-default {
  border-color: rgba(77, 171, 247, 0.3);
  background: rgba(77, 171, 247, 0.05);
}

.profile-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 1.5rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.profile-title-section {
  flex: 1;
}

.profile-name-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0.5rem;
}

.profile-card h4 {
  margin: 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.3rem 0.7rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.status-badge.default {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border: 1px solid rgba(76, 175, 80, 0.3);
}

.profile-description {
  margin: 0;
  color: #868e96;
  font-size: 0.9rem;
  line-height: 1.5;
}

.profile-actions {
  display: flex;
  gap: 0.5rem;
  margin-left: 1rem;
}

.profile-content {
  padding: 1.5rem;
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.profile-section {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.profile-section h5 {
  margin: 0;
  color: #4dabf7;
  font-size: 0.9rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.profile-section h5 i {
  font-size: 1rem;
}

/* Quality Badges */
.quality-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.quality-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.4rem 0.75rem;
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.3);
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.quality-badge.is-cutoff {
  background-color: rgba(255, 152, 0, 0.15);
  color: #ff9800;
  border-color: rgba(255, 152, 0, 0.3);
}

.quality-badge i {
  font-size: 0.75rem;
}

/* Preferences Grid */
.preferences-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
}

.preference-item {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.preference-label {
  color: #868e96;
  font-size: 0.8rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  font-weight: 500;
}

.preference-value {
  color: #fff;
  font-size: 0.9rem;
}

/* Limits Grid */
.limits-grid {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.limit-item {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
}

.limit-item i {
  color: #4dabf7;
  font-size: 1.1rem;
}

.limit-label {
  color: #868e96;
  font-size: 0.85rem;
  min-width: 80px;
}

.limit-value {
  color: #fff;
  font-size: 0.9rem;
  font-weight: 500;
}

/* Word Filters */
.word-filters {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.word-filter-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.filter-type {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #868e96;
  font-size: 0.8rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  font-weight: 500;
}

.filter-type i {
  font-size: 0.9rem;
}

.word-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.word-tag {
  padding: 0.35rem 0.65rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.word-tag.positive {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border: 1px solid rgba(76, 175, 80, 0.3);
}

.word-tag.required {
  background-color: rgba(255, 152, 0, 0.15);
  color: #fcc419;
  border: 1px solid rgba(255, 152, 0, 0.3);
}

.word-tag.forbidden {
  background-color: rgba(244, 67, 54, 0.15);
  color: #ff6b6b;
  border: 1px solid rgba(244, 67, 54, 0.3);
}

.warning-text {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem;
  background-color: rgba(255, 152, 0, 0.1);
  border-left: 3px solid #fcc419;
  color: #fcc419;
  margin: 1rem 0;
  border-radius: 6px;
}

.warning-text i {
  font-size: 1.2rem;
}

@media (max-width: 768px) {
  .settings-page {
    padding: 1rem;
  }

  .settings-tabs {
    flex-direction: column;
    gap: 0;
  }

  .tab-button {
    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
    border-left: 3px solid transparent;
    justify-content: flex-start;
  }

  .tab-button.active::after {
    display: none;
  }

  .tab-button.active {
    border-left-color: #4dabf7;
    border-bottom-color: transparent;
  }

  .config-card {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .config-info {
    width: 100%;
  }

  .config-info h4 {
    font-size: 1rem;
  }

  .config-url {
    font-size: 0.8rem;
    word-break: break-all;
    white-space: normal;
    margin-right: 1rem;
  }

  .config-meta {
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .config-meta span {
    font-size: 0.75rem;
    padding: 0.3rem 0.6rem;
  }

  .config-triggers {
    width: 100%;
  }

  .config-actions {
    width: 100%;
    justify-content: flex-end;
    gap: 0.75rem;
  }

  .config-actions .icon-button {
    padding: 0.6rem;
  }

  .section-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .section-header h3 {
    font-size: 1.3rem;
  }

  .add-button,
  .btn.btn-primary {
    width: 100%;
    justify-content: center;
  }

  .indexers-grid {
    grid-template-columns: 1fr;
  }

  .indexer-header {
    flex-direction: column;
    gap: 1rem;
  }

  .indexer-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .detail-row {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.25rem;
  }

  .detail-label {
    min-width: auto;
  }

  .profiles-grid {
    grid-template-columns: 1fr;
  }

  .profile-header {
    flex-direction: column;
    gap: 1rem;
  }

  .profile-actions {
    margin-left: 0;
    width: 100%;
    justify-content: flex-start;
  }
}

/* Webhook Modal Specific Styles */

/* modal-footer styles are centralized in src/assets/modals.css; webhook modal uses `.webhook-modal .modal-footer` for special padding */

/* Button color variants centralized in `src/assets/modals.css` */
/* Only add webhook-modal scoped overrides when absolutely needed */
/* `.btn-primary` is centralized in `src/assets/buttons.css` */

/* Webhook Modal Responsive Styles */
@media (max-width: 768px) {
  .webhooks-grid {
    grid-template-columns: 1fr;
  }

  .webhook-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .webhook-title-row {
    width: 100%;
  }

  .webhook-header-actions {
    width: 100%;
    justify-content: space-between;
  }

  .webhook-actions {
    grid-template-columns: 1fr 1fr;
  }

  .action-btn.toggle-btn,
  .action-btn.delete-btn {
    grid-column: span 1;
  }

  .action-btn.test-btn,
  .action-btn.edit-btn {
    grid-column: span 1;
  }

  .webhook-modal {
    width: 95%;
    max-height: 95vh;
  }

  .webhook-modal .modal-header,
  .webhook-modal .modal-body,
  .webhook-modal .modal-footer {
    padding: 1.25rem 1.5rem;
  }

  .webhook-modal .modal-icon {
    width: 48px;
    height: 48px;
  }

  .webhook-modal .modal-icon svg {
    width: 24px;
    height: 24px;
  }

  .webhook-modal h2,
  .webhook-modal .modal-title h3 {
    font-size: 1.3rem;
  }

  .webhook-form .form-row {
    flex-direction: column;
  }

  .trigger-content {
    gap: 0.75rem;
  }

  .trigger-icon {
    width: 40px;
    height: 40px;
  }

  .trigger-icon svg {
    width: 20px;
    height: 20px;
  }

  .trigger-check {
    width: 24px;
    height: 24px;
  }

  .triggers-section .section-title {
    flex-direction: column;
    align-items: flex-start;
  }

  .trigger-count {
    align-self: flex-start;
  }
}

/* Discord Bot Process Controls */
.bot-status-section {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.bot-status-display {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 1rem;
  background-color: var(--card-bg);
  border-radius: 6px;
  border: 1px solid var(--border-color);
}

.status-indicator {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 50%;
}

.status-indicator.status-running {
  color: #4caf50;
}

.status-indicator.status-stopped {
  color: #f44336;
}

.status-indicator.status-checking {
  color: #ff9800;
}

.status-indicator.status-error {
  color: #f44336;
}

.status-indicator.status-unknown {
  color: #9e9e9e;
}

.status-text {
  font-size: 0.9rem;
}

.bot-controls {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.status-button,
.start-button,
.stop-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-size: 0.9rem;
  transition: all 0.2s ease;
}

.status-button {
  background-color: var(--brand-500);
  color: white;
}

.status-button:hover:not(:disabled) {
  background-color: var(--brand-600);
}

.start-button {
  background-color: #4caf50;
  color: white;
}

.start-button:hover:not(:disabled) {
  background-color: #388e3c;
}

.stop-button {
  background-color: #f44336;
  color: white;
}

.stop-button:hover:not(:disabled) {
  background-color: #d32f2f;
}

.status-button:disabled,
.start-button:disabled,
.stop-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* Mobile styles for bot controls */
@media (max-width: 768px) {
  .bot-status-display {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.5rem;
  }

  .bot-controls {
    width: 100%;
  }

  .status-button,
  .start-button,
  .stop-button {
    flex: 1;
    justify-content: center;
  }
}

/* Mobile settings tabs */
@media (max-width: 768px) {
  .settings-tabs {
    flex-direction: column;
    gap: 1rem;
    border-bottom: unset;
  }

  .settings-tabs-mobile {
    display: block;
  }

  .settings-tabs-desktop {
    display: none;
  }

  .tab-dropdown {
    width: 100%;
    color: #fff;
    font-size: 0.95rem;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .tab-dropdown:focus {
    outline: none;
    border-color: #4dabf7;
    box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
  }

  .tab-dropdown option {
    background-color: #2a2a2a;
    color: #fff;
  }
}

/* Desktop settings tabs */
@media (min-width: 769px) {
  .settings-tabs {
    flex-direction: row;
  }

  .settings-tabs-mobile {
    display: none;
  }

  .settings-tabs-desktop {
    display: flex;
  }
}
</style>
