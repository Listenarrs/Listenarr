<template>
  <Modal class="confirm-dialog" :visible="modelValue" :title="title || 'Confirm'" size="sm" @close="onCancel">
    <div class="confirm-body">
      <p>{{ stripHtmlAndNormalize(message) }}</p>
    </div>

    <template #footer>
      <button class="btn cancel" @click="onCancel">{{ cancelText }}</button>
      <button class="btn confirm" :class="{ danger }" @click="onConfirm">{{ confirmText }}</button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { Modal } from '@/components/feedback'
import { stripHtmlAndNormalize } from '@/utils/textUtils'

const props = defineProps({
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
.confirm-body p { color: #ddd; margin: 0 0 0.5rem 0; white-space: pre-wrap }
</style>
