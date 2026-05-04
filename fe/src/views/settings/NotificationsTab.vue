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
  <div class="tab-content">
    <div class="notifications-tab">
      <div class="section-header">
        <h3>
          Notifications
          <PhSpinner v-if="loading" class="ph-spin small-inline-spinner" />
        </h3>
      </div>

      <LoadingState v-if="loading && webhooks.length === 0" message="Loading notifications..." />

      <div v-else-if="webhooks.length === 0" class="empty-state">
        <PhBellSlash class="empty-icon" />
        <h3>No webhooks configured</h3>
        <p>Webhooks allow you to receive real-time notifications when important events occur.</p>
        <p class="empty-help">
          Supported services include Slack, Discord, Telegram, Pushover, and more.
        </p>
      </div>

      <div v-else class="webhooks-grid">
        <div
          v-for="webhook in webhooks"
          :key="webhook.id"
          class="webhook-card"
          :class="{ disabled: !webhook.isEnabled }"
        >
          <div class="webhook-header">
            <div class="webhook-title-row">
              <div class="webhook-info">
                <h4 class="webhook-title">
                  <component
                    :is="getWebhookTypeIcon(webhook.type)"
                    class="webhook-type-icon"
                    :title="webhook.type"
                    :aria-label="webhook.type"
                    role="img"
                  />
                  <span class="webhook-name">{{ webhook.name }}</span>
                </h4>
                <div class="webhook-meta">
                  <div class="triggers-preview">
                    <span
                      v-for="trigger in orderedTriggers(webhook.triggers)"
                      :key="trigger"
                      class="trigger-badge-small"
                      :class="getTriggerClass(trigger)"
                      :title="formatTriggerName(trigger)"
                    >
                      <component :is="getTriggerIcon(trigger)" />
                    </span>
                  </div>
                </div>
              </div>
            </div>
            <div class="webhook-header-actions">
              <button
                class="icon-button action-secondary action-toggle"
                :class="{ active: webhook.isEnabled }"
                :title="webhook.isEnabled ? 'Disable webhook' : 'Enable webhook'"
                @click.stop="toggleWebhook(webhook)"
              >
                <component :is="webhook.isEnabled ? PhToggleRight : PhToggleLeft" />
              </button>

              <button
                class="icon-button action-secondary"
                :class="{
                  'test-success': lastWebhookTestResults[webhook.id] === 'success',
                  'test-fail': lastWebhookTestResults[webhook.id] === 'fail',
                }"
                :title="!webhook.isEnabled ? 'Enable webhook to test' : 'Send test notification'"
                @click.stop="testWebhook(webhook)"
                :disabled="testingWebhook === webhook.id || !webhook.isEnabled"
              >
                <PhSpinner v-if="testingWebhook === webhook.id" class="ph-spin" />
                <template v-else-if="lastWebhookTestResults[webhook.id] === 'success'">
                  <PhCheckCircle />
                </template>
                <template v-else-if="lastWebhookTestResults[webhook.id] === 'fail'">
                  <PhXCircle />
                </template>
                <template v-else>
                  <PhPaperPlaneTilt />
                </template>
              </button>

              <button
                class="icon-button action-edit"
                title="Edit webhook"
                @click.stop="editWebhook(webhook)"
              >
                <PhPencil />
              </button>

              <button
                class="icon-button danger action-delete"
                title="Delete webhook"
                @click.stop="confirmDeleteWebhook(webhook)"
              >
                <PhTrash />
              </button>
            </div>
          </div>

          <div class="webhook-body">
            <div class="webhook-url-container">
              <PhLink class="url-icon" />
              <span class="webhook-url">{{ webhook.url }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Webhook Configuration Modal (shared Modal component) -->
      <Modal
        class="webhook-modal"
        :visible="showWebhookForm"
        size="md"
        :title="editingWebhook ? 'Edit Webhook' : 'Add Webhook'"
        @close="closeWebhookForm"
      >
        <template #header>
          <ModalHeader
            :title="(editingWebhook ? 'Edit' : 'Add') + ' Webhook'"
            :icon="PhLink"
            @close="closeWebhookForm"
          />
        </template>

        <form @submit.prevent="saveWebhook">
          <!-- Delete Webhook Confirmation Modal (shared) -->
          <DeleteConfirmationModal
            :visible="!!webhookToDelete"
            title="Delete Webhook"
            @close="webhookToDelete = null"
            @confirm="executeDeleteWebhook"
          >
            <template v-slot>
              <p>
                Are you sure you want to delete the webhook
                <strong>{{ webhookToDelete?.name }}</strong
                >?
              </p>
              <p>This action cannot be undone.</p>
            </template>
          </DeleteConfirmationModal>

          <!-- Activation -->
          <FormSection title="Activation" :icon="PhToggleRight">
            <CheckboxCard
              v-model="webhookForm.isEnabled"
              title="Enable"
              description="Enable this webhook to start receiving notifications"
            />
          </FormSection>

          <!-- Basic Configuration Section -->
          <FormSection title="Basic" :icon="PhInfo">
            <FormRow label="Name *" labelFor="webhook-name">
              <input
                id="webhook-name"
                v-model="webhookForm.name"
                type="text"
                placeholder="e.g., Production Slack Channel"
                required
                @blur="validateWebhookField('name')"
              />
              <small v-if="webhookFormErrors.name" class="error-text">{{
                webhookFormErrors.name
              }}</small>
            </FormRow>

            <FormRow label="Type *" labelFor="webhook-type">
              <select
                id="webhook-type"
                v-model="webhookForm.type"
                required
                @change="onServiceTypeChange"
                @blur="validateWebhookField('type')"
              >
                <option value="" disabled>Select type...</option>
                <option value="Slack">Slack</option>
                <option value="Discord">Discord</option>
                <option value="Telegram">Telegram</option>
                <option value="Pushover">Pushover</option>
                <option value="Pushbullet">Pushbullet</option>
                <option value="NTFY">NTFY</option>
                <option value="Zapier">Zapier / Generic</option>
              </select>
              <small v-if="webhookFormErrors.type" class="error-text">{{
                webhookFormErrors.type
              }}</small>
              <small v-else-if="getServiceHelp()">{{ getServiceHelp() }}</small>
            </FormRow>

            <FormRow
              v-if="
                webhookForm.type !== 'Telegram' &&
                webhookForm.type !== 'Pushover' &&
                webhookForm.type !== 'Pushbullet'
              "
              label="Webhook URL *"
              labelFor="webhook-url"
            >
              <input
                id="webhook-url"
                v-model="webhookForm.url"
                type="url"
                placeholder="https://hooks.example.com/services/your-webhook-url"
                required
                @blur="validateWebhookField('url')"
              />
              <small v-if="webhookFormErrors.url" class="error-text">{{
                webhookFormErrors.url
              }}</small>
            </FormRow>

            <FormRow
              v-if="webhookForm.type === 'Telegram'"
              label="Bot Token *"
              labelFor="telegram-bot-token"
            >
              <input
                id="telegram-bot-token"
                v-model="webhookForm.telegramBotToken"
                type="text"
                placeholder="123456:ABCdefGhIJklMNopqRst_uvwxYZ"
                required
                @blur="validateWebhookField('url')"
              />
              <small v-if="webhookFormErrors.url" class="error-text">{{
                webhookFormErrors.url
              }}</small>
            </FormRow>
            <FormRow
              v-if="webhookForm.type === 'Pushover'"
              label="Pushover User Key"
              labelFor="pushover-user-key"
            >
              <input
                id="pushover-user-key"
                v-model="webhookForm.pushoverUserKey"
                type="text"
                placeholder="User key (e.g., uQiRzpo4DXghDmr9QzzfQu27cmVRsG)"
              />
            </FormRow>

            <FormRow
              v-if="webhookForm.type === 'Pushover'"
              label="Pushover API Token"
              labelFor="pushover-api-token"
            >
              <input
                id="pushover-api-token"
                v-model="webhookForm.pushoverApiToken"
                type="text"
                placeholder="Application API token (keep secret)"
              />
              <small v-if="webhookForm.type === 'Pushover'" class="help-text"
                >You can provide both keys instead of a full webhook URL; they'll be composed on
                save.</small
              >
            </FormRow>
            <FormRow
              v-if="webhookForm.type === 'Pushbullet'"
              label="Pushbullet Access Token"
              labelFor="pushbullet-access-token"
            >
              <input
                id="pushbullet-access-token"
                v-model="webhookForm.pushbulletAccessToken"
                type="text"
                placeholder="Access token (keep secret)"
                required
                @blur="validateWebhookField('url')"
              />
              <small v-if="webhookForm.type === 'Pushbullet'" class="help-text"
                >Get your Access Token from Pushbullet → Settings → Account → Access Tokens</small
              >
            </FormRow>
            <FormRow
              v-if="webhookForm.type === 'Telegram'"
              label="Chat ID (optional)"
              labelFor="telegram-chat-id"
            >
              <input
                id="telegram-chat-id"
                v-model="webhookForm.telegramChatId"
                type="text"
                placeholder="e.g., 123456789 or @channelusername"
              />
              <small class="help-text"
                >Provide a chat ID to target messages. If left blank, include chat_id in the
                URL.</small
              >
            </FormRow>
          </FormSection>

          <!-- Triggers Section -->
          <FormSection title="Triggers" :icon="PhBell">
            <div class="webhook-triggers triggers-grid">
              <CheckboxCard
                v-for="t in ['book-added', 'book-downloading', 'book-available', 'book-completed']"
                :key="t"
                :modelValue="webhookForm.triggers.includes(t)"
                @update:modelValue="onToggleTriggerValue(t, $event)"
                :title="formatTriggerName(t)"
              >
                <template #default>
                  <component :is="getTriggerIcon(t)" class="trigger-icon" />
                </template>
              </CheckboxCard>
            </div>
            <small v-if="webhookFormErrors.triggers" class="error-text">{{
              webhookFormErrors.triggers
            }}</small>
          </FormSection>
        </form>
        <template #footer>
          <ModalFooter :showCancel="false">
            <template #left>
              <button @click="closeWebhookForm" class="cancel-button btn" type="button">
                <PhX /> Cancel
              </button>
            </template>
            <template #default>
              <button
                v-if="webhookForm.type && !editingWebhook"
                @click="testWebhookConfig"
                class="btn btn-info"
                type="button"
                :disabled="testingWebhookConfig"
              >
                <PhSpinner v-if="testingWebhookConfig" class="ph-spin" />
                {{ testingWebhookConfig ? 'Testing...' : 'Test' }}
              </button>
              <button
                @click="saveWebhook"
                class="btn btn-primary"
                type="button"
                :disabled="!isWebhookFormValid || savingWebhook"
              >
                <PhSpinner v-if="savingWebhook" class="ph-spin" />
                {{ savingWebhook ? 'Saving...' : editingWebhook ? 'Update' : 'Save' }}
              </button>
            </template>
          </ModalFooter>
        </template>
      </Modal>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, nextTick } from 'vue'
