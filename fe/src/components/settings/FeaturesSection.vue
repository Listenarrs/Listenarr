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
    <h3><PhToggleLeft /> Features</h3>
    <div class="form-body">
      <CheckboxCard
        :modelValue="settings.enableMetadataProcessing"
        @update:modelValue="updateEnableMetadataProcessing"
        title="Enable Metadata Processing"
        description="Automatically fetch and embed audiobook metadata"
      />

      <CheckboxCard
        :modelValue="settings.embedCoverArtInAudioFiles"
        @update:modelValue="updateEmbedCoverArtInAudioFiles"
        title="Embed Cover Art In Audio Files"
        description="Write cover art into audiobook files as they are imported. Rewrites the file, so it is off by default."
      />

      <CheckboxCard
        :modelValue="settings.enableNotifications"
        @update:modelValue="updateEnableNotifications"
        title="Enable Notifications"
        description="Receive notifications for downloads and events"
      />

      <CheckboxCard
        :modelValue="settings.showCompletedExternalDownloads"
        @update:modelValue="updateShowCompletedExternalDownloads"
        title="Show completed external downloads in Activity"
        description="When enabled, completed torrents/NZBs from external clients will remain visible in the Activity view. When disabled, completed external items will be hidden to reduce clutter."
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { PhToggleLeft } from '@phosphor-icons/vue'
// Checkbox controls are provided via `CheckboxCard` wrapper where used
import CheckboxCard from '@/components/settings/CheckboxCard.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function updateEnableMetadataProcessing(value: boolean) {
  updateField('enableMetadataProcessing', value)
}

function updateEmbedCoverArtInAudioFiles(value: boolean) {
  updateField('embedCoverArtInAudioFiles', value)
}

function updateEnableNotifications(value: boolean) {
  updateField('enableNotifications', value)
}

function updateShowCompletedExternalDownloads(value: boolean) {
  updateField('showCompletedExternalDownloads', value)
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

/* Modal-like Features section */
.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.form-group {
  margin-bottom: 1.25rem;
}
.form-group label {
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}
.form-help {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #adb5bd;
}
</style>
