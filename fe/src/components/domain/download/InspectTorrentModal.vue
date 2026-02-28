<template>
  <Modal :visible="true" size="sm" @close="close">
    <template #header>
      <ModalHeader :title="'Inspect Cached Torrent'" @close="close" />
    </template>

    <template #default>
      <ModalBody>
        <div v-if="loading">Loading…</div>
        <div v-else>
          <div v-if="announces && announces.length">
            <h4>Announces</h4>
            <ul>
              <li v-for="a in announces" :key="a">{{ a }}</li>
            </ul>
          </div>
          <div v-else>
            <p>No cached announces available.</p>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <button @click="downloadTorrent" :disabled="loading || !hasStoredTorrent" class="btn btn-primary"><PhDownload /> Download Torrent</button>
      <button @click="close" class="cancel-button btn"><PhX /> Close</button>
    </template>
  </Modal>
</template> 

<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import { PhX, PhDownload } from '@phosphor-icons/vue' 

const props = defineProps<{
  downloadId: string
  initialAnnounces?: string[] | null
}>()

const emits = defineEmits(['close'])

const loading = ref(false)
const announces = ref<string[] | null>(props.initialAnnounces ?? null)
let cachedTorrent: { blob: Blob; filename?: string } | null = null

watch(
  () => props.downloadId,
  async (id) => {
    if (!id) return
    loading.value = true
    try {
      const r = await apiService.getCachedAnnounces(id)
      announces.value = r?.announces ?? null

      // Pre-fetch torrent blob so download is instant
      cachedTorrent = await apiService.getCachedTorrent(id)
    } catch (e) {
      logger.warn('Failed to fetch cached torrent/announces', e)
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

function close() {
  emits('close')
}

const hasStoredTorrent = computed(() => !!cachedTorrent?.blob)

function downloadTorrent() {
  if (!cachedTorrent) return
  const url = URL.createObjectURL(cachedTorrent.blob)
  const a = document.createElement('a')
  a.href = url
  a.download = cachedTorrent.filename ?? `download-${props.downloadId}.torrent`
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}
</script>

<style scoped>
/* Modal-specific styling moved to shared `modals.css` */
.modal-body { min-height: 120px; }
.close { background: none; border: none; font-size: 1.2rem; }
</style>
