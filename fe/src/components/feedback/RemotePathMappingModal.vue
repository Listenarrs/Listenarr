<template>
  <Modal :visible="visible" size="md" @close="closeModal">
    <template #header>
      <ModalHeader :title="(editingMapping ? 'Edit' : 'Add') + ' Remote Path Mapping'" :icon="PhLink" @close="closeModal" />
    </template>

    <template #default>
      <ModalForm @submit="handleSubmit">
        <ModalBody>
          <h3 class="modal-section-title"><PhLink /> Configuration</h3>

          <!-- Use modal body styling for these fields rather than the FormSection card -->
          <div class="modal-section-body">
            <div class="section-card">
              <FormField
                id="mapping-name"
                label="Mapping Name"
                help="Friendly name to identify this mapping"
              >
                <input
                  id="mapping-name"
                  v-model="formData.name"
                  type="text"
                  placeholder="Friendly name for this mapping"
                  class="form-control"
                />
              </FormField>

              <FormField
                id="download-client"
                label="Download Client"
              >
                <select id="download-client" v-model="formData.downloadClientId" class="form-control">
                  <option v-for="c in downloadClients" :key="c.id" :value="c.id">{{ c.name }}</option>
                </select>
              </FormField>

              <FormField
                id="remote-path"
                label="Remote Path"
                help="Path as seen by the download client (in its Docker container or remote system)"
                required
              >
                <div class="input-with-icon">
                  <input
                    id="remote-path"
                    v-model="formData.remotePath"
                    type="text"
                    placeholder="/path/to/complete/downloads"
                    class="form-control"
                    required
                  />
                </div>
              </FormField>

              <FormField
                id="local-path"
                label="Local Path"
                help="Path as seen by Listenarr (on this system where Listenarr is running)"
                required
              >
                <FolderBrowser v-model="formData.localPath" :inline="true" @open-modal="showBrowser = true" />
                <FolderBrowserModal v-model:visible="showBrowser" v-model:modelValue="formData.localPath" :show-input="true" :show-files="false" @close="showBrowser = false" />
              </FormField>
            </div>
          </div>
        </ModalBody>
      </ModalForm>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button class="cancel-button btn" @click="closeModal()"><PhX /> Cancel</button>
        </template>
        <template #default>
          <button type="submit" form="modal-form" class="btn btn-primary"><PhCheck /> Save</button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { PhX, PhCheck, PhLink, PhDesktop } from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalFooter, ModalForm, ModalBody } from '@/components/feedback'
import { FormField } from '@/components/base'
import FolderBrowser from '@/components/ui/FolderBrowser.vue'
import FolderBrowserModal from '@/components/feedback/FolderBrowserModal.vue'
import type { DownloadClientConfiguration, RemotePathMapping } from '@/types'

interface Props {
  visible: boolean
  editingMapping: RemotePathMapping | null
  downloadClients: DownloadClientConfiguration[]
}

interface Emits {
  (e: 'close'): void
  (e: 'save', mapping: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const formData = ref({
  name: '',
  downloadClientId: '',
  remotePath: '',
  localPath: '',
})

const showBrowser = ref(false)

// Watch for prop changes to populate form data
watch(() => props.editingMapping, (newMapping) => {
  if (newMapping) {
    formData.value = {
      name: newMapping.name || '',
      downloadClientId: newMapping.downloadClientId || '',
      remotePath: newMapping.remotePath || '',
      localPath: newMapping.localPath || '',
    }
  } else {
    // Reset form for new mapping
    formData.value = {
      name: '',
      downloadClientId: '',
      remotePath: '',
      localPath: '',
    }
  }
}, { immediate: true })

const handleSubmit = () => {
  emit('save', formData.value)
}

const closeModal = () => {
  emit('close')
}
</script>