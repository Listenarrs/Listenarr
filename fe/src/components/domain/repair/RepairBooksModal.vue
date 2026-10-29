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
      <ModalHeader :title="repairStepTitle(isRepairStep)" :icon="PhWrench" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="repair-modal-body">
        <div v-if="isLoading" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Loading the selected audiobooks…</p>
        </div>

        <div v-else-if="loadError" class="repair-state repair-error">
          <PhWarningCircle class="repair-icon" />
          <p>{{ loadError }}</p>
        </div>

        <!-- From Repair on: each book's repair as it runs, then how it ended and which ones to
             organize -->
        <RepairResults
          v-else-if="isRepairStep"
          v-model:selected-audiobook-ids="audiobookIdsToOrganize"
          :repair-outcomes="repairOutcomes"
          :is-running="isExecuting"
          :min-height="stepOneHeight"
          @retry="retryRepair"
        />

        <div v-else-if="repairRows.length" ref="stepOneRef" class="repair-list-wrapper">
          <NoticeBox>
            <template #icon><PhInfo /></template>
            <p>Find the right Audible record for each book, then repair.</p>
            <p data-testid="repair-info-asin">
              A filled ASIN is searched on its own: the title and author are left out of that
              search.
            </p>
          </NoticeBox>

          <RepairSelection
            :selected-count="selectedCount"
            :total-count="repairRows.length"
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
                v-for="repairRow in repairRows"
                :key="repairRow.audiobookId"
                v-model:book-match="repairRow.bookMatch"
                :configured-region="configuredRegion"
                :is-search-locked="
                  searchingAudiobookId !== null && searchingAudiobookId !== repairRow.audiobookId
                "
                :is-excluded="!repairRow.isSelected"
                :is-narrow="footerRef?.isLegendAbove ?? false"
                :should-keep-results-room="repairRows.length === 1"
                :book="{
                  title: repairRow.title,
                  subtitle: repairRow.subtitle,
                  authors: repairRow.authors,
                }"
                :data-testid="`repair-row-${repairRow.audiobookId}`"
                @searching="(isSearching) => onSearching(repairRow, isSearching)"
                @matched="repairRow.isSelected = true"
              >
                <template #heading>
                  <label
                    class="repair-heading"
                    :title="isMatched(repairRow) ? undefined : 'Find its new record first'"
                  >
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      data-testid="repair-include"
                      :checked="isIncluded(repairRow)"
                      :disabled="!isMatched(repairRow)"
                      @change="repairRow.isSelected = !repairRow.isSelected"
                    />
                    <span class="repair-title">{{
                      repairRow.title || `Audiobook #${repairRow.audiobookId}`
                    }}</span>
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
        :is-repair-step="isRepairStep"
        :is-executing="isExecuting"
        :is-canceling="isCanceling"
        :is-finished="isFinished"
        :is-repair-disabled="isLoading || searchingAudiobookId !== null || selectedCount === 0"
        :selected-count="selectedCount"
        :organize-count="audiobookIdsToOrganize.length"
        :skipped-count="skippedCount"
        :problem-text="problemText"
        :completed-repair-count="completedRepairCount"
        :total-repair-count="totalRepairCount"
        @close="handleClose"
        @cancel-repair="cancelRepair"
        @back="load"
        @repair="startRepair"
        @organize="emit('organize', audiobookIdsToOrganize)"
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
import { usePageLeaveGuard } from '@/composables/usePageLeaveGuard'
import { showConfirm } from '@/composables/confirmService'
import { SEARCH_CACHE } from '@/utils/bookMatch'
import { pixels } from '@/utils/measure'
import { isIncluded, isMatched, repairStepTitle } from '@/utils/repair'

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
    // Shown again after its step 3 (Back from Organize): as it was left, not loaded afresh
    isResumed?: boolean
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
    isResumed: false,
  },
)

const emit = defineEmits<{
  close: []
  done: []
  organize: [audiobookIds: number[]]
}>()

const {
  searchCache,
  configuredRegion,
  isLoading,
  loadError,
  repairRows,
  isExecuting,
  isFinished,
  isCanceling,
  hasLibraryChanged,
  completedRepairCount,
  totalRepairCount,
  audiobookIdsToOrganize,
  searchingAudiobookId,
  isRepairStep,
  selectedCount,
  skippedCount,
  problemText,
  repairOutcomes,
  load,
  forgetLoad,
  onSearching,
  repair,
  retryRepair,
  pauseRepair,
  resumeRepair,
  cancelRepair,
  selectAll,
  clearSelection,
} = useRepairBooks(() => props.audiobookIds)
provide(SEARCH_CACHE, searchCache)

// While the repairs run, leaving the page asks first (the dialog itself can't be closed then).
// The repairs hold while it asks: the one being repaired ends, the next one doesn't start. Stay
// goes on with them; Leave cancels the books still waiting, as Cancel does
usePageLeaveGuard({
  isActive: isExecuting,
  confirmLeave: async () => {
    pauseRepair()
    const isLeaving = await showConfirm(
      'The book being repaired ends its repair, the ones still waiting are not repaired.',
      'Leave while books are repaired?',
      { confirmText: 'Leave', cancelText: 'Stay', danger: true },
    )
    if (!isLeaving) resumeRepair()
    return isLeaving
  },
  onLeave: cancelRepair,
})

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
  const bodyElement = stepOneRef.value?.closest<HTMLElement>('.repair-modal-body')
  const footerElement = footerRef.value?.$el as HTMLElement | undefined
  if (repairRows.value.length !== 1 || !bodyElement || !footerElement) return async () => {}
  const bodyStyle = getComputedStyle(bodyElement)
  const bodyRoom =
    bodyElement.clientHeight - pixels(bodyStyle.paddingTop) - pixels(bodyStyle.paddingBottom)
  const footerHeightBefore = footerElement.offsetHeight
  return async () => {
    await nextTick()
    const keptHeight = bodyRoom - (footerElement.offsetHeight - footerHeightBefore)
    stepOneHeight.value = keptHeight > 0 ? keptHeight : null
  }
}

function startRepair() {
  void repair(keepStepOneRoom())
}

// Closing stops the running search: the cards go away with the modal
function handleClose() {
  if (isExecuting.value) return
  if (isFinished.value || hasLibraryChanged.value) emit('done')
  else emit('close')
}

watch(
  () => props.visible,
  (visible) => {
    // Closed while its books load: their answers fill nothing
    if (!visible) {
      forgetLoad()
      return
    }
    if (props.isResumed) return
    hasLibraryChanged.value = false
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