import {
  PhBell,
  PhBellSlash,
  PhPlus,
  PhCheckCircle,
  PhXCircle,
  PhCircleWavyCheck,
  PhToggleRight,
  PhToggleLeft,
  PhSpinner,
  PhPaperPlaneTilt,
  PhPencil,
  PhTrash,
  PhLink,
  PhX,
  PhDownloadSimple,
  PhSlackLogo,
  PhDiscordLogo,
  PhTelegramLogo,
  PhPushPinSimple,
  PhInfo,
} from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalFooter } from '@/components/feedback'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
// Checkbox not used directly here; CheckboxCard wraps checkbox UI
import FormSection from '@/components/settings/FormSection.vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import { LoadingState } from '@/components/base'
import { errorTracking } from '@/services/errorTracking'
import { useToast } from '@/services/toastService'
import { useConfigurationStore } from '@/stores/configuration'
import type { ApplicationSettings } from '@/types'
import { apiService } from '@/services/api'

// Props
const props = defineProps<{
  settings: ApplicationSettings | null
}>()

const toast = useToast()
const configStore = useConfigurationStore()
const loading = computed(() => configStore.isLoading || !props.settings)

// Helper function to format API errors
const formatApiError = (err: unknown): string => {
  if (err && typeof err === 'object' && 'message' in err) {
    return String((err as { message: string }).message)
  }
  return 'An unknown error occurred'
}

