<template>
  <Modal :visible="true" size="md" @close="close">
    <template #header>
      <ModalHeader :title="root?.id ? 'Edit Root Folder' : 'Add Root Folder'" :icon="PhFolder" @close="close" />
    </template>

    <template #default>
      <FormSection title="Basic Configuration" :icon="PhFolder">
        <FormRow label="Name">
          <input v-model="form.name" class="form-input" placeholder="Enter a name for this root folder" />
        </FormRow>

        <FormRow label="Path" labelFor="root-path">
          <div class="path-input-row">
            <input id="root-path" v-model="form.path" class="form-input" placeholder="Select or enter a path..." />
            <button
              type="button"
              class="icon-btn btn-secondary btn-inline-browse"
              @click="openBrowser"
              title="Browse for folder"
              aria-label="Browse for folder"
            >
              <PhFolder :size="16" />
            </button>
          </div>

          <!-- Folder browser modal (opens in a centered modal) -->
          <FolderBrowserModal v-model:visible="showBrowser" v-model:modelValue="form.path" :show-input="false" @close="closeBrowser" />
        </FormRow>

        <CheckboxCard v-model="form.isDefault" title="Set as default root folder" />
      </FormSection>
    </template>

    <template #footer>
      <!-- Use ModalFooter props for consistent button styles and behavior -->
      <ModalFooter :showCancel="true" :showSave="true" @cancel="close" @save="save" :saveLabel="'Save'" :showTest="false" />
    </template>
  </Modal> 

      <MoveAudiobookModal
        :visible="showConfirm"
        :pendingRootPath="form.path"
        v-model:moveFiles="modalMoveFiles"
        v-model:deleteEmpty="modalDeleteEmpty"
        @cancel="showConfirm = false"
        @confirm="(payload) => confirmChange(Boolean(payload?.moveFiles))"
      />
</template>

<script setup lang="ts">
import { ref } from 'vue'
import FolderBrowserModal from '@/components/feedback/FolderBrowserModal.vue'
import { Modal, ModalHeader, ModalFooter } from '@/components/feedback'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
// Checkbox not used directly; use CheckboxCard for checkbox UI
import FormSection from './FormSection.vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import { PhFolder } from '@phosphor-icons/vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useToast } from '@/services/toastService'
import type { RootFolder } from '@/types'

const { root } = defineProps<{ root?: RootFolder }>()
const emit = defineEmits<{
  close: []
  saved: [rootFolder: RootFolder]
}>()

const store = useRootFoldersStore()
const toast = useToast()

const form = ref({ name: root?.name || '', path: root?.path || '', isDefault: !!root?.isDefault })

const showConfirm = ref(false)
const modalMoveFiles = ref(true)
const modalDeleteEmpty = ref(true)

// Local state for showing the inline folder browser
const showBrowser = ref(false)

function openBrowser() {
  showBrowser.value = true
}
function closeBrowser() {
  showBrowser.value = false
}
function close() {
  emit('close')
}

async function save() {
  if (!form.value.name || !form.value.path) {
    toast.error('Validation Error', 'Name and Path are required')
    return
  }
  try {
    var newRoot;
    if (root?.id) {
      // If path changed, show confirmation to choose whether to move files
      if (form.value.path !== root.path) {
        showConfirm.value = true
        return
      }
      newRoot = await store.update(root.id, {
        id: root.id,
        name: form.value.name,
        path: form.value.path,
        isDefault: form.value.isDefault,
      })
      toast.success('Success', 'Root folder updated')
    } else {
      newRoot = await store.create({
        name: form.value.name,
        path: form.value.path,
        isDefault: form.value.isDefault,
      })
      toast.success('Success', 'Root folder created')
    }
    emit('saved', newRoot)
  } catch (e: unknown) {
    const error = e as Error
    toast.error('Error', error?.message || 'Failed to save root folder')
  }
}

async function confirmChange(moveFiles: boolean) {
  showConfirm.value = false
  try {
    await store.update(
      root!.id,
      {
        id: root!.id,
        name: form.value.name,
        path: form.value.path,
        isDefault: form.value.isDefault,
      },
      { moveFiles: moveFiles, deleteEmptySource: modalDeleteEmpty.value },
    )
    toast.success(
      'Success',
      moveFiles ? 'Root renamed and move jobs queued' : 'Root renamed (files unchanged)',
    )
    emit('saved', root!)
  } catch (e: unknown) {
    const error = e as Error
    toast.error('Error', error?.message || 'Failed to save root folder')
  }
}
</script>

<style scoped>
/* Modal-specific styling is now provided by shared `modals.css` */
.path-row .path-input-row { display:flex; gap:0.5rem; align-items:center }
.path-row input#root-path { flex:1 }
.folder-browser-overlay { margin-top:0.75rem }

.modal-close {
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 4px;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  justify-content: center;
}

.modal-close:hover {
  background: #444;
  color: #fff;
}

