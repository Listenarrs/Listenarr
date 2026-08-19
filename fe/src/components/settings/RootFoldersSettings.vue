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
  <div class="root-folders-settings">
    <div v-if="!props.hideHeader" class="section-header">
      <h3>
        Root Folders
        <PhSpinner v-if="store.loading" class="ph-spin small-inline-spinner" />
      </h3>
    </div>
    <div v-if="store.loading" class="loading-state">
      <PhSpinner class="ph-spin" />
      <p>Loading root folders...</p>
    </div>

    <div v-else>
      <div v-if="store.folders.length === 0" class="empty-state">
        <PhFolderOpen />
        <h4>No root folders configured</h4>
        <p>
          Add a root folder to organize your audiobook library. You can create multiple named root
          folders for different storage locations.
        </p>
      </div>

      <div v-else class="folders-list">
        <div
          v-for="folder in store.folders"
          :key="folder.id"
          class="folder-card"
          :class="{ 'is-default': folder.isDefault }"
        >
          <div class="folder-info">
            <div class="folder-header">
              <div class="folder-title-section">
                <div class="folder-name-row">
                  <h4>{{ folder.name }}</h4>
                  <div class="folder-badges">
                    <Pill variant="success" v-if="folder.isDefault">Default</Pill>
                    <Pill v-if="folder.storageState === 'Healthy'" variant="success">Healthy</Pill>
                    <Pill
                      v-else-if="folder.storageReason === 'MutationSemanticsUnproven'"
                      variant="warning"
                    >
                      Needs case setting
                    </Pill>
                    <Pill v-else-if="folder.storageState === 'Limited'" variant="warning">
                      Limited
                    </Pill>
                    <Pill v-else-if="folder.storageState === 'Missing'" variant="warning"
                      >Missing</Pill
                    >
                    <Pill v-else-if="folder.storageState === 'Changed'" variant="error">
                      Folder changed
                    </Pill>
                    <Pill v-else-if="folder.storageState === 'Unconfirmed'" variant="warning">
                      Needs confirmation
                    </Pill>
                    <Pill v-else-if="folder.storageState === 'Unavailable'" variant="error">
                      Unavailable
                    </Pill>
                    <Pill v-else-if="folder.storageState === 'Initializing'" variant="subtle">
                      Initializing
                    </Pill>
                    <Pill
                      v-else-if="folder.storageState === 'InitializationFailed'"
                      variant="error"
                    >
                      Initialization failed
                    </Pill>
                    <Pill v-else variant="subtle">{{ folder.resolvedCaseSensitivity }}</Pill>
                  </div>
                </div>
              </div>
              <div class="folder-actions">
                <button
                  class="icon-button action-scan"
                  @click="scanUnmatched(folder)"
                  title="Scan for unmatched files"
                  data-cy="scan-unmatched"
                  :disabled="
                    filesystemReadinessStore.filesystemReady === false ||
                    folder.canScanFilesystem === false ||
                    !!folder.activeRelocation
                  "
                >
                  <PhMagnifyingGlass />
                </button>
                <button
                  class="icon-button action-edit"
                  @click="edit(folder)"
                  title="Edit"
                  data-cy="edit-root-folder"
                  :disabled="!!folder.activeRelocation"
                >
                  <PhPencil />
                </button>
                <button
                  v-if="folder.canConfirmCurrentFolder && folder.confirmationToken"
                  class="icon-button action-secondary"
                  @click="openFolderConfirmation(folder)"
                  title="Confirm this folder"
                  data-cy="confirm-root-folder"
                  :disabled="!!folder.activeRelocation"
                >
                  <PhShieldCheck />
                </button>
                <button
                  v-if="!folder.isDefault"
                  class="icon-button action-secondary"
                  @click="setDefaultFolder(folder)"
                  title="Set as Default"
                  :disabled="!!folder.activeRelocation"
                >
                  <PhStar />
                </button>
                <button
                  class="icon-button danger action-delete"
                  @click="confirmDelete(folder)"
                  title="Delete"
                  data-cy="delete-root-folder"
                  :disabled="!!folder.activeRelocation"
                >
                  <PhTrash />
                </button>
              </div>
            </div>
            <div class="folder-path">
              <PhFolder />
              <code>{{ folder.path }}</code>
            </div>
            <div
              v-if="needsMutationSemanticsConfirmation(folder)"
              class="storage-guidance"
              data-cy="mutation-semantics-guidance"
            >
              <PhWarningCircle class="storage-guidance-icon" />
              <div class="storage-guidance-copy">
                <strong>One storage setting needs confirmation</strong>
                <span>
                  Listenarr detected this root as
                  {{ detectedCaseSettingLabel(folder) }}, but the storage cannot report that
                  reliably enough for file moves and deletes.
                </span>
              </div>
              <button
                type="button"
                class="btn btn-primary storage-guidance-action"
                :disabled="confirmingSemanticsRootId === folder.id || !!folder.activeRelocation"
                @click="confirmDetectedCaseSetting(folder)"
              >
                <PhSpinner v-if="confirmingSemanticsRootId === folder.id" class="ph-spin" />
                {{
                  confirmingSemanticsRootId === folder.id
                    ? 'Saving...'
                    : `Use detected setting: ${detectedCaseSettingLabel(folder)}`
                }}
              </button>
            </div>
            <p
              v-else-if="folder.storageState !== 'Healthy' && folder.storageMessage"
              class="storage-message"
            >
              {{ folder.storageMessage }}
            </p>
            <details
              v-if="folder.storageState !== 'Healthy' && folder.storageDetail"
              class="storage-detail"
            >
              <summary>Technical storage details</summary>
              <code>{{ folder.storageDetail }}</code>
            </details>
            <section
              v-if="folder.activeRelocation"
              class="relocation-state"
              :class="{ 'needs-attention': folder.activeRelocation.status === 'NeedsAttention' }"
            >
              <div class="relocation-header">
                <div class="relocation-heading">
                  <PhWarningCircle
                    v-if="
                      folder.activeRelocation.status === 'NeedsAttention' ||
                      folder.activeRelocation.status === 'Failed'
                    "
                    class="relocation-icon"
                  />
                  <PhSpinner v-else class="ph-spin relocation-icon" />
                  <div>
                    <strong>{{ relocationTitle(folder.activeRelocation) }}</strong>
                    <span class="relocation-progress-copy">
                      {{ relocationProgressLabel(folder.activeRelocation) }}
                    </span>
                  </div>
                </div>
                <div
                  v-if="canRetryRelocation(folder) || folder.activeRelocation.canAbandon"
                  class="relocation-actions"
                >
                  <button
                    v-if="canRetryRelocation(folder)"
                    type="button"
                    :disabled="
                      retryingRelocationId === folder.activeRelocation.relocationId ||
                      abandoningRelocationId === folder.activeRelocation.relocationId ||
                      filesystemReadinessStore.filesystemReady === false
                    "
                    class="btn btn-secondary relocation-retry"
                    @click="retryRelocation(folder)"
                  >
                    <PhSpinner
                      v-if="retryingRelocationId === folder.activeRelocation.relocationId"
                      class="ph-spin"
                    />
                    {{
                      retryingRelocationId === folder.activeRelocation.relocationId
                        ? 'Retrying...'
                        : folder.activeRelocation.mode === 'MetadataOnly'
                          ? folder.activeRelocation.status === 'Failed'
                            ? 'Retry repair'
                            : 'Retry remaining'
                          : 'Retry'
                    }}
                  </button>
                  <button
                    v-if="folder.activeRelocation.canAbandon"
                    type="button"
                    class="btn btn-secondary relocation-retry"
                    :disabled="
                      abandoningRelocationId === folder.activeRelocation.relocationId ||
                      retryingRelocationId === folder.activeRelocation.relocationId ||
                      filesystemReadinessStore.filesystemReady === false
                    "
                    @click="confirmAbandonRelocation(folder)"
                  >
                    <PhSpinner
                      v-if="abandoningRelocationId === folder.activeRelocation.relocationId"
                      class="ph-spin"
                    />
                    {{
                      abandoningRelocationId === folder.activeRelocation.relocationId
                        ? 'Canceling...'
                        : 'Cancel unfinished'
                    }}
                  </button>
                </div>
              </div>

              <div
                class="relocation-progress"
                role="progressbar"
                aria-label="Root folder path change progress"
                aria-valuemin="0"
                aria-valuemax="100"
                :aria-valuenow="relocationProgressPercent(folder.activeRelocation)"
              >
                <span
                  class="relocation-progress-bar"
                  :style="{ width: `${relocationProgressPercent(folder.activeRelocation)}%` }"
                />
              </div>

              <p class="relocation-description">
                {{ relocationDescription(folder.activeRelocation) }}
              </p>

              <p v-if="showRelocationTarget(folder)" class="relocation-target">
                Destination: <code>{{ folder.activeRelocation.targetPath }}</code>
              </p>

              <details
                v-if="folder.activeRelocation.skippedAudiobookIds?.length"
                class="relocation-affected"
              >
                <summary>
                  {{ folder.activeRelocation.skippedAudiobookIds.length }}
                  {{
                    folder.activeRelocation.skippedAudiobookIds.length === 1
                      ? 'audiobook needs attention'
                      : 'audiobooks need attention'
                  }}
                </summary>
                <div class="relocation-audiobooks">
                  <div
                    v-for="audiobookId in folder.activeRelocation.skippedAudiobookIds"
                    :key="audiobookId"
                    class="relocation-audiobook-item"
                  >
                    <div class="relocation-audiobook-row">
                      <a :href="`/audiobooks/${audiobookId}`" class="relocation-audiobook-link">
                        Audiobook #{{ audiobookId }}
                      </a>
                      <span class="relocation-audiobook-reason">
                        {{ skippedReasonLabel(folder.activeRelocation, audiobookId) }}
                      </span>
                      <button
                        v-if="canReviewSkippedRepair(folder.activeRelocation)"
                        type="button"
                        class="btn btn-secondary relocation-review"
                        :disabled="loadingRepairAudiobookId === audiobookId"
                        @click="loadMetadataRepairDetails(folder.activeRelocation, audiobookId)"
                      >
                        <PhSpinner
                          v-if="loadingRepairAudiobookId === audiobookId"
                          class="ph-spin"
                        />
                        {{ loadingRepairAudiobookId === audiobookId ? 'Loading...' : 'Review' }}
                      </button>
                    </div>

                    <div v-if="metadataRepairDetails[audiobookId]" class="metadata-repair-details">
                      <strong>{{ metadataRepairDetails[audiobookId].audiobookTitle }}</strong>
                      <p v-if="metadataRepairDetails[audiobookId].collisionGroups.length === 0">
                        No remaining conflicting tracked file records were found. Retry the path
                        repair to finish updating this audiobook.
                      </p>
                      <div
                        v-for="group in metadataRepairDetails[audiobookId].collisionGroups"
                        :key="group.targetRelativePath"
                        class="metadata-repair-collision"
                      >
                        <p>
                          These records would refer to the same destination:
                          <code>{{ group.targetRelativePath }}</code>
                        </p>
                        <div
                          v-for="file in group.files"
                          :key="file.audiobookFileId"
                          class="metadata-repair-file"
                        >
                          <div class="metadata-repair-file-label">
                            <code>{{ file.relativePath }}</code>
                            <span v-if="!file.canRemove">
                              Tracked by Audiobook #{{ file.audiobookId }}
                            </span>
                          </div>
                          <button
                            v-if="file.canRemove"
                            type="button"
                            class="btn btn-secondary"
                            @click="
                              confirmRemoveTrackedFile(folder.activeRelocation, audiobookId, file)
                            "
                          >
                            Remove tracked record
                          </button>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </details>
            </section>
          </div>
        </div>
      </div>
    </div>

    <RootFolderFormModal v-if="showForm" :root="editingRoot" @close="close" @saved="onSaved" />

    <UnmatchedFilesModal
      :is-open="showUnmatchedModal"
      :root-folder="scanningFolder"
      @close="showUnmatchedModal = false"
    />

    <!-- Delete Root Folder Confirmation (shared) -->
    <DeleteConfirmationModal
      :visible="!!folderToDelete"
      title="Delete Root Folder"
      @close="folderToDelete = null"
      @confirm="executeDeleteFolder"
    >
      <template v-slot>
        <p>
          Are you sure you want to delete the root folder
          <strong>{{ folderToDelete?.name }}</strong
          >?
        </p>
        <p>This will only remove the reference and will not delete files from disk.</p>
      </template>
    </DeleteConfirmationModal>

    <DeleteConfirmationModal
      :visible="relocationToAbandon !== null"
      title="Cancel unfinished relocation"
      confirm-text="Cancel relocation"
      @close="relocationToAbandon = null"
      @confirm="abandonUnpublishedRelocation"
    >
      <template #default>
        <p>
          Cancel the unfinished relocation for
          <strong>{{ relocationToAbandon?.rootName }}</strong
          >?
        </p>
        <p>
          No audiobook move jobs were published. Listenarr will release this failed relocation and
          remove only empty destination directories it can still prove it created. It will not move
          or delete audiobook files, and any unproven or non-empty destination directory will be
          left in place.
        </p>
      </template>
    </DeleteConfirmationModal>

    <DeleteConfirmationModal
      :visible="repairFileToRemove !== null"
      title="Remove tracked file record"
      confirm-text="Remove record"
      @close="repairFileToRemove = null"
      @confirm="removeTrackedRepairFile"
    >
      <template #default>
        <p>
          Remove <code>{{ repairFileToRemove?.relativePath }}</code> from this audiobook's tracked
          file records?
        </p>
        <p>
          This changes Listenarr metadata only. It does <strong>not</strong> delete or rename any
          file on disk. Use this only when this tracked record is stale or should no longer belong
          to the audiobook.
        </p>
      </template>
    </DeleteConfirmationModal>

    <DeleteConfirmationModal
      :visible="rootToConfirm !== null"
      title="Confirm library folder"
      confirm-text="Confirm folder"
      @close="rootToConfirm = null"
      @confirm="executeFolderConfirmation"
    >
      <template #confirm-icon><PhShieldCheck /></template>
      <template #default>
        <p v-if="rootToConfirm?.storageState === 'Changed'">
          The folder currently at this location is different from the folder Listenarr previously
          used for <strong>{{ rootToConfirm?.name }}</strong
          >.
        </p>
        <p v-else>
          Listenarr needs to confirm the folder currently configured for
          <strong>{{ rootToConfirm?.name }}</strong> before using it for filesystem operations.
        </p>
        <p>
          <code
            class="folder-confirmation-target-path"
            data-testid="root-folder-confirmation-path"
            >{{ rootToConfirm?.path }}</code
          >
        </p>
        <p>
          Confirm only if this is the folder you want Listenarr to use. Confirming it does not move,
          modify, or delete any files.
        </p>
      </template>
    </DeleteConfirmationModal>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch } from 'vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import RootFolderFormModal from '@/components/settings/RootFolderFormModal.vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import UnmatchedFilesModal from '@/components/feedback/UnmatchedFilesModal.vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { apiService } from '@/services/api'
