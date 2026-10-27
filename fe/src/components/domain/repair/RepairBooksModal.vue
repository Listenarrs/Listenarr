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
<!-- Repair Books, steps 1 and 2 of three: each book's new record picked (Matching), then the
     repairs one after the other and their results (Repair process), to choose what to organize
     (step 3, in the Organize dialog it opens). -->
<template>
  <Modal :visible="visible" size="lg" @close="handleClose">
    <template #header>
      <!-- The step on the title, so the dialog says where the repair is and how much is left -->
      <ModalHeader :title="repairStepTitle(repairStep)" :icon="PhWrench" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="repair-modal-body">
        <div v-if="loading" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Loading the selected audiobooks…</p>
        </div>

        <div v-else-if="error" class="repair-state repair-error">
          <PhWarningCircle class="repair-icon" />
          <p>{{ error }}</p>
        </div>

        <!-- From Repair on: each book's repair as it runs, then how it ended and which ones to
             organize -->
        <RepairResults
          v-else-if="repairStep"
          v-model:selected="organizeIds"
          :outcomes="outcomes"
          :running="executing"
          :min-height="stepOneHeight"
          @retry="retryRepair"
        />

        <div v-else-if="rows.length" ref="stepOneRef" class="repair-list-wrapper">
          <NoticeBox>
            <template #icon><PhInfo /></template>
            <p>Find the right Audible record for each book, then repair.</p>
            <p data-testid="repair-info-asin">
              A filled ASIN is searched on its own: the title and author are left out of that
              search.
            </p>
          </NoticeBox>

          <RepairSelection
            :selected="selectedCount"
            :total="rows.length"
            action="repair"
            @select-all="selectAll"
            @clear="clearSelection"
          />

          <div class="form-group repair-group">
            <label class="form-label">
              <PhWrench />
              Books
            </label>
            <div class="repair-list">
              <BookMatchCard
                v-for="row in rows"
                :key="row.id"
                v-model:match="row.match"
                :configured-region="configuredRegion"
                :search-locked="searchingRowId !== null && searchingRowId !== row.id"
                :excluded="!row.selected"
                :narrow="footerRef?.legendAbove ?? false"
                :placeholders="rows.length === 1"
                :book="{ title: row.title, subtitle: row.subtitle, authors: row.authors }"
                :data-testid="`repair-row-${row.id}`"
                @searching="(searching) => onSearching(row, searching)"
                @matched="row.selected = true"
              >
                <template #heading>
                  <label
                    class="repair-heading"
                    :title="isMatched(row) ? undefined : 'Find its new record first'"
                  >
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      data-testid="repair-include"
                      :checked="isIncluded(row)"
                      :disabled="!isMatched(row)"
                      @change="row.selected = !row.selected"
                    />
                    <span class="repair-title">{{ row.title || `Audiobook #${row.id}` }}</span>
                  </label>
                </template>
              </BookMatchCard>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <!-- The attribute tells Modal its footer is given: no footer box of its own around it -->
      <RepairFooter
        ref="footerRef"
        data-modal-footer="true"
        :repair-step="repairStep"
        :executing="executing"
        :canceling="canceling"
        :finished="finished"
        :repair-disabled="loading || searchingRowId !== null || selectedCount === 0"
        :selected-count="selectedCount"
        :organize-count="organizeIds.length"
        :skipped-count="skippedCount"
        :problem-text="problemText"
        :repair-completed="repairCompleted"
        :repair-total="repairTotal"
        @close="handleClose"
        @cancel-repair="cancelRepair"
        @back="load"
        @repair="startRepair"
        @organize="emit('organize', organizeIds)"
      />
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { nextTick, provide, ref, watch } from 'vue'
import { PhInfo, PhSpinner, PhWarningCircle, PhWrench } from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalHeader } from '@/components/feedback'
import NoticeBox from '@/components/base/NoticeBox.vue'
import BookMatchCard from '@/components/domain/bookMatch/BookMatchCard.vue'
import RepairFooter from './RepairFooter.vue'
import RepairResults from './RepairResults.vue'
import RepairSelection from './RepairSelection.vue'
import { useRepairBooks } from '@/composables/useRepairBooks'
import { SEARCH_CACHE } from '@/utils/bookMatch'
import { px } from '@/utils/measure'
import { isIncluded, isMatched, repairStepTitle } from '@/utils/repair'

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
    // Shown again after its step 3 (Back from Organize): as it was left, not loaded afresh
    resume?: boolean
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
    resume: false,
  },
)

const emit = defineEmits<{
  close: []
  done: []
  organize: [ids: number[]]
}>()

const {
  searchCache,
  configuredRegion,
  loading,
  error,
  rows,
  executing,
  finished,
  canceling,
  libraryChanged,
  repairCompleted,
  repairTotal,
  organizeIds,
  searchingRowId,
  repairStep,
  selectedCount,
  skippedCount,
  problemText,
  outcomes,
  load,
  onSearching,
  repair,
  retryRepair,
  cancelRepair,
  selectAll,
  clearSelection,
} = useRepairBooks(() => props.audiobookIds)
provide(SEARCH_CACHE, searchCache)

const footerRef = ref<InstanceType<typeof RepairFooter> | null>(null)
// One book: the room step 1 took in the dialog's body, measured on Repair, kept by step 2 so the
// dialog keeps its height
const stepOneRef = ref<HTMLElement | null>(null)
const stepOneHeight = ref<number | null>(null)

// Measures step 1 before it goes; the returned function, called once step 2 shows, keeps its room.
// The body's visible height (capped by its maximum, step 1 often scrolls), less its padding and
// what step 2's footer takes more (its legend on a line of its own)
function keepStepOneRoom(): () => Promise<void> {
  stepOneHeight.value = null
  const body = stepOneRef.value?.closest<HTMLElement>('.repair-modal-body')
  const footer = footerRef.value?.$el as HTMLElement | undefined
  if (rows.value.length !== 1 || !body || !footer) return async () => {}
  const style = getComputedStyle(body)
  const room = body.clientHeight - px(style.paddingTop) - px(style.paddingBottom)
  const footerBefore = footer.offsetHeight
  return async () => {
    await nextTick()
    const kept = room - (footer.offsetHeight - footerBefore)
    stepOneHeight.value = kept > 0 ? kept : null
  }
}

function startRepair() {
  void repair(keepStepOneRoom())
}

// Closing stops the running search: the cards go away with the modal
function handleClose() {
  if (executing.value) return
  if (finished.value || libraryChanged.value) emit('done')
  else emit('close')
}

watch(
  () => props.visible,
  (visible) => {
    if (!visible || props.resume) return
    libraryChanged.value = false
    void load()
  },
  { immediate: true },
)
</script>

<style scoped>
.repair-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 2rem 1rem;
  text-align: center;
}

.repair-icon {
  font-size: 2rem;
}

.repair-error {
  color: #ef9a9a;
}

.repair-group {
  margin-bottom: 1rem;
}

.repair-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.repair-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
}
</style>
