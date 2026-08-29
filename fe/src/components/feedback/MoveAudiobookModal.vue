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
  <Modal :visible="visible" size="md" @close="$emit('cancel')">
    <template #header>
      <ModalHeader :title="title" @close="$emit('cancel')" :icon="icon" />
    </template>

    <template #default>
      <ModalBody>
        <div class="confirm-description">
          <p v-if="rootFolderRepair">
            Listenarr needs to reconfirm the storage rules for<span v-if="rootFolderName">
              <strong>{{ rootFolderName }}</strong></span
            >
            before using this folder for filesystem operations.
          </p>
          <p v-else-if="rootFolderChange">
            You're changing the library folder<span v-if="rootFolderName">
              for <strong>{{ rootFolderName }}</strong></span
            >. Confirm that the new path is the storage location you want Listenarr to use.
          </p>
          <p v-else>
            You're updating the audiobook destination. You can update the path only, or choose to
            move files immediately by selecting "Move files now."
          </p>
        </div>

        <div class="path-comparison" v-if="pendingMove || pendingRootPath">
          <div
            class="path-section"
            v-if="
              !rootFolderRepair &&
              ((pendingMove && pendingMove.original) || (rootFolderChange && currentRootPath))
            "
          >
            <div class="path-label">
              <PhArrowRight />
              <span>From:</span>
            </div>
            <div class="path-display">
              <code>{{ pendingMove?.original || currentRootPath }}</code>
            </div>
          </div>

          <div class="path-section">
            <div class="path-label">
              <PhArrowDown />
              <span v-if="rootFolderRepair">Library Folder:</span>
              <span v-else-if="pendingMove || rootFolderChange">To:</span>
              <span v-else>New Root Folder:</span>
            </div>
            <div class="path-display">
              <code>{{ pendingMove?.combined || pendingRootPath || 'No destination path' }}</code>
            </div>
          </div>

          <!-- Path length warning -->
          <div v-if="movePathWarning" class="path-length-warning">
            <PhWarning :size="16" />
            <span>{{ movePathWarning }}</span>
          </div>

          <!-- Hardlink warning -->
          <div
            class="hardlink-warning"
            v-if="showHardlinkWarning && volumeCheckResult?.willBreakHardlinks"
          >
            <PhWarning :size="20" />
            <div class="warning-content">
              <strong>Hardlink Warning</strong>
              <p>
                Moving files across volumes ({{ volumeCheckResult.sourceVolume }} →
                {{ volumeCheckResult.destVolume }}) will break hardlinks and create independent
                copies. The original download will no longer share disk space with the library file.
              </p>
            </div>
          </div>
        </div>

        <div class="confirm-options">
          <div class="checkbox-row" v-if="showMoveOption">
            <label class="checkbox-wrapper checkbox-label">
              <input
                type="checkbox"
                class="checkbox-input"
                :checked="moveFiles && effectiveAllowMoveFiles"
                :disabled="!effectiveAllowMoveFiles"
                @change="onToggleMoveFiles($event)"
                aria-label="Move files now"
              />
              <div class="checkbox-content">
                <span class="checkbox-title">Move files now</span>
                <small v-if="effectiveAllowMoveFiles"
                  >Copy all audiobook files to the new location (recommended)</small
                >
                <small v-else-if="!filesystemReadinessStore.filesystemReady">
                  Files can be moved after library filesystem initialization completes. The path can
                  still be updated without moving files.
                </small>
                <small v-else>
                  Files cannot be moved from the current root on this system. The configured path
                  can still be updated.
                </small>
              </div>
            </label>
          </div>

          <div
            class="checkbox-row"
            v-if="moveFiles && effectiveAllowMoveFiles && !effectiveSourceIsManagedRoot"
          >
            <label class="checkbox-wrapper checkbox-label">
              <input
                type="checkbox"
                class="checkbox-input"
                :checked="deleteEmpty"
                @change="onToggleDeleteEmpty($event)"
                aria-label="Remove empty source folder"
              />
              <div class="checkbox-content">
                <span class="checkbox-title">Remove empty source folder after move</span>
                <small>Deletes the audiobook's old folder after its files move successfully</small>
              </div>
            </label>
          </div>

          <div class="cleanup-summary" v-if="!rootFolderChange && moveFiles">
            <div>
              <strong>Source files</strong>
              <span>{{ sourceFileDisposition }}</span>
            </div>
            <div v-if="effectiveSourceIsManagedRoot">
              <strong>Managed root folder</strong>
              <span>{{ sourcePathLabel }} will remain.</span>
            </div>
          </div>

          <p class="confirm-note" v-if="rootFolderChange">
            Confirming the new folder updates the configured root. When the destination exists,
            Listenarr verifies that exact folder before using it for filesystem operations.
          </p>
          <p class="confirm-note" v-else>
            The primary button will <strong>{{ buttonLabel }}</strong> based on the checkbox. Use
            <strong>Move files now</strong> to perform the move immediately, or leave it unchecked
            to only update the path.
          </p>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button class="cancel-button btn" @click="$emit('cancel')"><PhX /> Cancel</button>
        </template>

        <template #default>
          <button class="btn btn-primary" @click="onSubmit">{{ buttonLabel }}</button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback'
