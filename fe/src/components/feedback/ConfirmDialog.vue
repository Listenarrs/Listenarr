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
  <Modal
    class="confirm-dialog"
    :visible="modelValue"
    :title="title || 'Confirm'"
    size="sm"
    @close="onCancel"
  >
    <div class="confirm-body">
      <p>{{ stripHtmlAndNormalize(message) }}</p>
    </div>

    <template #footer>
      <button class="btn cancel" @click="onCancel">{{ cancelText }}</button>
      <button class="btn confirm" :class="danger ? 'btn-danger' : 'btn-info'" @click="onConfirm">
        {{ confirmText }}
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { Modal } from '@/components/feedback'
import { stripHtmlAndNormalize } from '@/utils/textUtils'

defineProps({
  modelValue: { type: Boolean, required: true },
  title: { type: String, default: '' },
  message: { type: String, default: '' },
  confirmText: { type: String, default: 'Confirm' },
  cancelText: { type: String, default: 'Cancel' },
  danger: { type: Boolean, default: false },
})

const emit = defineEmits(['update:modelValue', 'confirm'])

function onConfirm() {
  emit('confirm')
  emit('update:modelValue', false)
}

function onCancel() {
  emit('update:modelValue', false)
}
</script>

<style scoped>
/* Rely on centralized modal styles for layout; keep tiny confirm-specific tweaks here */
.confirm-body p {
  color: #ddd;
  margin: 0 0 0.5rem 0;
  white-space: pre-wrap;
}
</style>

<style>
/* A confirmation asked from inside another dialog shows on top of it: both overlays are on body at
   the same level, and this one is mounted first (App.vue), so it would come under the other */
.modal-overlay:has(> .confirm-dialog) {
  z-index: 3001;
}
</style>