/* Triggers grid styles */

// State
const showWebhookForm = ref(false)
const editingWebhook = ref<{
  id: string
  name: string
  url: string
  type: 'Pushbullet' | 'Telegram' | 'Slack' | 'Discord' | 'Pushover' | 'NTFY' | 'Zapier'
  triggers: string[]
  isEnabled: boolean
} | null>(null)
const testingWebhook = ref<string | null>(null)
// Per-webhook ephemeral test results
const lastWebhookTestResults = reactive<Record<string, 'success' | 'fail' | undefined>>({})
const webhooks = ref<
  Array<{
    id: string
    name: string
    url: string
    type: 'Pushbullet' | 'Telegram' | 'Slack' | 'Discord' | 'Pushover' | 'NTFY' | 'Zapier'
    triggers: string[]
    isEnabled: boolean
  }>
>([])

const webhookForm = reactive({
  id: '',
  name: '',
  url: '',
  type: '' as 'Pushbullet' | 'Telegram' | 'Slack' | 'Discord' | 'Pushover' | 'NTFY' | 'Zapier' | '',
  triggers: [] as string[],
  isEnabled: true,
  telegramChatId: '',
  telegramBotToken: '',
  pushoverUserKey: '',
  pushoverApiToken: '',
  pushbulletAccessToken: '',
})

const webhookFormErrors = reactive({
  name: '',
  url: '',
  type: '',
  triggers: '',
})