.modal-body {
  padding: 2rem;
  flex: 1;
  overflow-y: auto;
}

.form-row {
  margin-bottom: 1.5rem;
}

.form-row:last-child {
  margin-bottom: 0;
}

.form-row label {
  display: block;
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}

.form-row input {
  width: 100%;
  padding: 0.75rem;
  background: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.2s ease;
}

.form-row input:focus {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 3px rgba(33, 150, 243, 0.1);
}

.form-row input::placeholder {
  color: #666;
}

.checkbox-label {
  display: flex;
  align-items: center;
  gap: 1rem;
  color: #ddd;
  cursor: pointer;
  user-select: none;
  padding: 0.5rem 0;
  text-align: left;
}

.checkbox-label input[type='checkbox'] {
  width: 18px;
  height: 18px;
  margin: 0;
  cursor: pointer;
  flex-shrink: 0;
  -webkit-appearance: none;
  appearance: none;
  background-color: #1a1a1a;
  border: 2px solid #555;
  border-radius: 6px;
  position: relative;
  transition: all 0.2s ease;
  vertical-align: sub;
}

.checkbox-label input[type='checkbox']:hover {
  border-color: var(--brand-500);
}

.checkbox-label input[type='checkbox']:checked {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
}

.checkbox-label input[type='checkbox']:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.3);
  outline-offset: 2px;
} 

.checkbox-label span {
  line-height: 1.4;
  font-size: 0.95rem;
  margin-left: 0.25rem;
}

/* Ensure `Checkbox` component aligns vertically with text in this modal */
.form-row > label.input-checkbox {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0; /* override .form-row label spacing */
}
.form-row > label.input-checkbox .checkbox-box {
  margin-top: 0;
}
.form-row > label.input-checkbox .checkbox-label > :first-child {
  display: inline-flex;
  height: auto;
  align-items: center;
}

/* Make the inline browse button match input height and be compact in this modal */
.path-row .btn-inline-browse {
  width: 40px;
  height: 40px;
  min-width: 40px;
  min-height: 40px;
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
}
.path-row .btn-inline-browse svg {
  width: 18px;
  height: 18px;
}
.path-row .btn-inline-browse:focus {
  outline: none;
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.12);
}
.path-row .btn-inline-browse { align-self: center; }

/* modal-footer base styles are centralized in src/assets/modals.css; keep modal-specific background and spacing */
.modal-footer {
  justify-content: space-between;
  gap: 0.75rem;
  background: #2a2a2a;
}

/* Base `.btn` styles are centralized in src/assets/modals.css; avoid duplicating them here. */

.btn:hover:not(:disabled) {
  background: #444;
}

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn.primary {
  background: #1e88e5;
  color: white;
}

.btn.primary:hover:not(:disabled) {
  background: var(--brand-600);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

.confirm-modal {
  z-index: 1001;
}

.confirm-modal .modal-content {
  max-width: 560px;
}

.confirm-body {
  padding: 1rem;
  background: #1a1a1a;
  border-radius: 6px;
  border: 1px solid #444;
}

.confirm-body p {
  margin: 0 0 0.5rem 0;
  font-weight: 500;
  color: #fff;
}

.confirm-body pre {
  background: #2a2a2a;
  padding: 0.75rem;
  border-radius: 4px;
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 0.85rem;
  word-break: break-all;
  border: 1px solid #444;
  margin: 0.5rem 0;
  color: #adb5bd;
  white-space: pre-wrap;
}

.checkbox-row {
  margin-top: 1rem;
}

.checkbox-row label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  color: #ddd;
  cursor: pointer;
  user-select: none;
  padding: 0.5rem 0;
}

.checkbox-row label input[type='checkbox'] {
  width: 18px;
  height: 18px;
  margin: 0;
  cursor: pointer;
  flex-shrink: 0;
  -webkit-appearance: none;
  appearance: none;
  background-color: #1a1a1a;
  border: 2px solid #555;
  border-radius: 6px;
  position: relative;
  transition: all 0.2s ease;
  vertical-align: sub;
}

.checkbox-row label input[type='checkbox']:hover {
  border-color: var(--brand-500);
}

.checkbox-row label input[type='checkbox']:checked {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
}

.checkbox-row label input[type='checkbox']:checked::after {
  content: '';
  position: absolute;
  left: 5px;
  top: 2px;
  width: 4px;
  height: 8px;
  border: solid white;
  border-width: 0 2px 2px 0;
  transform: rotate(45deg);
}

.checkbox-row label input[type='checkbox']:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.3);
  outline-offset: 2px;
}

/* Form input styling to match login and other forms */
.form-input {
  padding: 0.75rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: white;
  font-size: 1rem;
  width: 100%;
  box-sizing: border-box;
}

.form-input:focus {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 3px rgba(33, 150, 243, 0.1);
}

.path-input-row {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}

.path-input-row .form-input {
  flex: 1;
}
</style>
