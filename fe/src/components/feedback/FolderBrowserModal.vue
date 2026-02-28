<template>
  <Modal :visible="visible" @close="onClose" size="lg">
    <template #header>
      <ModalHeader :title="'Browse Folders'" :icon="PhFolder" @close="onClose" />
    </template>

    <ModalBody>
      <div class="section-card section-card--no-top">
        <FolderBrowser
          inline
          :use-inner-card="false"
          :show-header="false"
          :auto-select="false"
          :modelValue="modelValue"
          :show-input="showInput"
          :show-files="showFiles"
          :open="visible"
          @update:modelValue="onUpdatePath"
          @update:open="onInternalOpenUpdate"
          @browser-opened="onBrowserOpened"
          @browser-closed="onBrowserClosed"
          @path-draft="onPathDraft"
        />
      </div>
    </ModalBody>

    <template #footer>
      <ModalFooter
        :showCancel="true"
        :showSave="true"
        :cancelLabel="cancelLabel"
        :saveLabel="confirmLabel"
        @cancel="onClose"
        @save="onSelect"
      />
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { watch, ref } from 'vue'
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback'
import FolderBrowser from '@/components/ui/FolderBrowser.vue'
import { PhFolder } from '@phosphor-icons/vue'

const props = defineProps({
  visible: { type: Boolean, required: true },
  modelValue: { type: String as () => string | undefined, required: false },
  showInput: { type: Boolean, default: true },
  showFiles: { type: Boolean, default: false },
  confirmLabel: { type: String, default: 'Select' },
  cancelLabel: { type: String, default: 'Cancel' },
})
const emit = defineEmits(['update:visible', 'close', 'update:modelValue'])

const selectedDraft = ref<string | null>(props.modelValue ?? null)

function onClose() {
  // restore body scroll when explicitly closed
  document.body.style.overflow = ''
  emit('update:visible', false)
  emit('close')
}

function onUpdatePath(v: string | null) {
  emit('update:modelValue', v)
  // When the user selects a path from the browser list, close the modal and restore body scroll
  onClose()
}

function onPathDraft(v: string) {
  selectedDraft.value = v || null
}

function onSelect() {
  // Confirm the currently selected draft (or the prop value) and close
  const pathToEmit = selectedDraft.value ?? props.modelValue ?? null
  emit('update:modelValue', pathToEmit)
  onClose()
}

function onBrowserOpened() {
  // lock body scroll
  document.body.style.overflow = 'hidden'
}
function onBrowserClosed() {
  // restore
  document.body.style.overflow = ''
  emit('update:visible', false)
  emit('close')
}

// If the parent controls visible, keep body lock in sync
watch(
  () => props.visible,
  (v) => {
    if (!v) document.body.style.overflow = ''
  },
)

function onInternalOpenUpdate(v: boolean) {
  // Sync internal open back to modal visibility
  if (!v) onBrowserClosed()
}
</script>