const testingWebhookConfig = ref(false)
const savingWebhook = ref(false)

// Computed
const isWebhookFormValid = computed(() => {
  if (!webhookForm.name.trim() || webhookForm.type === '') return false
  if (webhookFormErrors.name || webhookFormErrors.url || webhookFormErrors.type) return false

  // Service-specific required fields
  if (webhookForm.type === 'Telegram') {
    return !!(webhookForm.telegramBotToken && webhookForm.telegramBotToken.trim().length > 0)
  }

  if (webhookForm.type === 'Pushover') {
    return !!(
      webhookForm.pushoverApiToken &&
      webhookForm.pushoverUserKey &&
      webhookForm.pushoverApiToken.trim().length > 0 &&
      webhookForm.pushoverUserKey.trim().length > 0
    )
  }

  if (webhookForm.type === 'Pushbullet') {
    return !!(
      webhookForm.pushbulletAccessToken && webhookForm.pushbulletAccessToken.trim().length > 0
    )
  }

  // Default: require URL
  return !!(webhookForm.url && webhookForm.url.trim().length > 0)
})

// Helper functions
function generateUUID(): string {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
    const r = (Math.random() * 16) | 0
    const v = c === 'x' ? r : (r & 0x3) | 0x8
    return v.toString(16)
  })
}

const getTriggerIcon = (trigger: string) => {
  const iconMap: Record<string, unknown> = {
    'book-added': PhPlus,
    'book-downloading': PhDownloadSimple,
    'book-available': PhCheckCircle,
    'book-completed': PhCircleWavyCheck,
  }
  return iconMap[trigger] || PhBell
}

const getWebhookTypeIcon = (type: string) => {
  const t = (type || '').toLowerCase()
  const map: Record<string, unknown> = {
    slack: PhSlackLogo,
    discord: PhDiscordLogo,
    telegram: PhTelegramLogo,
    pushover: PhBell,
    pushbullet: PhPushPinSimple,
    ntfy: PhBell,
    zapier: PhPaperPlaneTilt,
  }
  return map[t] || PhLink
}

const getTriggerClass = (trigger: string): string => {
  const classMap: Record<string, string> = {
    'book-added': 'trigger-added',
    'book-downloading': 'trigger-downloading',
    'book-available': 'trigger-available',
    'book-completed': 'trigger-completed',
  }
  return classMap[trigger] || ''
}

// Return triggers in a consistent display order
const orderedTriggerList = ['book-added', 'book-downloading', 'book-available', 'book-completed']

const orderedTriggers = (triggers: string[] | undefined) => {
  if (!triggers || triggers.length === 0) return []
  return orderedTriggerList.filter((t) => triggers.includes(t))
}

const formatTriggerName = (trigger: string): string => {
  const nameMap: Record<string, string> = {
    'book-added': 'Book Added',
    'book-downloading': 'Download Started',
    'book-available': 'Download Complete',
    'book-completed': 'Processing Complete',
  }
  return nameMap[trigger] || trigger
}

const isValidUrl = (url: string): boolean => {
  try {
    const urlObj = new URL(url)
    return urlObj.protocol === 'https:' || urlObj.protocol === 'http:'
  } catch {
    return false
  }
}

const validateWebhookField = (field: 'name' | 'url' | 'type' | 'triggers') => {
  switch (field) {
    case 'name':
      if (!webhookForm.name || webhookForm.name.trim().length === 0) {
        webhookFormErrors.name = 'Webhook name is required'
      } else if (webhookForm.name.trim().length < 3) {
        webhookFormErrors.name = 'Name must be at least 3 characters'
      } else {
        webhookFormErrors.name = ''
      }
      break
    case 'url':
      // Validation differs by service type
      if (webhookForm.type === 'Telegram') {
        if (!webhookForm.telegramBotToken || webhookForm.telegramBotToken.trim().length === 0) {
          webhookFormErrors.url = 'Bot token is required for Telegram'
        } else if (!/^[0-9]+:[A-Za-z0-9_-]+$/.test(webhookForm.telegramBotToken.trim())) {
          webhookFormErrors.url = 'Please enter a valid Telegram bot token (e.g. 123456:ABC...)'
        } else {
          webhookFormErrors.url = ''
        }
      } else if (webhookForm.type === 'Pushover') {
        if (!webhookForm.pushoverApiToken || !webhookForm.pushoverUserKey) {
          webhookFormErrors.url = 'Pushover API Token and User Key are required'
        } else {
          webhookFormErrors.url = ''
        }
      } else if (webhookForm.type === 'Pushbullet') {
        if (
          !webhookForm.pushbulletAccessToken ||
          webhookForm.pushbulletAccessToken.trim().length === 0
        ) {
          webhookFormErrors.url = 'Pushbullet Access Token is required'
        } else {
          webhookFormErrors.url = ''
        }
      } else {
        if (!webhookForm.url || webhookForm.url.trim().length === 0) {
          webhookFormErrors.url = 'Webhook URL is required'
        } else if (!isValidUrl(webhookForm.url)) {
          webhookFormErrors.url = 'Please enter a valid URL'
        } else {
          webhookFormErrors.url = ''
        }
      }
      break
    case 'type':
      if (!webhookForm.type) {
        webhookFormErrors.type = 'Please select a service type'
      } else {
        webhookFormErrors.type = ''
      }
      break
    case 'triggers':
      if (webhookForm.triggers.length === 0) {
        webhookFormErrors.triggers = 'Please select at least one trigger'
      } else {
        webhookFormErrors.triggers = ''
      }
      break
  }
}