import { getApiValidationError } from '@/services/apiErrors'
import { Pill } from '@/components/base'
import {
  PhFolder,
  PhPencil,
  PhTrash,
  PhSpinner,
  PhFolderOpen,
  PhStar,
  PhMagnifyingGlass,
  PhShieldCheck,
  PhWarningCircle,
} from '@phosphor-icons/vue'
import type {
  RootFolder,
  RootFolderMetadataRepairCollisionFile,
  RootFolderMetadataRepairDetails,
  RootFolderPathChangeResult,
  RootFolderRelocationSkipReasonCode,
} from '@/types'
import { signalRService } from '@/services/signalr'
import {
  applyDetectedMutationSemantics,
  caseSensitivityLabel,
  detectedMutationSemantics,
  needsMutationSemanticsConfirmation,
} from '@/composables/useMutationSemanticsConfirmation'

interface Props {
  hideHeader?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  hideHeader: false,
})

const store = useRootFoldersStore()
const filesystemReadinessStore = useFilesystemReadinessStore()
const showForm = ref(false)
const editing = ref<{ id?: number; name: string; path: string } | null>(null)
const showUnmatchedModal = ref(false)
const scanningFolder = ref<RootFolder | null>(null)
const editingRoot = computed(() => editing.value as RootFolder | undefined)
const toast = useToast()
const retryingRelocationId = ref<string | null>(null)
const abandoningRelocationId = ref<string | null>(null)
const confirmingSemanticsRootId = ref<number | null>(null)
const relocationToAbandon = ref<{
  relocationId: string
  rootName: string
} | null>(null)
const loadingRepairAudiobookId = ref<number | null>(null)
const metadataRepairDetails = ref<Record<number, RootFolderMetadataRepairDetails>>({})
const repairFileToRemove = ref<{
  relocationId: string
  audiobookId: number
  audiobookFileId: number
  relativePath: string
} | null>(null)
const rootToConfirm = ref<{
  id: number
  name: string
  path: string
  storageState?: RootFolder['storageState']
  confirmationToken: string
} | null>(null)

