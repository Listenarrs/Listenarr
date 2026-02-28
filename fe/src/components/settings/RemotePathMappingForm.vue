<template>
  <div class="remote-path-mapping-form">
    <div class="form-header">
      <h4>
        <component :is="isEdit ? PhPencil : PhPlus" />
        {{ isEdit ? 'Edit' : 'Add' }} Path Mapping
      </h4>
    </div>

    <form @submit.prevent="handleSubmit">
      <div v-if="error" class="error-banner">
        <PhWarningCircle />
        <span>{{ error }}</span>
      </div>

      <FormRow label="Name (Optional)" help="Friendly name to identify this mapping">
        <input id="name" v-model="formData.name" type="text" placeholder="e.g., Docker to Host Mapping" class="form-control" />
      </FormRow>

      <FormRow label="Remote Path" help="Path as seen by the download client (in its Docker container or remote system)">
        <div class="input-with-icon">
          <PhDesktop />
          <input id="remotePath" v-model="formData.remotePath" type="text" placeholder="/path/to/downloads" class="form-control" required />
        </div>
      </FormRow>

      <FormRow label="Local Path" help="Path as seen by Listenarr (on this system where Listenarr is running)">
        <div class="input-with-icon">
          <PhFolderOpen />
          <input id="localPath" v-model="formData.localPath" type="text" placeholder="/mnt/media/audiobooks" class="form-control" required />
        </div>
      </FormRow>

      <div class="form-actions">
        <button type="button" class="btn btn-secondary" @click="handleCancel">
          <PhX />
          Cancel
        </button>
        <button type="submit" class="btn btn-primary" :disabled="!isValid || loading">
          <PhSpinner v-if="loading" class="ph-spin" />
          <PhCheck v-else />
          <span v-if="loading">Saving...</span>
          <span v-else>{{ isEdit ? 'Update' : 'Save' }}</span>
        </button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import FormRow from '@/components/settings/FormRow.vue'
import type { RemotePathMapping } from '@/types'
import { PhPencil, PhPlus, PhWarningCircle, PhDesktop, PhFolderOpen, PhX, PhSpinner, PhCheck } from '@phosphor-icons/vue' 

interface Props {
  downloadClientId: string
  mapping?: RemotePathMapping | null
}

interface Emits {
  (e: 'save', mapping: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>): void
  (e: 'cancel'): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const formData = ref({
  name: '',
  remotePath: '',
  localPath: '',
})

const loading = ref(false)
const error = ref<string | null>(null)

const isEdit = computed(() => !!props.mapping)

const isValid = computed(() => {
  return formData.value.remotePath.trim().length > 0 && formData.value.localPath.trim().length > 0
})

// Load existing mapping data when in edit mode
watch(
  () => props.mapping,
  (mapping) => {
    if (mapping) {
      formData.value.name = mapping.name || ''
      formData.value.remotePath = mapping.remotePath
      formData.value.localPath = mapping.localPath
    } else {
      formData.value.name = ''
      formData.value.remotePath = ''
      formData.value.localPath = ''
    }
  },
  { immediate: true },
)

const handleSubmit = () => {
  if (!isValid.value) return

  error.value = null
  loading.value = true

  try {
    emit('save', {
      downloadClientId: props.downloadClientId,
      name: formData.value.name.trim() || undefined,
      remotePath: formData.value.remotePath.trim(),
      localPath: formData.value.localPath.trim(),
    })
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to save mapping'
  } finally {
    loading.value = false
  }
}

const handleCancel = () => {
  emit('cancel')
}
</script>

<style scoped>
.remote-path-mapping-form {
  background-color: #222;
  padding: 1.5rem;
  border-radius: 6px;
  border: 1px solid #444;
}

.form-header {
  margin-bottom: 1.5rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid #444;
}

.form-header h4 {
  margin: 0;
  color: #fff;
  font-size: 1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.form-header i {
  font-size: 1.1rem;
  color: var(--brand-500);
}

.error-banner {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  margin-bottom: 1rem;
  background-color: rgba(220, 53, 69, 0.1);
  border: 1px solid rgba(220, 53, 69, 0.3);
  border-radius: 6px;
  color: #ff6b7a;
}

.error-banner i {
  font-size: 1.25rem;
  flex-shrink: 0;
}

.form-group {
  margin-bottom: 1.5rem;
}

.form-group:last-of-type {
  margin-bottom: 0;
}

label {
  display: block;
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
  font-size: 0.95rem;
}

label.required::after {
  content: ' *';
  color: #dc3545;
}

.input-with-icon {
  position: relative;
  display: flex;
  align-items: center;
}

.input-with-icon i {
  position: absolute;
  left: 0.75rem;
  color: #999;
  font-size: 1.1rem;
  pointer-events: none;
}

.input-with-icon .form-control {
  padding-left: 2.5rem;
}

.form-control {
  width: 100%;
  padding: 0.75rem;
  font-size: 0.95rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  transition: all 0.2s;
}

.form-control:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.form-control::placeholder {
  color: #666;
}

.help-text {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #999;
  line-height: 1.4;
}

.form-actions {
  display: flex;
  gap: 0.75rem;
  justify-content: flex-end;
  margin-top: 1.5rem;
  padding-top: 1.5rem;
  border-top: 1px solid #444;
}

/* Use centralized `.btn` styles in `src/assets/buttons.css`. Local components may apply `.btn-primary` / `.cancel-button` as needed. */

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn i {
  font-size: 1.1rem;
}

/* Button color variants centralized in `src/assets/modals.css` - use `.btn` / `.btn-primary` */

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

@media (max-width: 768px) {
  .form-actions {
    flex-direction: column-reverse;
  }

  .btn {
    width: 100%;
    justify-content: center;
  }
}
</style>