const resetWebhookFormErrors = () => {
  webhookFormErrors.name = ''
  webhookFormErrors.url = ''
  webhookFormErrors.type = ''
  webhookFormErrors.triggers = ''
}

const onToggleTrigger = (trigger: string, enabled: boolean) => {
  const idx = webhookForm.triggers.indexOf(trigger)
  if (enabled && idx === -1) webhookForm.triggers.push(trigger)
  if (!enabled && idx !== -1) webhookForm.triggers.splice(idx, 1)
}

const onToggleTriggerValue = (trigger: string, value: boolean) => {
  onToggleTrigger(trigger, value)
}

const onServiceTypeChange = () => {
  validateWebhookField('type')
}

const getServiceHelp = (): string => {
  const helpText: Record<string, string> = {
    Slack:
      'Get your webhook URL from Slack: Settings & administration → Manage apps → Incoming Webhooks',
    Discord: 'Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL',
    Telegram:
      'Create a bot with @BotFather. Enter the bot token (e.g. 123456:ABC...) or the full webhook URL (https://api.telegram.org/bot{token}/sendMessage). Optionally provide a Chat ID below.',
    Pushover: 'Get your User Key and API Token from pushover.net/apps/build',
    Pushbullet: 'Get your Access Token from Settings → Account → Access Tokens',
    NTFY: 'Use format: https://ntfy.sh/{topic} or your self-hosted instance URL',
    Zapier: 'Create a Zap with "Webhooks by Zapier" and copy the webhook URL',
  }
  return webhookForm.type ? helpText[webhookForm.type] || '' : ''
}

// Webhook CRUD operations
const openWebhookForm = () => {
  editingWebhook.value = null
  webhookForm.id = ''
  webhookForm.name = ''
  webhookForm.url = ''
  webhookForm.type = ''
  webhookForm.triggers = []
  webhookForm.isEnabled = true
  webhookForm.telegramChatId = ''
  webhookForm.telegramBotToken = ''
  webhookForm.pushoverUserKey = ''
  webhookForm.pushoverApiToken = ''
  resetWebhookFormErrors()
  showWebhookForm.value = true
}

const closeWebhookForm = () => {
  showWebhookForm.value = false
  editingWebhook.value = null
  webhookForm.id = ''
  webhookForm.name = ''
  webhookForm.url = ''
  webhookForm.type = ''
  webhookForm.triggers = []
  webhookForm.isEnabled = true
  webhookForm.telegramChatId = ''
  resetWebhookFormErrors()
}