onMounted(async () => {
  await store.load()
})

watch(
  () => filesystemReadinessStore.filesystemStatus,
  (status, previous) => {
    if (status !== previous && (status === 'Ready' || status === 'Failed')) {
      void store.load()
    }
  },
)

const refreshRootFolders = () => {
  store.load().catch(() => {})
}
const unsubscribeRelocation =
  typeof signalRService.onRootFolderRelocationUpdate === 'function'
    ? signalRService.onRootFolderRelocationUpdate(refreshRootFolders)
    : () => {}
const unsubscribeConnected =
  typeof signalRService.onConnected === 'function'
    ? signalRService.onConnected(refreshRootFolders)
    : () => {}
onUnmounted(() => {
  unsubscribeRelocation()
  unsubscribeConnected()
})

function openAdd() {
  editing.value = null
  showForm.value = true
}

function scanUnmatched(folder: RootFolder) {
  scanningFolder.value = folder
  showUnmatchedModal.value = true
}

function detectedCaseSettingLabel(folder: RootFolder): string {
  const detected = detectedMutationSemantics(folder)
  return detected ? caseSensitivityLabel(detected) : 'unknown'
}

async function confirmDetectedCaseSetting(folder: RootFolder) {
  if (!folder.id || confirmingSemanticsRootId.value !== null) return
  confirmingSemanticsRootId.value = folder.id
  try {
    await applyDetectedMutationSemantics(folder)
  } finally {
    confirmingSemanticsRootId.value = null
  }
}