import { PhX, PhArrowRight, PhArrowDown, PhWarning } from '@phosphor-icons/vue'
import type { Component } from 'vue'
import { computed, watch, ref } from 'vue'
import { apiService } from '@/services/api'
import { usePathLengthCheck } from '@/composables/usePathLengthCheck'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'

const props = withDefaults(
  defineProps<{
    visible?: boolean
    title?: string
    pendingMove?: { original?: string; combined?: string } | null
    pendingRootPath?: string | null
    rootFolderChange?: boolean
    rootFolderRepair?: boolean
    currentRootPath?: string | null
    rootFolderName?: string | null
    showMoveOption?: boolean
    allowMoveFiles?: boolean
    allowDeleteEmpty?: boolean
    moveFiles?: boolean
    deleteEmpty?: boolean
    icon?: Component | undefined
  }>(),
  {
    visible: false,
    title: 'Move Audiobook Files',
    pendingMove: null,
    pendingRootPath: null,
    rootFolderChange: false,
    rootFolderRepair: false,
    currentRootPath: null,
    rootFolderName: null,
    showMoveOption: true,
    allowMoveFiles: true,
    allowDeleteEmpty: true,
    moveFiles: true,
    deleteEmpty: true,
    icon: undefined,
  },
)

const emit = defineEmits(['cancel', 'confirm', 'update:moveFiles', 'update:deleteEmpty'])
const filesystemReadinessStore = useFilesystemReadinessStore()
const effectiveAllowMoveFiles = computed(
  () => props.allowMoveFiles && filesystemReadinessStore.filesystemReady,
)

const volumeCheckResult = ref<{
  sameVolume: boolean
  willBreakHardlinks: boolean
  sourceVolume?: string
  destVolume?: string
  message?: string
  verifiedSourceDeletionEnabled?: boolean
  forceCopyAndRetainSource?: boolean
  sourceIsManagedRoot?: boolean
  sourceCleanupMessage?: string
} | null>(null)
const showHardlinkWarning = ref(false)
let volumeCheckGeneration = 0

// Path-length warning for the destination
const moveDestinationPath = computed(
  () => props.pendingMove?.combined || props.pendingRootPath || '',
)
const { pathLengthWarning: movePathWarning } = usePathLengthCheck(moveDestinationPath)

// Check volumes when paths change
watch(
  () => [
    props.pendingMove?.original,
    props.pendingMove?.combined,
    props.currentRootPath,
    props.pendingRootPath,
    props.visible,
    props.moveFiles,
    effectiveAllowMoveFiles.value,
  ],
  async () => {
    const generation = ++volumeCheckGeneration
    volumeCheckResult.value = null
    if (!props.visible || !props.moveFiles || !effectiveAllowMoveFiles.value) {
      showHardlinkWarning.value = false
      return
    }

    const source = props.pendingMove?.original || props.currentRootPath
    const dest = props.pendingMove?.combined || props.pendingRootPath

    if (source && dest) {
      try {
        const result = await apiService.checkVolume(source, dest)
        if (generation !== volumeCheckGeneration) return
        volumeCheckResult.value = result
        showHardlinkWarning.value = result.willBreakHardlinks
      } catch (error) {
        if (generation !== volumeCheckGeneration) return
        console.error('Failed to check volume:', error)
        showHardlinkWarning.value = false
      }
    }
  },
  { immediate: true },
)

function onToggleMoveFiles(e: Event) {
  const t = e.target as HTMLInputElement | null
  emit('update:moveFiles', Boolean(effectiveAllowMoveFiles.value && t && t.checked))
}
function onToggleDeleteEmpty(e: Event) {
  const t = e.target as HTMLInputElement | null
  emit('update:deleteEmpty', Boolean(!effectiveSourceIsManagedRoot.value && t && t.checked))
}

