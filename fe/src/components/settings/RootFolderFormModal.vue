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
  <Modal :visible="true" size="md" @close="close">
    <template #header>
      <ModalHeader
        :title="root?.id ? 'Edit Root Folder' : 'Add Root Folder'"
        :icon="PhFolder"
        @close="close"
      />
    </template>

    <template #default>
      <FormSection title="Basic Configuration" :icon="PhFolder">
        <FormRow label="Name">
          <input
            v-model="form.name"
            class="form-input"
            placeholder="Enter a name for this root folder"
          />
        </FormRow>

        <FormRow label="Filesystem behavior">
          <div class="filesystem-behavior-summary">
            <div>
              <strong>{{ filesystemBehaviorTitle }}</strong>
              <small class="semantics-help">{{ filesystemBehaviorDetail }}</small>
            </div>

            <button
              v-if="needsCaseSettingConfirmation"
              type="button"
              class="btn btn-primary detected-semantics-action"
              :disabled="rootFilesystemMutationLocked"
              @click="useDetectedCaseSetting"
            >
              Use detected setting: {{ detectedCaseSettingLabel }}
            </button>
          </div>

          <details class="advanced-semantics">
            <summary>Advanced options</summary>
            <label for="root-case-sensitivity" class="advanced-semantics-label">
              Case sensitivity
            </label>
            <select
              id="root-case-sensitivity"
              v-model="form.caseSensitivityMode"
              class="form-input"
              :disabled="rootFilesystemMutationLocked"
            >
              <option value="Auto">Automatic (recommended)</option>
              <option value="Sensitive">Case-sensitive</option>
              <option value="Insensitive">Case-insensitive</option>
            </select>
          </details>
        </FormRow>

        <FormRow label="Path" labelFor="root-path">
          <div class="path-input-row">
            <input
              id="root-path"
              v-model="form.path"
              class="form-input"
              placeholder="Select or enter a path..."
              :disabled="rootFilesystemMutationLocked"
            />
            <button
              type="button"
              class="icon-btn btn-secondary btn-inline-browse"
              @click="openBrowser"
              :disabled="rootFilesystemMutationLocked"
              title="Browse for folder"
              aria-label="Browse for folder"
            >
              <PhFolder :size="16" />
            </button>
          </div>

          <small v-if="rootFilesystemMutationLocked" class="semantics-help">
            Path and filesystem semantics changes are available after library filesystem
            initialization completes.
          </small>

          <!-- Folder browser modal (opens in a centered modal) -->
          <FolderBrowserModal
            v-model:visible="showBrowser"
            v-model:modelValue="form.path"
            :show-input="false"
            @close="closeBrowser"
          />
        </FormRow>

        <CheckboxCard v-model="form.isDefault" title="Set as default root folder" />
      </FormSection>
    </template>

    <template #footer>
      <!-- Use ModalFooter props for consistent button styles and behavior -->
      <ModalFooter
        :showCancel="true"
        :showSave="true"
        @cancel="close"
        @save="save"
        :saveLabel="'Save'"
        :showTest="false"
      />
    </template>
  </Modal>

  <MoveAudiobookModal
    :visible="showConfirm"
    title="Change Library Folder"
    :pendingRootPath="form.path"
    :currentRootPath="root?.path ?? null"
    :rootFolderName="form.name"
    :rootFolderChange="true"
    :rootFolderRepair="rootStorageRepairRequired() && !rootPathChanged()"
    :showMoveOption="rootPathChanged()"
    :allowMoveFiles="rootPathChanged() && root?.storageState === 'Healthy'"
    v-model:moveFiles="modalMoveFiles"
    v-model:deleteEmpty="modalDeleteEmpty"
    @cancel="showConfirm = false"
    @confirm="(payload) => confirmChange(Boolean(payload?.moveFiles))"
  />
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
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
import { getApiValidationError } from '@/services/apiErrors'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import type { RootFolder } from '@/types'
import { detectPathKind, validateLibraryDestinationPath, type PathKind } from '@/utils/path'
import { persistedRootPathKind, rootFolderPathChanged } from '@/utils/rootFolderPath'
import {
  caseSensitivityLabel,
  detectedMutationSemantics,
  needsMutationSemanticsConfirmation,
} from '@/composables/useMutationSemanticsConfirmation'

const { root } = defineProps<{ root?: RootFolder }>()
const emit = defineEmits<{
  close: []
  saved: [rootFolder: RootFolder]
}>()

const store = useRootFoldersStore()
const filesystemReadinessStore = useFilesystemReadinessStore()
const toast = useToast()
const rootFilesystemMutationLocked = computed(
  () =>
    Boolean(root?.id) &&
    (root?.canChangePath === false || filesystemReadinessStore.filesystemReady === false),
)

const form = ref({
  name: root?.name || '',
  path: root?.path || '',
  isDefault: !!root?.isDefault,
  caseSensitivityMode: root?.caseSensitivityMode ?? ('Auto' as const),
})

const showConfirm = ref(false)
const modalMoveFiles = ref(true)
const modalDeleteEmpty = ref(true)