function edit(r: { id?: number; name: string; path: string }) {
  editing.value = { ...r }
  showForm.value = true
}

const folderToDelete = ref<{ id?: number; name: string; path: string } | null>(null)

function confirmDelete(r: { id?: number; name: string; path: string }) {
  if (!r.id) return
  folderToDelete.value = r
}

const executeDeleteFolder = async () => {
  if (!folderToDelete.value?.id) return
  try {
    await store.remove(folderToDelete.value.id)
    toast.success('Success', 'Root folder deleted')
    folderToDelete.value = null
  } catch (e: unknown) {
    errorTracking.captureException(e as Error, {
      component: 'RootFoldersSettings',
      operation: 'deleteRootFolder',
    })
    toast.error('Error', (e as Error)?.message || 'Failed to delete root folder')
  }
}

const setDefaultFolder = async (folder: RootFolder) => {
  if (!folder.id || folder.activeRelocation) return
  try {
    await store.update(folder.id, { ...folder, isDefault: true })
    toast.success('Root folder', `${folder.name} set as default`)
  } catch (e: unknown) {
    errorTracking.captureException(e as Error, {
      component: 'RootFoldersSettings',
      operation: 'setDefaultFolder',
    })
    toast.error('Set default failed', (e as Error)?.message || 'Failed to set default root folder')
  }
}