const effectiveSourceIsManagedRoot = computed(() =>
  Boolean(volumeCheckResult.value?.sourceIsManagedRoot || !props.allowDeleteEmpty),
)
const sourcePathLabel = computed(() => props.pendingMove?.original || 'The managed root folder')
const sourceFileDisposition = computed(() => {
  if (volumeCheckResult.value?.forceCopyAndRetainSource) {
    return 'Source files will be retained because this storage cannot safely remove them.'
  }
  if (volumeCheckResult.value?.sameVolume) {
    return 'Source files will be moved into the new location.'
  }
  if (volumeCheckResult.value?.verifiedSourceDeletionEnabled) {
    return 'Source files will be removed after every copied file is verified.'
  }
  if (volumeCheckResult.value?.sourceCleanupMessage) {
    return volumeCheckResult.value.sourceCleanupMessage
  }
  return 'Source files will be retained unless verified source deletion is authorized for both root folders.'
})

const buttonLabel = computed(() => {
  if (props.rootFolderRepair) return 'Confirm Folder'
  if (props.rootFolderChange) {
    return props.moveFiles && effectiveAllowMoveFiles.value
      ? 'Confirm & Move Files'
      : 'Confirm New Folder'
  }
  return props.moveFiles && effectiveAllowMoveFiles.value ? 'Move Files' : 'Update Path'
})

function onSubmit() {
  emit('confirm', {
    moveFiles: Boolean(props.moveFiles && effectiveAllowMoveFiles.value),
    deleteEmpty: Boolean(props.deleteEmpty && !effectiveSourceIsManagedRoot.value),
  })
}
</script>

<style scoped>
.confirm-description {
  padding: 0.5rem 0;
  color: #cfd8dc;
}
.path-comparison {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  background: #252526;
  border-radius: 8px;
  padding: 1rem;
}
.path-section {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.path-label {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  color: #ddd;
}
.path-display code {
  background: #1f1f1f;
  padding: 0.5rem;
  border-radius: 6px;
  color: #e6eef8;
}
.confirm-options {
  margin-top: 0.5rem;
}
.checkbox-row {
  margin-top: 0.5rem;
}
.checkbox-label {
  display: flex;
  gap: 0.75rem;
  align-items: flex-start;
  text-align: left;
}

.checkbox-content {
  display: flex;
  flex-direction: column;
}
.checkbox-content small {
  color: #bfc8cc;
  margin-top: 4px;
}
.checkbox-content .checkbox-title {
  font-weight: 500;
  color: #e6eef8;
}
.confirm-note {
  color: #bfc8cc;
  font-size: 0.9rem;
  margin-top: 0.75rem;
}
.cleanup-summary {
  display: grid;
  gap: 0.65rem;
  margin-top: 0.85rem;
  padding: 0.75rem;
  border: 1px solid #3b3b3b;
  border-radius: 6px;
  background: #252526;
}
.cleanup-summary > div {
  display: grid;
  gap: 0.2rem;
}
.cleanup-summary strong {
  color: #e6eef8;
  font-size: 0.9rem;
}
.cleanup-summary span {
  color: #bfc8cc;
  font-size: 0.85rem;
  line-height: 1.4;
}

/* Path length warning */
.path-length-warning {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 0.5rem;
  padding: 6px 10px;
  background: rgba(255, 152, 0, 0.12);
  border: 1px solid rgba(255, 152, 0, 0.3);
  border-radius: 6px;
  color: #ffb74d;
  font-size: 0.82rem;
}

/* Hardlink warning */
.hardlink-warning {
  display: flex;
  gap: 0.75rem;
  align-items: flex-start;
  background: rgba(255, 152, 0, 0.1);
  border: 1px solid rgba(255, 152, 0, 0.3);
  border-radius: 6px;
  padding: 0.75rem;
  color: #ffb74d;
  margin-top: 0.5rem;
}
.hardlink-warning svg {
  flex-shrink: 0;
  color: #ffb74d;
}
.warning-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}
.warning-content strong {
  color: #fff;
  font-size: 0.95rem;
}
.warning-content p {
  margin: 0;
  color: #ddd;
  font-size: 0.9rem;
  line-height: 1.4;
}

/* Ensure footer spacing and button emphasis match the app styles */
.modal-footer .cancel-button {
  min-width: 120px;
}
.modal-footer .btn {
  min-width: 120px;
}
</style>
