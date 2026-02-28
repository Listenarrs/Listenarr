<template>
  <Modal :visible="visible" size="sm" :title="title" @close="$emit('close')">
    <template #header>
      <ModalHeader :title="title" @close="$emit('close')">
        <template #icon><slot name="icon"><PhWarningCircle /></slot></template>
      </ModalHeader>
    </template>

    <ModalBody>
      <slot>
        <p>Are you sure?</p>
      </slot>
    </ModalBody>

    <template #footer>
      <button @click="$emit('close')" class="cancel-button btn">Cancel</button>
      <button @click="$emit('confirm')" class="delete-button modal-delete-button"> <slot name="confirm-icon"><PhTrash /></slot> {{ confirmText }}</button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { PhWarningCircle, PhTrash, PhX } from '@phosphor-icons/vue' 
const props = defineProps({
  visible: { type: Boolean, required: true },
  title: { type: String, default: 'Delete' },
  confirmText: { type: String, default: 'Delete' },
})
const emit = defineEmits(['close', 'confirm'])
</script>

<style scoped>
/* small tweaks; main modal button styles live in the centralized stylesheet */
.modal-body p { margin: 0 0 1rem 0 }
</style>