function openFolderConfirmation(folder: RootFolder) {
  if (!folder.id || folder.activeRelocation || !folder.confirmationToken) return
  rootToConfirm.value = {
    id: folder.id,
    name: folder.name,
    path: folder.path,
    storageState: folder.storageState,
    confirmationToken: folder.confirmationToken,
  }
}

async function executeFolderConfirmation() {
  const confirmation = rootToConfirm.value
  if (!confirmation) return
  rootToConfirm.value = null
  try {
    await store.confirmCurrentFolder(
      confirmation.id,
      confirmation.path,
      confirmation.confirmationToken,
    )
    toast.success('Root folder', 'Library folder confirmed')
  } catch (e: unknown) {
    errorTracking.captureException(e as Error, {
      component: 'RootFoldersSettings',
      operation: 'confirmRootFolder',
    })
    toast.error(
      'Folder confirmation failed',
      (e as Error)?.message || 'Failed to confirm the current library folder',
    )
  }
}

function skippedReasonCode(
  relocation: RootFolderPathChangeResult,
  audiobookId: number,
): RootFolderRelocationSkipReasonCode {
  return (
    relocation.skippedItems?.find((item) => item.audiobookId === audiobookId)?.reasonCode ??
    'Unknown'
  )
}

function skippedReasonLabel(relocation: RootFolderPathChangeResult, audiobookId: number): string {
  switch (skippedReasonCode(relocation, audiobookId)) {
    case 'TargetIdentityCollision':
      return 'Tracked file paths collide at this destination.'
    case 'TargetIdentityUnresolvedConflict':
      return 'A destination file identity is unresolved.'
    case 'InvalidStoredPath':
      return 'Stored audiobook paths need repair.'
    case 'SourceSemanticsUnavailable':
      return 'The old path semantics could not be reconstructed.'
    case 'TargetPathInvalid':
      return 'One or more stored paths are invalid for this destination.'
    default:
      return 'Stored paths could not be updated safely.'
  }
}

function canReviewSkippedRepair(relocation: RootFolderPathChangeResult): boolean {
  return (
    !!relocation.relocationId &&
    relocation.mode === 'MetadataOnly' &&
    relocation.status === 'NeedsAttention'
  )
}

async function loadMetadataRepairDetails(
  relocation: RootFolderPathChangeResult,
  audiobookId: number,
) {
  if (!relocation.relocationId || loadingRepairAudiobookId.value !== null) return
  loadingRepairAudiobookId.value = audiobookId
  try {
    const details = await apiService.getRootFolderMetadataRepairDetails(
      relocation.relocationId,
      audiobookId,
    )
    metadataRepairDetails.value = {
      ...metadataRepairDetails.value,
      [audiobookId]: details,
    }
  } catch (error: unknown) {
    toast.error(
      'Unable to load path repair',
      getApiValidationError(error)?.message ||
        (error instanceof Error ? error.message : 'Failed to load path repair details'),
    )
  } finally {
    loadingRepairAudiobookId.value = null
  }
}

function confirmRemoveTrackedFile(
  relocation: RootFolderPathChangeResult,
  audiobookId: number,
  file: RootFolderMetadataRepairCollisionFile,
) {
  if (!relocation.relocationId) return
  repairFileToRemove.value = {
    relocationId: relocation.relocationId,
    audiobookId,
    audiobookFileId: file.audiobookFileId,
    relativePath: file.relativePath,
  }
}

async function removeTrackedRepairFile() {
  const pending = repairFileToRemove.value
  if (!pending) return
  try {
    const details = await apiService.removeRootFolderMetadataRepairFile(
      pending.relocationId,
      pending.audiobookId,
      pending.audiobookFileId,
    )
    metadataRepairDetails.value = {
      ...metadataRepairDetails.value,
      [pending.audiobookId]: details,
    }
    if (details.collisionGroups.length === 0) {
      toast.success(
        'Path conflict resolved',
        'Retry the remaining path repair to update this audiobook.',
      )
    } else {
      toast.success(
        'Tracked record removed',
        'Review the remaining conflicting records before retrying.',
      )
    }
  } catch (error: unknown) {
    toast.error(
      'Unable to remove tracked record',
      getApiValidationError(error)?.message ||
        (error instanceof Error ? error.message : 'Failed to remove tracked file record'),
    )
  } finally {
    repairFileToRemove.value = null
  }
}

