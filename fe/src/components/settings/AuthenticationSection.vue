<template>
  <div class="form-section">
    <h3>
      <PhUserCircle /> Authentication
    </h3>
    <div class="form-body">
      <div class="form-group checkbox-group">
        <CheckboxCard v-model="authEnabledComputed" title="Enable login screen" description="Toggle to enable the login screen. This setting reflects the server's AuthenticationRequired value from config.json. Changes here are local and will not modify server files — edit config/config.json on the host to persist." />
      </div>

      <div class="form-group checkbox-group">
        <CheckboxCard
          v-model="hideNoAuthSecurityBanner"
          title="Hide no-auth security warning banner"
          description="Permanently hide the top warning banner on this browser when authentication is disabled. This applies immediately and does not require Save."
        />
      </div>

      <FormRow v-if="authEnabledComputed" label="Admin Account Management" help="To set or change the admin password, enter a new password and save settings. The username and password are configured in config/config.json.">
        <div class="admin-credentials">
          <input :value="settings.adminUsername" @input="e => updateField('adminUsername', (e.target as HTMLInputElement).value)" type="text" placeholder="Admin username" class="admin-input" />
          <PasswordInput :modelValue="settings.adminPassword" @update:modelValue="v => updateField('adminPassword', v)" placeholder="New admin password (to update)" />
        </div>
      </FormRow>

      <FormRow label="API Key (Server)" help="API key for authenticating external applications. Generate a new key if needed. Copy it to use with API clients.">
        <ApiKeyControl :apiKey="startupConfig?.apiKey" :disabled="false" @update:apiKey="onApiKeyUpdated" />
      </FormRow>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { ApplicationSettings, StartupConfig } from '@/types'
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
  startupConfig?: StartupConfig | null
  authEnabled: boolean
}>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
  'update:authEnabled': [value: boolean]
  'update:startupConfig': [value: StartupConfig]
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
  emit('update:startupConfig', { ...(props.startupConfig || {}), apiKey: newKey } as StartupConfig)
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

.form-body { padding: 1.25rem; border-radius: 6px; border: 1px solid #333; box-shadow: 0 4px 14px rgba(0,0,0,0.6); background-color: #232323; }
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
