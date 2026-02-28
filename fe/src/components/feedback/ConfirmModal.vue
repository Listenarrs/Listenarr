<template>
  <Modal :visible="visible" :size="size" @close="onCancel">
    <template #header>
      <ModalHeader :title="title" @close="onCancel" :icon="icon" :iconLabel="iconLabel" />
    </template>

    <template #default>
      <ModalBody>
        <div class="confirm-message">
          <slot>{{ message }}</slot>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter
        :showCancel="true"
        :showSave="true"
        :saving="confirming"
        :cancelLabel="cancelLabel"
        :saveLabel="confirmLabel"
        @cancel="onCancel"
        @save="onConfirm"
      />
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback'
import type { Component } from 'vue'

withDefaults(
  defineProps<{
    visible?: boolean
    title?: string
    message?: string
    confirmLabel?: string
    cancelLabel?: string
    size?: 'sm' | 'md' | 'lg'
    confirming?: boolean
    icon?: Component | undefined
    iconLabel?: string | undefined
  }>(),
  { visible: false, title: 'Confirm', message: '', confirmLabel: 'Confirm', cancelLabel: 'Cancel', size: 'sm', confirming: false },
)

const emit = defineEmits(['confirm','cancel'])

function onConfirm() { emit('confirm') }
function onCancel() { emit('cancel') }
</script>

<style scoped>
.confirm-message { color:#ddd; }
</style>