function relocationRemainingCount(relocation: RootFolderPathChangeResult): number {
  if (relocation.mode === 'MetadataOnly' && relocation.skippedAudiobookIds?.length) {
    return relocation.skippedAudiobookIds.length
  }
  return Math.max(0, relocation.totalJobs - relocation.completedJobs)
}

function relocationProgressPercent(relocation: RootFolderPathChangeResult): number {
  if (relocation.totalJobs <= 0) return 0
  return Math.min(100, Math.round((relocation.completedJobs / relocation.totalJobs) * 100))
}

function relocationTitle(relocation: RootFolderPathChangeResult): string {
  if (relocation.status === 'NeedsAttention') {
    return relocation.mode === 'MetadataOnly'
      ? 'Path repair needs attention'
      : 'Library move needs attention'
  }
  if (relocation.status === 'Failed') {
    return relocation.mode === 'MetadataOnly' ? 'Path repair failed' : 'Path change failed'
  }
  return relocation.mode === 'MetadataOnly' ? 'Repairing audiobook paths' : 'Moving library'
}

function relocationProgressLabel(relocation: RootFolderPathChangeResult): string {
  if (relocation.mode === 'MetadataOnly') {
    return `${relocation.completedJobs} of ${relocation.totalJobs} audiobooks updated`
  }
  return `${relocation.completedJobs} of ${relocation.totalJobs} move jobs completed`
}

function relocationDescription(relocation: RootFolderPathChangeResult): string {
  const remaining = relocationRemainingCount(relocation)
  if (relocation.mode === 'MetadataOnly' && relocation.status === 'NeedsAttention') {
    const subject = remaining === 1 ? 'audiobook still needs' : 'audiobooks still need'
    return `The root folder path is updated. ${remaining} ${subject} manual review because the stored paths could not be updated safely. Open the affected audiobooks, correct the path conflicts, then retry.`
  }
  return (
    relocation.error ||
    (relocation.mode === 'MetadataOnly'
      ? 'Listenarr is updating stored audiobook paths.'
      : 'Listenarr is moving the library to the selected destination.')
  )
}

function showRelocationTarget(folder: RootFolder): boolean {
  const relocation = folder.activeRelocation
  return (
    !!relocation && (relocation.mode !== 'MetadataOnly' || folder.path !== relocation.targetPath)
  )
}

function confirmAbandonRelocation(folder: RootFolder) {
  const relocation = folder.activeRelocation
  if (!relocation?.relocationId || !relocation.canAbandon) return
  relocationToAbandon.value = {
    relocationId: relocation.relocationId,
    rootName: folder.name,
  }
}

const abandonUnpublishedRelocation = async () => {
  const pending = relocationToAbandon.value
  if (!pending || abandoningRelocationId.value) return
  relocationToAbandon.value = null
  abandoningRelocationId.value = pending.relocationId
  try {
    await store.abandonUnpublishedRelocation(pending.relocationId)
    toast.success(
      'Root relocation canceled',
      'No audiobook files were moved. Any unproven or non-empty destination directories were left in place.',
    )
  } catch (e: unknown) {
    toast.error(
      'Cancel relocation failed',
      getApiValidationError(e)?.message ||
        (e instanceof Error ? e.message : 'Failed to cancel the unfinished relocation'),
    )
  } finally {
    abandoningRelocationId.value = null
  }
}

const retryRelocation = async (folder: RootFolder) => {
  const relocation = folder.activeRelocation
  const relocationId = relocation?.relocationId
  if (!relocationId || retryingRelocationId.value) return
  retryingRelocationId.value = relocationId
  try {
    const result = await store.retryRelocation(relocationId)
    metadataRepairDetails.value = {}
    if (result.status === 'Completed') {
      toast.success(
        'Root folder',
        relocation.mode === 'MetadataOnly' ? 'Path repair complete' : 'Relocation complete',
      )
    } else if (result.status === 'Failed') {
      toast.error(
        relocation.mode === 'MetadataOnly' ? 'Path repair failed' : 'Root relocation failed',
        result.error || 'The recovery attempt failed. Review the server logs and try again.',
      )
    } else if (relocation.mode === 'MetadataOnly') {
      const remaining = relocationRemainingCount(result)
      toast.warning(
        'Path repair still needs attention',
        `${remaining} ${remaining === 1 ? 'audiobook still needs' : 'audiobooks still need'} review.`,
      )
    } else {
      toast.info('Root relocation', 'Relocation queued for retry')
    }
  } catch (e: unknown) {
    toast.error(
      'Retry failed',
      getApiValidationError(e)?.message ||
        (e instanceof Error ? e.message : 'Failed to retry relocation'),
    )
  } finally {
    retryingRelocationId.value = null
  }
}

function canRetryRelocation(folder: RootFolder): boolean {
  const relocation = folder.activeRelocation
  return (
    !!relocation &&
    !relocation.canAbandon &&
    (relocation.status === 'NeedsAttention' ||
      (relocation.mode === 'MetadataOnly' && relocation.status === 'Failed')) &&
    (relocation.mode === 'MetadataOnly' ||
      relocation.targetIdentityEnrollmentState === 'Authorized' ||
      relocation.targetIdentityEnrollmentState === 'NotRequired')
  )
}

