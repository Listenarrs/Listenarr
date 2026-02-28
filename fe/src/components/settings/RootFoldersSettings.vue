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
                  </div>
                </div>
              </div>
              <div class="folder-actions">
                <button
                  class="icon-button action-edit"
                  @click="edit(folder)"
                  title="Edit"
                  data-cy="edit-root-folder"
                >
                  <PhPencil />
                </button>
                <button
                  v-if="!folder.isDefault"
                  class="icon-button action-secondary"
                  @click="setDefaultFolder(folder)"
                  title="Set as Default"
                >
                  <PhStar />
                </button>
                <button
                  class="icon-button danger action-delete"
                  @click="confirmDelete(folder)"
                  title="Delete"
                  data-cy="delete-root-folder"
                >
                  <PhTrash />
                </button>
              </div>
            </div>
            <div class="folder-path">
              <PhFolder />
              <code>{{ folder.path }}</code>
            </div>
          </div>
        </div>
      </div>
    </div>

    <RootFolderFormModal
      v-if="showForm"
      :root="editingRoot"
      @close="close"
      @saved="onSaved"
    />

    <!-- Delete Root Folder Confirmation (shared) -->
    <DeleteConfirmationModal :visible="!!folderToDelete" title="Delete Root Folder" @close="folderToDelete = null" @confirm="executeDeleteFolder">
      <template v-slot>
        <p>
          Are you sure you want to delete the root folder
          <strong>{{ folderToDelete?.name }}</strong>?
        </p>
        <p>This will only remove the reference and will not delete files from disk.</p>
      </template>
    </DeleteConfirmationModal>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import RootFolderFormModal from '@/components/settings/RootFolderFormModal.vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { Pill } from '@/components/base'
import {
  PhPlus,
  PhFolder,
  PhPencil,
  PhTrash,
  PhSpinner,
  PhFolderOpen,
  PhWarningCircle,
  PhX,
  PhStar,
} from '@phosphor-icons/vue'
import type { RootFolder } from '@/types'

interface Props {
  hideHeader?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  hideHeader: false,
})

const store = useRootFoldersStore()
const showForm = ref(false)
const editing = ref<{ id?: number; name: string; path: string } | null>(null)
import { computed } from 'vue'
const editingRoot = computed(() => editing.value as RootFolder | undefined)
const toast = useToast()

onMounted(async () => {
  await store.load()
})

function openAdd() {
  editing.value = null
  showForm.value = true
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
  if (!folder.id) return
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

.folder-actions {
  display: flex;
  gap: 0.5rem;
  margin-left: 1rem;
}

/* Override global action ordering for folder cards so Edit sits next to Delete */
.folder-actions .action-secondary { order: 1 }
.folder-actions .action-edit { order: 2 }
.folder-actions .action-delete { order: 3 }

/* Use shared .icon-button in src/assets/buttons.css to avoid duplication */

/* Button visuals are centralized in `src/assets/buttons.css`. Use `.btn` and `.btn-primary`.
   If a component needs a small override, use a component-scoped helper class like `.folder-btn`. */
.folder-btn { padding: 0.5rem 1rem; } 

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
.modal-footer { display:flex; gap:0.75rem; justify-content:flex-end }

/* If this modal needs special sizing for delete buttons in future, add a small override here. */
</style>