const detectedCaseSetting = computed(() => (root ? detectedMutationSemantics(root) : null))
const needsCaseSettingConfirmation = computed(
  () =>
    Boolean(root) &&
    form.value.caseSensitivityMode === 'Auto' &&
    needsMutationSemanticsConfirmation(root!),
)
const detectedCaseSettingLabel = computed(() =>
  detectedCaseSetting.value ? caseSensitivityLabel(detectedCaseSetting.value) : 'unknown',
)
const filesystemBehaviorTitle = computed(() => {
  if (needsCaseSettingConfirmation.value) return 'Confirmation needed'
  if (form.value.caseSensitivityMode === 'Sensitive') return 'Case-sensitive'
  if (form.value.caseSensitivityMode === 'Insensitive') return 'Case-insensitive'
  return 'Automatic (recommended)'
})
const filesystemBehaviorDetail = computed(() => {
  if (needsCaseSettingConfirmation.value) {
    return `Listenarr detected ${detectedCaseSettingLabel.value} behavior, but this storage cannot report it reliably enough for file moves and deletes. Confirm the detected setting to enable those operations.`
  }
  if (form.value.caseSensitivityMode !== 'Auto') {
    return 'File operations will use this explicitly configured behavior.'
  }
  if (root?.resolvedCaseSensitivity && root.resolvedCaseSensitivity !== 'Unknown') {
    return `Detected ${caseSensitivityLabel(root.resolvedCaseSensitivity)} behavior automatically.`
  }
  return 'Listenarr will detect case behavior automatically when the storage provides reliable evidence.'
})

function useDetectedCaseSetting() {
  if (detectedCaseSetting.value) {
    form.value.caseSensitivityMode = detectedCaseSetting.value
  }
}

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

function rootPathKind(): PathKind {
  const sourceKind = root ? persistedRootPathKind(root) : 'unknown'
  const detected = detectPathKind(form.value.path, sourceKind)
  return detected === 'unknown' ? sourceKind : detected
}

function rootPathChanged(): boolean {
  return root ? rootFolderPathChanged(root, form.value.path) : false
}

function rootStorageRepairRequired(): boolean {
  return (
    root?.storageReason === 'FilesystemSemanticsChanged' ||
    root?.storageReason === 'FilesystemSemanticsUnavailable'
  )
}

function rootFolderSaveError(error: unknown): string {
  return (
    getApiValidationError(error)?.message ||
    (error instanceof Error ? error.message : 'Failed to save root folder')
  )
}

async function save() {
  if (!form.value.name || !form.value.path) {
    toast.error('Validation Error', 'Name and Path are required')
    return
  }

  const pathValidationError = validateLibraryDestinationPath(form.value.path, {
    pathKind: rootPathKind(),
    requireAbsolute: true,
  })
  if (pathValidationError) {
    toast.error('Validation Error', pathValidationError)
    return
  }

  try {
    let newRoot
    if (root?.id) {
      // If path changed, show confirmation to choose whether to move files
      if (rootPathChanged() || rootStorageRepairRequired()) {
        modalMoveFiles.value = rootPathChanged() && root.storageState === 'Healthy'
        modalDeleteEmpty.value = modalMoveFiles.value
        showConfirm.value = true
        return
      }
      newRoot = await store.update(
        root.id,
        {
          id: root.id,
          name: form.value.name,
          path: form.value.path,
          isDefault: form.value.isDefault,
          caseSensitivityMode: form.value.caseSensitivityMode,
        },
        { expectedCurrentPath: root.path },
      )
      if (newRoot.activeRelocation?.status === 'NeedsAttention') {
        toast.warning(
          'Root folder changed',
          'The root folder was updated, but one or more audiobooks still need path repair.',
        )
      } else {
        toast.success('Success', 'Root folder updated')
      }
    } else {
      newRoot = await store.create({
        name: form.value.name,
        path: form.value.path,
        isDefault: form.value.isDefault,
        caseSensitivityMode: form.value.caseSensitivityMode,
      })
      toast.success('Success', 'Root folder created')
    }
    emit('saved', newRoot)
  } catch (e: unknown) {
    toast.error('Error', rootFolderSaveError(e))
  }
}

async function confirmChange(moveFiles: boolean) {
  showConfirm.value = false
  try {
    const updated = await store.update(
      root!.id,
      {
        id: root!.id,
        name: form.value.name,
        path: form.value.path,
        isDefault: form.value.isDefault,
        caseSensitivityMode: form.value.caseSensitivityMode,
      },
      {
        expectedCurrentPath: root!.path,
        pathChangeConfirmed: true,
        moveFiles,
        deleteEmptySource: modalDeleteEmpty.value,
      },
    )
    if (!moveFiles && updated.activeRelocation?.status === 'NeedsAttention') {
      toast.warning(
        'Root folder changed',
        'The root folder was updated, but one or more audiobooks still need path repair.',
      )
    } else {
      toast.success('Success', moveFiles ? 'Root relocation started' : 'Root folder changed')
    }
    emit('saved', updated)
  } catch (e: unknown) {
    toast.error('Error', rootFolderSaveError(e))
  }
}
</script>

<style scoped>
/* Modal-specific styling is now provided by shared `modals.css` */
.path-row .path-input-row {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}
.path-row input#root-path {
  flex: 1;
}
.folder-browser-overlay {
  margin-top: 0.75rem;
}

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
.path-row .btn-inline-browse {
  align-self: center;
}

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

.filesystem-behavior-summary {
  display: grid;
  gap: 0.75rem;
  padding: 0.875rem;
  border: 1px solid var(--border-color, #444);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.025);
}

.filesystem-behavior-summary strong,
.filesystem-behavior-summary .semantics-help {
  display: block;
}

.filesystem-behavior-summary .semantics-help {
  margin-top: 0.25rem;
  line-height: 1.45;
}

.detected-semantics-action {
  justify-self: start;
}

.advanced-semantics {
  margin-top: 0.75rem;
}

.advanced-semantics summary {
  cursor: pointer;
  color: var(--text-secondary, #aaa);
}

.advanced-semantics-label {
  display: block;
  margin: 0.75rem 0 0.35rem;
  color: var(--text-secondary, #bbb);
  font-size: 0.875rem;
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