function close() {
  showForm.value = false
}
function onSaved() {
  showForm.value = false
  store.load().catch(() => {})
}

// Expose the openAdd method so parent components can call it
defineExpose({
  openAdd,
})
</script>

<style scoped>
.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.section-header h3 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.section-header .small-inline-spinner {
  margin-left: 0.5rem;
  width: 18px;
  height: 18px;
}

.add-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1.5rem;
  background: #1e88e5;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  font-size: 0.95rem;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
  transition: all 0.2s ease;
}

.add-button:hover {
  background: #1565c0;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

.loading-state {
  text-align: center;
  padding: 3rem;
  color: #adb5bd;
}

.loading-state p {
  margin: 1rem 0 0 0;
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  color: #868e96;
  min-height: 40vh; /* center within the tab when empty */
  gap: 1rem;
}

.empty-state svg {
  font-size: 2rem;
  color: #4dabf7;
  opacity: 0.9;
  margin-bottom: 0.25rem;
}

.empty-state h4 {
  margin: 0;
  color: #fff;
  font-size: 1.6rem;
  font-weight: 500;
}

.empty-state p {
  margin: 0.5rem 0;
  font-size: 1.05rem;
  line-height: 1.6;
  color: #adb5bd;
}

.folders-list {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
  gap: 1.5rem;
  margin-top: 1.5rem;
}

.folder-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  transition: all 0.2s ease;
  display: flex;
  justify-content: space-between;
}

.folder-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.15);
}

.folder-card.disabled {
  opacity: 0.5;
  filter: grayscale(50%);
}

.folder-card.is-default {
  border-color: rgba(77, 171, 247, 0.3);
  background: rgba(77, 171, 247, 0.05);
}

.folder-info {
  flex: 1;
  min-width: 0;
}

.folder-header {
  display: flex;
  justify-content: space-between;
  align-items: center; /* align actions vertically with title */
  padding: 1.5rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  gap: 0.5rem;
}

.folder-title-section {
  flex: 1;
}

.folder-name-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0; /* remove extra gap so title centers with actions */
}

/* Badge styles - Now using Pill component from @/components/base */
.folder-badges {
  display: flex;
  gap: 0.5rem;
}

