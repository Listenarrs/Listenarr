<template>
  <div class="form-section">
    <h3>
      <PhDownload /> Download Settings
    </h3>
    <div class="form-body">
      <FormRow label="Max Concurrent Downloads" help="Maximum number of simultaneous downloads (1-10)">
        <input :value="settings.maxConcurrentDownloads" @input="e => updateField('maxConcurrentDownloads', Number((e.target as HTMLInputElement).value || 1))" type="number" min="1" max="10" />
      </FormRow>

      <FormRow label="Polling Interval (seconds)" help="How often to check download status (10-300 seconds)">
        <input :value="settings.pollingIntervalSeconds" @input="e => updateField('pollingIntervalSeconds', Number((e.target as HTMLInputElement).value || 10))" type="number" min="10" max="300" />
      </FormRow>

      <FormRow label="Download Completion Stability (seconds)" help="How long (seconds) a download must be seen as complete on the client before finalization begins. Increase for clients that post-process/extract after completion.">
        <input :value="settings.downloadCompletionStabilitySeconds" @input="e => updateField('downloadCompletionStabilitySeconds', Number((e.target as HTMLInputElement).value || 1))" type="number" min="1" max="600" />
      </FormRow>

      <FormRow label="Missing-source Retry Initial Delay (seconds)" help="Initial retry delay (seconds) used when files are not yet available at finalization time.">
        <input :value="settings.missingSourceRetryInitialDelaySeconds" @input="e => updateField('missingSourceRetryInitialDelaySeconds', Number((e.target as HTMLInputElement).value || 1))" type="number" min="1" max="600" />
      </FormRow>

      <FormRow label="Missing-source Max Retries" help="Maximum number of retries to attempt if the finalized download's source files are missing.">
        <input :value="settings.missingSourceMaxRetries" @input="e => updateField('missingSourceMaxRetries', Number((e.target as HTMLInputElement).value || 0))" type="number" min="0" max="20" />
      </FormRow>

      <CheckboxCard
        :modelValue="settings.failedDownloadHandlingEnabled"
        @update:modelValue="v => updateField('failedDownloadHandlingEnabled', v)"
        title="Enable Failed Download Handling"
        description="When a download fails, mark it as failed, remove it from the client, and record history."
      />

      <CheckboxCard
        :modelValue="settings.failedDownloadAutoSearch"
        @update:modelValue="v => updateField('failedDownloadAutoSearch', v)"
        :disabled="!settings.failedDownloadHandlingEnabled"
        title="Auto-search on Failed Downloads"
        description="Automatically search for a replacement when a download fails (requires failed download handling)."
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { PhDownload } from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
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

/* Modal-like card and local form styles for Download Settings */
.form-body { padding: 1.25rem; border-radius: 6px; border: 1px solid #333; box-shadow: 0 4px 14px rgba(0,0,0,0.6); background-color: #232323; }

.form-row-label { margin-bottom: 0.5rem; font-weight: 500; color: #fff }

.form-group input[type='number'], .form-row-control input[type='number'] { width: 100%; padding: 0.9rem 0.85rem; border: 1px solid #444; border-radius: 6px; background-color: #1a1a1a; color: #fff; font-size: 0.95rem }

.form-group input:focus, .form-row-control input:focus { outline: none; border-color: var(--brand-500); box-shadow: 0 0 0 3px rgba(77,171,247,0.08); }
.form-group input::placeholder, .form-row-control input::placeholder { color: #6c757d }

.form-help { display:block; margin-top:0.5rem; font-size:0.85rem; color:#adb5bd; line-height:1.5 }
</style>