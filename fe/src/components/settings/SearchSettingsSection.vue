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
    <h3><PhMagnifyingGlass /> Search Settings</h3>
    <div class="form-body">
      <div class="form-row">
        <div class="form-group">
          <label for="default-search-region">Preferred Default Region</label>
          <select
            id="default-search-region"
            :value="defaultSearchRegion"
            class="form-select"
            @change="updateDefaultSearchRegion"
          >
            <option v-for="option in searchRegionOptions" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
          <small class="form-help">
            Used as the default market in Add New searches. You can still change it per search.
          </small>
        </div>

        <div class="form-group">
          <label for="default-search-language">Preferred Default Language</label>
          <select
            id="default-search-language"
            :value="defaultSearchLanguage"
            class="form-select"
            @change="updateDefaultSearchLanguage"
          >
            <option
              v-for="option in preferredSearchLanguageOptions"
              :key="option.value"
              :value="option.value"
            >
              {{ option.label }}
            </option>
          </select>
          <small class="form-help">
            Used as the default language filter for Add New searches. Choose All to disable language
            filtering.
          </small>
        </div>
      </div>

      <CheckboxCard
        :modelValue="settings.enableOpenLibrarySearch"
        @update:modelValue="updateEnableOpenLibrarySearch"
        title="Enable OpenLibrary Searching"
        description="Include OpenLibrary title augmentation and lookups when performing intelligent searches."
      />

      <CheckboxCard
        :modelValue="settings.enableIndexerDomainRotation ?? true"
        @update:modelValue="updateEnableIndexerDomainRotation"
        title="Enable Indexer Domain Rotation"
        description="Automatically switch domain-rotating indexers (Anna's Archive, AudioBookBay) to a working mirror when the current domain becomes unreachable. Checked every 6 hours."
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ApplicationSettings } from '@/types'
import { PhMagnifyingGlass } from '@phosphor-icons/vue'
// Checkbox usage handled via CheckboxCard; no direct Checkbox import needed here
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import {
  normalizePreferredSearchLanguage,
  normalizeSearchRegion,
  preferredSearchLanguageOptions,
  searchRegionOptions,
} from '@/utils/languageMapping'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function updateEnableOpenLibrarySearch(value: boolean) {
  updateField('enableOpenLibrarySearch', value)
}

function updateEnableIndexerDomainRotation(value: boolean) {
  updateField('enableIndexerDomainRotation', value)
}

const defaultSearchRegion = computed(() =>
  normalizeSearchRegion(props.settings.defaultSearchRegion),
)

const defaultSearchLanguage = computed(() =>
  normalizePreferredSearchLanguage(props.settings.defaultSearchLanguage),
)

function updateDefaultSearchRegion(event: Event) {
  updateField('defaultSearchRegion', (event.target as HTMLSelectElement).value)
}

function updateDefaultSearchLanguage(event: Event) {
  updateField('defaultSearchLanguage', (event.target as HTMLSelectElement).value)
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

/* Modal-like search settings */
.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
  gap: 1.5rem;
}

.form-group label {
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}

.form-group input[type='number'] {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
}

.form-group select,
.form-group input[type='number'] {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
}

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.08);
}
.form-group input::placeholder {
  color: #6c757d;
}

.form-help {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #adb5bd;
  line-height: 1.5;
}
</style>
