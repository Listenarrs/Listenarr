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
  <div class="form-group checkbox-group">
    <Checkbox
      :modelValue="modelValue"
      :disabled="disabled"
      @update:modelValue="$emit('update:modelValue', $event)"
    >
      <span>
        <slot />
        <span class="checkbox-text">
          <strong>{{ title }}</strong>
          <small v-if="description">{{ description }}</small>
        </span>
      </span>
    </Checkbox>
  </div>
</template>

<script setup lang="ts">
import Checkbox from '@/components/form/Checkbox.vue'

defineProps<{ modelValue?: boolean; title: string; description?: string; disabled?: boolean }>()
</script>

<style scoped>
/* Reuse .checkbox-group styles from global.css; no extra styles needed here */
/* Layout override so icon slot and text appear on one row within this card */
.input-checkbox {
  align-items: center;
}
.input-checkbox .checkbox-label {
  align-items: center;
}
.input-checkbox .checkbox-label > span {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 0.6rem;
}
.input-checkbox .checkbox-label .checkbox-text {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 0.5rem;
}
.input-checkbox .checkbox-label .checkbox-text small {
  margin-left: 0.25rem;
}
</style>