.folder-info h4,
.folder-header h4 {
  margin: 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.folder-path {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ccc;
  font-size: 0.9rem;
  padding: 1.5rem;
}

.folder-path code {
  background: #1a1a1a;
  padding: 0.25rem 0.5rem;
  border-radius: 4px;
  font-family: monospace;
  word-break: break-all;
  color: #4dabf7;
}

.folder-confirmation-target-path {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.storage-guidance {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 0.75rem;
  margin: -0.75rem 1.5rem 1.5rem;
  padding: 0.875rem;
  border: 1px solid color-mix(in srgb, var(--warning-500) 45%, transparent);
  border-radius: 8px;
  background: color-mix(in srgb, var(--warning-500) 8%, transparent);
}

.storage-guidance-icon {
  color: var(--warning-500);
  font-size: 1.25rem;
}

.storage-guidance-copy {
  display: grid;
  gap: 0.2rem;
  min-width: 0;
}

.storage-guidance-copy span {
  color: var(--text-secondary);
  font-size: 0.85rem;
  line-height: 1.4;
}

.storage-guidance-action {
  white-space: nowrap;
}

@media (max-width: 760px) {
  .storage-guidance {
    grid-template-columns: auto minmax(0, 1fr);
  }

  .storage-guidance-action {
    grid-column: 1 / -1;
    justify-self: start;
  }
}

.storage-message {
  margin: -0.75rem 1.5rem 1.5rem;
  color: var(--text-secondary);
  font-size: 0.85rem;
  line-height: 1.4;
}

.storage-detail {
  margin: -1rem 1.5rem 1.5rem;
  color: var(--text-secondary);
  font-size: 0.8rem;
}

.storage-detail summary {
  cursor: pointer;
}

.storage-detail code {
  display: block;
  margin-top: 0.5rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.relocation-state {
  margin: 0 1.5rem 1.5rem;
  padding: 1rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 8px;
  background: rgba(0, 0, 0, 0.16);
}

.relocation-state.needs-attention {
  border-color: color-mix(in srgb, var(--warning-500) 42%, transparent);
  background: color-mix(in srgb, var(--warning-500) 8%, transparent);
}

.relocation-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.relocation-heading {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  min-width: 0;
}

.relocation-heading strong {
  display: block;
  color: var(--text-primary, #fff);
  font-size: 0.95rem;
  line-height: 1.3;
}

.relocation-icon {
  flex: 0 0 auto;
  width: 1.25rem;
  height: 1.25rem;
  margin-top: 0.05rem;
  color: var(--warning-500);
}

.relocation-progress-copy {
  display: block;
  margin-top: 0.2rem;
  color: var(--text-secondary);
  font-size: 0.82rem;
}

.relocation-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex: 0 0 auto;
}

.relocation-retry {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  flex: 0 0 auto;
  min-width: 8.25rem;
  white-space: nowrap;
}

.relocation-retry svg {
  width: 1rem;
  height: 1rem;
}

.relocation-progress {
  height: 5px;
  margin-top: 0.9rem;
  overflow: hidden;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.1);
}

.relocation-progress-bar {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--info-600);
  transition: width 0.25s ease;
}

.relocation-state.needs-attention .relocation-progress-bar {
  background: var(--warning-500);
}

.relocation-description,
.relocation-target {
  margin: 0.85rem 0 0;
  color: var(--text-secondary);
  font-size: 0.85rem;
  line-height: 1.5;
}

.relocation-target code {
  color: var(--text-primary, #fff);
  overflow-wrap: anywhere;
}

.relocation-affected {
  margin-top: 0.9rem;
  padding-top: 0.75rem;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
}

.relocation-affected summary {
  width: fit-content;
  cursor: pointer;
  color: var(--text-primary, #fff);
  font-size: 0.85rem;
  font-weight: 500;
  user-select: none;
}

.relocation-affected summary:hover {
  color: var(--warning-500);
}

.relocation-audiobooks {
  display: grid;
  gap: 0.65rem;
  margin-top: 0.75rem;
}

.relocation-audiobook-item {
  padding: 0.65rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 7px;
  background: rgba(0, 0, 0, 0.12);
}

.relocation-audiobook-row {
  display: flex;
  align-items: center;
  gap: 0.65rem;
}

.relocation-audiobook-link {
  display: inline-flex;
  align-items: center;
  padding: 0.3rem 0.55rem;
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 999px;
  background: rgba(0, 0, 0, 0.16);
  color: var(--text-secondary);
  font-size: 0.78rem;
  text-decoration: none;
}

.relocation-audiobook-link:hover {
  border-color: color-mix(in srgb, var(--warning-500) 55%, transparent);
  color: var(--text-primary, #fff);
}

.relocation-audiobook-reason {
  flex: 1 1 auto;
  color: var(--text-secondary);
  font-size: 0.8rem;
  line-height: 1.35;
}

.relocation-review {
  flex: 0 0 auto;
  min-width: 5.5rem;
}

.metadata-repair-details {
  margin-top: 0.7rem;
  padding-top: 0.7rem;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
}

.metadata-repair-details > strong {
  display: block;
  margin-bottom: 0.45rem;
  color: var(--text-primary, #fff);
  font-size: 0.85rem;
}

.metadata-repair-details p {
  margin: 0.45rem 0;
  color: var(--text-secondary);
  font-size: 0.8rem;
  line-height: 1.45;
}

.metadata-repair-collision + .metadata-repair-collision {
  margin-top: 0.8rem;
}

.metadata-repair-collision code,
.metadata-repair-file code {
  overflow-wrap: anywhere;
  color: var(--text-primary, #fff);
}

.metadata-repair-file {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  margin-top: 0.4rem;
  padding: 0.45rem 0.55rem;
  border-radius: 5px;
  background: rgba(0, 0, 0, 0.16);
}

.metadata-repair-file-label {
  display: grid;
  gap: 0.15rem;
  min-width: 0;
}

.metadata-repair-file-label span {
  color: var(--text-secondary);
  font-size: 0.72rem;
}

.metadata-repair-file .btn {
  flex: 0 0 auto;
  font-size: 0.75rem;
}

@media (max-width: 640px) {
  .relocation-header {
    flex-direction: column;
  }

  .relocation-actions {
    width: 100%;
    flex-direction: column;
  }

  .relocation-retry {
    width: 100%;
  }
}

.folder-actions {
  display: flex;
  gap: 0.5rem;
  margin-left: 1rem;
}

/* Override global action ordering for folder cards */
.folder-actions .action-scan {
  order: 1;
}
.folder-actions .action-secondary {
  order: 2;
}
.folder-actions .action-edit {
  order: 3;
}
.folder-actions .action-delete {
  order: 4;
}

/* Use shared .icon-button in src/assets/buttons.css to avoid duplication */

/* Button visuals are centralized in `src/assets/buttons.css`. Use `.btn` and `.btn-primary`.
   If a component needs a small override, use a component-scoped helper class like `.folder-btn`. */
.folder-btn {
  padding: 0.5rem 1rem;
}

/* Modal styles are centralized in `modals.css` */

.modal-close:hover {
  background: #333;
  color: #fff;
}

.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

/* modal-actions and modal delete-button styles are centralized in src/assets/modals.css */
.modal-footer {
  display: flex;
  gap: 0.75rem;
  justify-content: flex-end;
}

/* If this modal needs special sizing for delete buttons in future, add a small override here. */
</style>