const editWebhook = (webhook: (typeof webhooks.value)[0]) => {
  editingWebhook.value = webhook
  webhookForm.id = webhook.id
  webhookForm.name = webhook.name
  webhookForm.url = webhook.url
  webhookForm.type = webhook.type
  webhookForm.triggers = [...webhook.triggers]
  webhookForm.isEnabled = webhook.isEnabled
  // If stored URL contains a Telegram chat_id query param, extract it for editing
  try {
    if (webhook.type === 'Telegram' && webhook.url) {
      try {
        const u = new URL(webhook.url)
        // path is like /bot<TOKEN>/sendMessage
        const segments = u.pathname.split('/')
        const botSegment = segments.find((s) => s.startsWith('bot')) || ''
        const token = botSegment.startsWith('bot') ? botSegment.substring(3) : ''
        const tokenVal = token || ''
        const params = u.searchParams
        const cid = params.get('chat_id')
        webhookForm.telegramChatId = cid || ''
        webhookForm.telegramBotToken = tokenVal
        // clear URL field for token-based editing
        webhookForm.url = ''
      } catch {
        webhookForm.telegramChatId = ''
        webhookForm.telegramBotToken = ''
      }
    } else if (webhook.type === 'Pushover' && webhook.url) {
      try {
        const u = new URL(webhook.url)
        const token = u.searchParams.get('token')
        const user = u.searchParams.get('user')
        webhookForm.pushoverApiToken = token || ''
        webhookForm.pushoverUserKey = user || ''
        // keep only base path in the url field
        webhookForm.url = u.origin + u.pathname
      } catch {
        webhookForm.pushoverApiToken = ''
        webhookForm.pushoverUserKey = ''
      }
    } else {
      webhookForm.telegramChatId = ''
    }

    // Extract Pushbullet access token if stored in query string
    try {
      if (webhook.type === 'Pushbullet' && webhook.url) {
        try {
          const u = new URL(webhook.url)
          const token = u.searchParams.get('token') || u.searchParams.get('access_token')
          webhookForm.pushbulletAccessToken = token || ''
          // keep only base path in the url field
          webhookForm.url = u.origin + u.pathname
        } catch {
          // fallback: support pushbullet://TOKEN format
          if (webhook.url.startsWith('pushbullet://')) {
            webhookForm.pushbulletAccessToken = webhook.url.substring('pushbullet://'.length)
            webhookForm.url = 'https://api.pushbullet.com/v2/pushes'
          }
        }
      }
    } catch {
      // ignore
    }
  } catch {
    webhookForm.telegramChatId = ''
  }
  resetWebhookFormErrors()
  showWebhookForm.value = true
}

const saveWebhook = async () => {
  // Validate all fields
  validateWebhookField('name')
  validateWebhookField('url')
  validateWebhookField('type')
  validateWebhookField('triggers')

  // Check if form is valid
  if (!isWebhookFormValid.value) {
    toast.error('Validation error', 'Please fix the errors before saving')
    return
  }

  savingWebhook.value = true
  try {
    // Compose final URL for Telegram when user provided token/chat id separately
    let finalUrl = webhookForm.url.trim()
    if (webhookForm.type === 'Telegram') {
      // Build from bot token field
      const token = webhookForm.telegramBotToken.trim()
      finalUrl = `https://api.telegram.org/bot${token}/sendMessage`
      if (webhookForm.telegramChatId && webhookForm.telegramChatId.trim() !== '') {
        try {
          const u = new URL(finalUrl)
          u.searchParams.set('chat_id', webhookForm.telegramChatId.trim())
          finalUrl = u.toString()
        } catch {
          const sep = finalUrl.includes('?') ? '&' : '?'
          finalUrl = `${finalUrl}${sep}chat_id=${encodeURIComponent(webhookForm.telegramChatId.trim())}`
        }
      }
    }

    // Compose final URL for Pushbullet when user provided access token
    if (webhookForm.type === 'Pushbullet') {
      if (webhookForm.pushbulletAccessToken && webhookForm.pushbulletAccessToken.trim() !== '') {
        finalUrl = `https://api.pushbullet.com/v2/pushes?token=${encodeURIComponent(webhookForm.pushbulletAccessToken.trim())}`
      }
    }

    // Compose final URL for Pushover when user provided token/user separately
    if (webhookForm.type === 'Pushover') {
      if (webhookForm.pushoverApiToken && webhookForm.pushoverUserKey) {
        finalUrl = `https://api.pushover.net/1/messages.json?token=${encodeURIComponent(
          webhookForm.pushoverApiToken.trim(),
        )}&user=${encodeURIComponent(webhookForm.pushoverUserKey.trim())}`
      }
    }

    const webhook = {
      id: webhookForm.id || generateUUID(),
      name: webhookForm.name.trim(),
      url: finalUrl,
      type: webhookForm.type as
        | 'Pushbullet'
        | 'Telegram'
        | 'Slack'
        | 'Discord'
        | 'Pushover'
        | 'NTFY'
        | 'Zapier',
      triggers: [...webhookForm.triggers],
      isEnabled: webhookForm.isEnabled,
    }

    if (editingWebhook.value) {
      // Update existing webhook
      const index = webhooks.value.findIndex((w) => w.id === webhook.id)
      if (index !== -1) {
        webhooks.value[index] = webhook
      }
      toast.success('Webhook', 'Webhook updated successfully')
    } else {
      // Add new webhook
      webhooks.value.push(webhook)
      toast.success('Webhook', 'Webhook added successfully')
    }

    // Persist webhooks to settings
    await persistWebhooks()

    closeWebhookForm()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'NotificationsTab',
      operation: 'saveWebhook',
    })
    toast.error('Save failed', 'Failed to save webhook')
  } finally {
    savingWebhook.value = false
  }
}

const webhookToDelete = ref<(typeof webhooks.value)[0] | null>(null)

const confirmDeleteWebhook = (webhook: (typeof webhooks.value)[0]) => {
  webhookToDelete.value = webhook
}

