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
  <div class="form-section">
    <h3><PhUserCircle /> Authentication</h3>
    <div class="form-body">
      <div class="form-group checkbox-group">
        <CheckboxCard
          v-model="authEnabledComputed"
          title="Enable login screen"
          description="Toggle the login screen on or off. This setting is persisted to the server's AuthenticationRequired value in config.json when you save. When enabled, the admin credentials below are required for sign-in — set or update them in the same save."
        />
      </div>

      <div class="form-group checkbox-group">
        <CheckboxCard
          v-model="hideNoAuthSecurityBanner"
          title="Hide no-auth security warning banner"
          description="Permanently hide the top warning banner on this browser when authentication is disabled. This applies immediately and does not require Save."
        />
      </div>

      <FormRow
        label="Admin Account Management"
        help="Set or change the admin username and password used for sign-in. Available whether or not the login screen is currently enabled so you can configure credentials before turning auth on. Leave the password blank to keep the existing one; provide both fields to create or update the admin user when you save."
      >
        <div class="admin-credentials">
          <input
            :value="settings.adminUsername"
            @input="(e) => updateField('adminUsername', (e.target as HTMLInputElement).value)"
            type="text"
            placeholder="Admin username"
            class="admin-input"
          />
          <PasswordInput
            :modelValue="settings.adminPassword"
            @update:modelValue="(v) => updateField('adminPassword', v)"
            placeholder="Admin password (leave blank to keep existing)"
          />
        </div>
      </FormRow>

      <FormRow
        label="API Key (Server)"
        help="API key for authenticating external applications. Generate a new key if needed. Copy it to use with API clients."
      >
        <ApiKeyControl :apiKey="apiKey" :disabled="false" @update:apiKey="onApiKeyUpdated" />
      </FormRow>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { ApplicationSettings } from '@/types'
import { PhUserCircle } from '@phosphor-icons/vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import PasswordInput from '@/components/form/PasswordInput.vue'
import ApiKeyControl from '@/components/form/ApiKeyControl.vue'
import FormRow from '@/components/settings/FormRow.vue'
import {
  getSecurityWarningBannerHiddenPreference,
  setSecurityWarningBannerHiddenPreference,
} from '@/utils/securityWarningBannerPreference'

const props = defineProps<{
  settings: Partial<ApplicationSettings>
  apiKey?: string
  authEnabled: boolean
}>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
  'update:authEnabled': [value: boolean]
  'update:apiKey': [value: string]
}>()

const authEnabledComputed = computed({
  get: () => props.authEnabled,
  set: (v: boolean) => emit('update:authEnabled', v),
})

const hideNoAuthSecurityBanner = ref(false)

onMounted(() => {
  hideNoAuthSecurityBanner.value = getSecurityWarningBannerHiddenPreference()
})

watch(hideNoAuthSecurityBanner, (value) => {
  setSecurityWarningBannerHiddenPreference(value)
})

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function onApiKeyUpdated(newKey: string) {
  emit('update:apiKey', newKey)
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}
.admin-credentials {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.admin-input {
  width: 100%;
  padding: 0.75rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
}

:deep(.password-input) {
  width: 100%;
}
</style>
