<template>
  <Modal :visible="visible" size="md" @close="$emit('cancel')">
    <template #header>
      <ModalHeader :title="title" @close="$emit('cancel')" :icon="icon" />
    </template>

    <template #default>
      <ModalBody>
          <div class="confirm-description">
            <p>You're updating the audiobook destination. You can update the path only, or choose to move files immediately by selecting "Move files now."</p>
          </div>

          <div class="path-comparison" v-if="pendingMove || pendingRootPath">
            <div class="path-section" v-if="pendingMove && pendingMove.original">
              <div class="path-label">
                <PhArrowRight />
                <span>From:</span>
              </div>
              <div class="path-display"><code>{{ pendingMove?.original }}</code></div>
            </div>

            <div class="path-section">
              <div class="path-label">
                <PhArrowDown />
                <span v-if="pendingMove">To:</span>
                <span v-else>New Root Folder:</span>
              </div>
              <div class="path-display"><code>{{ pendingMove?.combined || pendingRootPath || 'No destination path' }}</code></div>
            </div>

            <!-- Hardlink warning -->
            <div class="hardlink-warning" v-if="showHardlinkWarning && volumeCheckResult?.willBreakHardlinks">
              <PhWarning :size="20" />
              <div class="warning-content">
                <strong>Hardlink Warning</strong>
                <p>Moving files across volumes ({{ volumeCheckResult.sourceVolume }} → {{ volumeCheckResult.destVolume }}) will break hardlinks and create independent copies. The original download will no longer share disk space with the library file.</p>
              </div>
            </div>
          </div>

          <div class="confirm-options">
            <div class="checkbox-row">
              <label class="checkbox-wrapper checkbox-label">
                <input
                  type="checkbox"
                  class="checkbox-input"
                  :checked="moveFiles"
                  @change="onToggleMoveFiles($event)"
                  aria-label="Move files now"
                />
                <div class="checkbox-content">
                  <span class="checkbox-title">Move files now</span>
                  <small>Copy all audiobook files to the new location (recommended)</small>
                </div>
              </label>
            </div>

            <div class="checkbox-row" v-if="moveFiles">
              <label class="checkbox-wrapper checkbox-label">
                <input
                  type="checkbox"
                  class="checkbox-input"
                  :checked="deleteEmpty"
                  @change="onToggleDeleteEmpty($event)"
                  aria-label="Clean up empty folders"
                />
                <div class="checkbox-content">
                  <span class="checkbox-title">Clean up empty folders</span>
                  <small>Delete the original folder if it becomes empty after moving</small>
                </div>
              </label>
            </div>

            <p class="confirm-note">The primary button will <strong>{{ buttonLabel }}</strong> based on the checkbox. Use <strong>Move files now</strong> to perform the move immediately, or leave it unchecked to only update the path.</p>
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

const props = withDefaults(
  defineProps<{
    visible?: boolean
    title?: string
    pendingMove?: { original?: string; combined?: string } | null
    pendingRootPath?: string | null
    moveFiles?: boolean
    deleteEmpty?: boolean
    icon?: Component | undefined
  }>(),
  { visible: false, title: 'Move Audiobook Files', pendingMove: null, pendingRootPath: null, moveFiles: true, deleteEmpty: true, icon: undefined },
)

const emit = defineEmits(['cancel', 'confirm', 'update:moveFiles', 'update:deleteEmpty'])

const volumeCheckResult = ref<{
  sameVolume: boolean
  willBreakHardlinks: boolean
  sourceVolume?: string
  destVolume?: string
  message?: string
} | null>(null)
const showHardlinkWarning = ref(false)

// Check volumes when paths change
watch(
  () => [props.pendingMove?.original, props.pendingMove?.combined, props.visible],
  async () => {
    if (!props.visible || !props.moveFiles) {
      showHardlinkWarning.value = false
      return
    }

    const source = props.pendingMove?.original
    const dest = props.pendingMove?.combined

    if (source && dest) {
      try {
        const result = await apiService.checkVolume(source, dest)
        volumeCheckResult.value = result
        showHardlinkWarning.value = result.willBreakHardlinks
      } catch (error) {
        console.error('Failed to check volume:', error)
        showHardlinkWarning.value = false
      }
    }
  },
  { immediate: true }
)

function onToggleMoveFiles(e: Event) {
  const t = e.target as HTMLInputElement | null
  emit('update:moveFiles', Boolean(t && t.checked))
}
function onToggleDeleteEmpty(e: Event) {
  const t = e.target as HTMLInputElement | null
  emit('update:deleteEmpty', Boolean(t && t.checked))
}

const buttonLabel = computed(() => (props.moveFiles ? 'Move Files' : 'Update Path'))

function onSubmit() {
  emit('confirm', { moveFiles: Boolean(props.moveFiles), deleteEmpty: Boolean(props.deleteEmpty) })
}

</script>

<style scoped>
.confirm-description { padding: 0.5rem 0; color: #cfd8dc }
.path-comparison { display:flex; flex-direction:column; gap:1rem; background:#252526; border-radius:8px; padding:1rem }
.path-section { display:flex; flex-direction:column; gap:0.5rem }
.path-label { display:flex; gap:0.5rem; align-items:center; color:#ddd }
.path-display code { background:#1f1f1f; padding:0.5rem; border-radius:6px; color:#e6eef8 }
.confirm-options { margin-top:0.5rem }
.checkbox-row { margin-top:0.5rem }
.checkbox-label { display:flex; gap:0.75rem; align-items:flex-start; text-align: left; }

.checkbox-content { display:flex; flex-direction:column }
.checkbox-content small { color:#bfc8cc; margin-top:4px }
.checkbox-content .checkbox-title { font-weight: 500; color:#e6eef8 }
.confirm-note { color:#bfc8cc; font-size:0.9rem; margin-top:0.75rem }

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
.hardlink-warning svg { flex-shrink: 0; color: #ffb74d; }
.warning-content { display: flex; flex-direction: column; gap: 0.25rem; }
.warning-content strong { color: #fff; font-size: 0.95rem; }
.warning-content p { margin: 0; color: #ddd; font-size: 0.9rem; line-height: 1.4; }

/* Ensure footer spacing and button emphasis match the app styles */
.modal-footer .cancel-button { min-width: 120px }
.modal-footer .btn { min-width: 120px }
</style>