const executeDeleteWebhook = async () => {
  if (!webhookToDelete.value) return
  try {
    webhooks.value = webhooks.value.filter((w) => w.id !== webhookToDelete.value!.id)
    toast.success('Webhook', 'Webhook deleted successfully')
    await persistWebhooks()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'NotificationsTab',
      operation: 'executeDeleteWebhook',
    })
    toast.error('Delete failed', 'Failed to delete webhook')
    throw error
  } finally {
    webhookToDelete.value = null
  }
}

const toggleWebhook = async (webhook: (typeof webhooks.value)[0]) => {
  const index = webhooks.value.findIndex((w) => w.id === webhook.id)
  if (index !== -1) {
    const targetWebhook = webhooks.value[index]
    if (targetWebhook) {
      targetWebhook.isEnabled = !targetWebhook.isEnabled
      toast.success(
        'Webhook',
        `${webhook.name} ${targetWebhook.isEnabled ? 'enabled' : 'disabled'}`,
      )

      // Persist webhooks to settings
      await persistWebhooks()
    }
  }
}

const testWebhook = async (webhook: (typeof webhooks.value)[0]) => {
  testingWebhook.value = webhook.id
  try {
    const payload = {
      trigger: 'book-available',
      data: { message: 'Test notification from Listenarr UI' },
    }
    const response = await apiService.testNotification(
      payload.trigger,
      payload.data,
      webhook.id,
      webhook.url,
    )
    if (response && response.success) {
      toast.success(
        'Test notification',
        response.message || `Test notification sent to ${webhook.name}`,
      )
      lastWebhookTestResults[webhook.id] = 'success'
    } else {
      toast.error('Test failed', response?.message || 'Failed to send test notification')
      lastWebhookTestResults[webhook.id] = 'fail'
    }
    console.debug(
      'NotificationsTab: lastWebhookTestResults set',
      webhook.id,
      lastWebhookTestResults[webhook.id],
    )
    await nextTick()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'NotificationsTab',
      operation: 'testWebhook',
    })
    const errorMessage = formatApiError(error)
    toast.error('Test failed', errorMessage)
    lastWebhookTestResults[webhook.id] = 'fail'
    console.debug(
      'NotificationsTab: lastWebhookTestResults set',
      webhook.id,
      lastWebhookTestResults[webhook.id],
    )
    await nextTick()
  } finally {
    testingWebhook.value = null
  }
}

const testWebhookConfig = async () => {
  testingWebhookConfig.value = true
  try {
    const payload = {
      trigger: 'book-available',
      data: { message: 'Test notification from Listenarr UI' },
    }
    const response = await apiService.testNotification(payload.trigger, payload.data)
    if (response && response.success) {
      toast.success(
        'Test successful',
        response.message || `Test notification sent to ${webhookForm.type}`,
      )
    } else {
      toast.error('Test failed', response?.message || 'Failed to send test notification')
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'NotificationsTab',
      operation: 'testWebhookConfig',
    })
    const errorMessage = formatApiError(error)
    toast.error('Test failed', errorMessage)
  } finally {
    testingWebhookConfig.value = false
  }
}

// Persist webhooks to backend settings (do not mutate incoming props)
const persistWebhooks = async () => {
  // Create a shallow copy of settings and assign updated webhooks
  const current = props.settings
    ? { ...(props.settings as unknown as Record<string, unknown>) }
    : {}
  try {
    const payload: ApplicationSettings = {
      ...(current as unknown as ApplicationSettings),
      webhooks: webhooks.value,
    }
    // Save to backend using the configuration store
    await configStore.saveApplicationSettings(payload)
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'NotificationsTab',
      operation: 'persistWebhooks',
    })
    toast.error('Save failed', 'Failed to save webhooks to settings')
    throw error
  }
}

// Initialize webhooks from settings
onMounted(() => {
  if (props.settings?.webhooks) {
    webhooks.value = props.settings.webhooks
  }
})

// Expose openWebhookForm for parent component
defineExpose({ openWebhookForm })
</script>

<style scoped>
.tab-content {
  animation: fadeIn 0.2s ease;
}

/* @keyframes fadeIn is centralized in src/assets/animations.css */

/* Section Header */
.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.section-header .small-inline-spinner {
  margin-left: 0.5rem;
  width: 18px;
  height: 18px;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */
/* Empty State */
.empty-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #868e96;
}

.empty-icon {
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

.empty-help {
  font-size: 0.95rem;
  color: #868e96;
  margin-bottom: 2rem;
}

.add-button {
  padding: 0.75rem 1.5rem;
  background: #1e88e5;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  font-size: 0.95rem;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.add-button:hover {
  background: var(--brand-600);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.4);
}

