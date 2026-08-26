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
    <h3><PhGlobe /> Host</h3>
    <div class="form-body">
      <FormRow
        label="URL Base"
        labelFor="urlBase"
        help="For reverse proxy support, default is empty. Requires a restart to take effect."
      >
        <input
          id="urlBase"
          class="url-base-input"
          :value="urlBase"
          type="text"
          placeholder="/listenarr"
          spellcheck="false"
          autocomplete="off"
          @change="(e) => updateUrlBase((e.target as HTMLInputElement).value)"
        />
        <span v-if="urlBaseError" class="form-error" role="alert">{{ urlBaseError }}</span>
      </FormRow>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { StartupConfig } from '@/types'
import { PhGlobe } from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'

const props = defineProps<{ startupConfig: StartupConfig | null | undefined }>()
const emit = defineEmits<{ 'update:startupConfig': [value: StartupConfig] }>()

const urlBase = computed(() => props.startupConfig?.urlBase ?? '')

/**
 * The server treats an absolute URL as unusable and serves at the site root, so
 * saying so here is friendlier than saving a value that silently does nothing.
 * This mirrors the *arr projects, whose UrlBase validator rejects anything
 * beginning with a scheme.
 */
const urlBaseError = computed(() => {
  const value = urlBase.value.trim()
  if (!value) return ''
  if (/^\/?https?:\/\//i.test(value)) {
    return "Must be a path, not a full URL (for example '/listenarr')."
  }
  return ''
})

function updateUrlBase(value: string) {
  emit('update:startupConfig', { ...(props.startupConfig || {}), urlBase: value.trim() })
}
</script>

<style scoped>
/*
  Scoped styles do not cascade between sibling sections, so each settings section
  restates the heading and card rules. These match FileManagementSection, which
  renders directly below this one on the General tab.
*/
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0.5rem 0;
  font-size: 1.2rem;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 0.625rem;
  color: #fff;
  letter-spacing: 0.01em;
}

h3 svg {
  color: var(--brand-500);
  filter: drop-shadow(0 0 8px rgba(33, 150, 243, 0.3));
}

.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.url-base-input {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.12s;
}

.url-base-input::placeholder {
  color: #6c757d;
}

.url-base-input:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.form-error {
  display: block;
  margin-top: 0.5rem;
  color: var(--color-danger, #d9534f);
  font-size: 0.85rem;
}
</style>