.add-button-large {
  margin-top: 1.5rem;
  padding: 1rem 2rem;
  background: var(--brand-600);
  color: white;
  border: none;
  border-radius: var(--btn-radius);
  cursor: pointer;
  transition: all 0.2s ease;
  display: inline-flex;
  align-items: center;
  gap: 0.75rem;
  font-weight: 500;
  font-size: 1rem;
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.3);
}

.add-button-large:hover {
  background: var(--brand-700);
  transform: translateY(-2px);
  box-shadow: 0 6px 16px rgba(var(--brand-rgb), 0.4);
}

/* Form styles */
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

.form-group {
  margin-bottom: 1.5rem;
}

.form-group:last-child {
  margin-bottom: 0;
}

.form-group label {
  display: block;
  margin-bottom: 0.5rem;
  color: #fff;
  font-weight: 500;
}

.form-group input,
.form-group select {
  width: 100%;
  padding: 0.75rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: border-color 0.2s;
}

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: var(--brand-focus);
}

.form-group small {
  display: block;
  margin-top: 0.25rem;
  color: #b3b3b3;
  font-size: 0.85rem;
}

.error-text {
  color: #ff6b6b;
  font-size: 0.85rem;
  margin-top: 0.25rem;
  display: block;
}

/* Base checkbox-group styles are provided globally via `src/styles/global.css`.
   Keep per-view small overrides below. */

/* Notifications-specific overrides */
.checkbox-group label:hover {
  border-color: var(--brand-500);
}
.checkbox-group label span {
  flex: 1;
}
.checkbox-group label small {
  color: #b3b3b3;
}
.checkbox-group input[type='checkbox']:focus-visible {
  outline: 2px solid rgba(var(--brand-rgb), 0.24);
}
/* Modal footer styling moved to shared `modals.css` */

/* Modal actions styling moved to shared `modals.css` */

/* Webhooks Grid */
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
}

.webhook-card:hover {
  border-color: rgba(var(--brand-rgb), 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.15);
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
}

.webhook-title-row {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex: 1;
  min-width: 0;
}

.webhook-info {
  min-width: 0;
}

.webhook-info h4 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: flex;
  align-items: initial;
}

.webhook-meta {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
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

.trigger-badge-small.trigger-completed {
  background-color: rgba(255, 255, 255, 0.02);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.12);
}

.webhook-title {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0;
  line-height: 1;
}

.webhook-type-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.03);
  color: var(--color-text-secondary);
  font-size: 1rem;
  flex-shrink: 0;
}

.webhook-name {
  display: inline-block;
  line-height: 1;
}

@media (max-width: 768px) {
  .webhook-title {
    width: 100%;
  }
}

.webhook-header-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: 1rem;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */

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
}

.url-icon {
  color: #4dabf7;
  font-size: 1.1rem;
}

.webhook-url {
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 0.85rem;
  color: #adb5bd;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Triggers grid styles */
.webhook-triggers {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 0.75rem;
}

.webhook-triggers .checkbox-group {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.04);
  padding: 0.6rem 0.75rem;
  border-radius: 8px;
  margin: 0;
}

.webhook-triggers .input-checkbox {
  gap: 0.75rem;
  align-items: center;
}

/* Layout the label contents with icon on the left and stacked text */
.webhook-triggers .checkbox-label {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 0.75rem;
}

.webhook-triggers .checkbox-text {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 0.5rem;
  line-height: 1;
}

.webhook-triggers .trigger-icon {
  color: var(--color-text-secondary);
  font-size: 1.05rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.webhook-triggers .checkbox-text small {
  font-size: 0.82rem;
  color: var(--color-text-secondary);
  margin: 0 0 0 0.25rem;
}

/* Mobile Responsive */
@media (max-width: 768px) {
  .webhooks-grid {
    grid-template-columns: 1fr;
  }

  .webhook-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .webhook-header-actions {
    width: 100%;
    justify-content: space-between;
    margin-left: 0;
  }
}

/* Override to ensure the checkbox uses our custom component styling inside teleported modal */
.webhook-modal .input-checkbox {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  cursor: pointer !important;
}
.webhook-modal .input-checkbox input {
  position: absolute;
  opacity: 0;
  width: 0;
  height: 0;
}
.webhook-modal .checkbox-box {
  width: 18px;
  height: 18px;
  border-radius: 3px;
  margin-top: 0;
  flex-shrink: 0;
}
.webhook-modal .checkbox-label > :first-child {
  height: 18px;
  display: flex;
  align-items: center;
}

/* Spin animation for loading icons */
.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */
</style>
