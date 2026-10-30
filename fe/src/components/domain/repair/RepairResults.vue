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
<!-- Repair's step 2, the repair book by book from the moment it starts: the books ticked for it on
     step 1, one repair at a time, then which repaired ones to organize (all of them at first).
     The box on top says where the repairs are, then what came of them. -->
<template>
  <div class="repair-results" :style="minHeight ? { minHeight: `${minHeight}px` } : undefined">
    <NoticeBox
      :tone="isRunning ? 'progress' : repairedCount ? 'warning' : 'muted'"
      data-testid="repair-results-box"
    >
      <template #icon>
        <PhSpinner v-if="isRunning" class="ph-spin" />
        <PhFolderOpen v-else />
      </template>
      <!-- The box keeps the height of its longest message from the start, so nothing moves when
           the repairs end -->
      <SteadySwap :current-slot="currentMessage" current-test-id="repair-results-message">
        <template #running>
          <strong>Repairing {{ runningPosition }} of {{ booksToRepairCount }}…</strong> The books
          are repaired one after the other; Cancel stops before the next one.
        </template>
        <template #repaired>
          <strong>{{ booksRepairedText(repairedCount) }}</strong
          >{{ organizeQuestion(repairedCount) }}
        </template>
        <template #none>No book was repaired: there is nothing to organize.</template>
        <!-- The longest message these books can bring: every one of them repaired -->
        <template #longest>
          <strong>{{ booksRepairedText(booksToRepairCount) }}</strong
          >{{ organizeQuestion(booksToRepairCount) }}
        </template>
      </SteadySwap>
    </NoticeBox>

    <!-- The books to organize: only repaired ones can be, once the repairs are over -->
    <RepairSelection
      :selected-count="selectedAudiobookIds.length"
      :total-count="repairOutcomes.length"
      action="organize"
      :disabled="isRunning"
      @select-all="selectedAudiobookIds = repairedAudiobookIds"
      @clear="selectedAudiobookIds = []"
    />

    <div class="repair-results-list">
      <RepairResultCard
        v-for="repairOutcome in repairOutcomes"
        :key="repairOutcome.audiobookId"
        :repair-outcome="repairOutcome"
        :is-selected="selectedAudiobookIds.includes(repairOutcome.audiobookId)"
        :is-running="isRunning"
        :data-testid="`repair-result-${repairOutcome.audiobookId}`"
        @toggle="toggle(repairOutcome.audiobookId)"
        @retry="emit('retry', repairOutcome.audiobookId)"
      />
    </div>

    <!-- The room left by step 1 (one book), kept so the dialog keeps its height -->
    <div v-if="minHeight" class="repair-results-room" data-testid="repair-results-room" />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhFolderOpen, PhSpinner } from '@phosphor-icons/vue'
import NoticeBox from '@/components/base/NoticeBox.vue'
import SteadySwap from '@/components/base/SteadySwap.vue'
import RepairResultCard from './RepairResultCard.vue'
import RepairSelection from './RepairSelection.vue'
import { booksRepairedText, organizeQuestion, type RepairOutcome } from '@/utils/repair'

// isRunning: the repairs aren't over yet. minHeight: the height to keep (step 1's, with one book)
const props = defineProps<{
  repairOutcomes: RepairOutcome[]
  isRunning: boolean
  minHeight?: number | null
}>()

// The books to organize: repaired ones only
const selectedAudiobookIds = defineModel<number[]>('selectedAudiobookIds', { required: true })

// Retry: repair that book again
const emit = defineEmits<{ retry: [audiobookId: number] }>()

const repairedAudiobookIds = computed(() =>
  props.repairOutcomes
    .filter((repairOutcome) => repairOutcome.state === 'done')
    .map((repairOutcome) => repairOutcome.audiobookId),
)
const repairedCount = computed(() => repairedAudiobookIds.value.length)
const booksToRepairCount = computed(() => props.repairOutcomes.length)

// The book being repaired, counted from 1 among the books to repair
const runningPosition = computed(
  () =>
    props.repairOutcomes.findIndex((repairOutcome) => repairOutcome.state === 'running') + 1 ||
    booksToRepairCount.value,
)

const currentMessage = computed(() =>
  props.isRunning ? 'running' : repairedCount.value ? 'repaired' : 'none',
)

function toggle(audiobookId: number) {
  selectedAudiobookIds.value = selectedAudiobookIds.value.includes(audiobookId)
    ? selectedAudiobookIds.value.filter((selectedId) => selectedId !== audiobookId)
    : [...selectedAudiobookIds.value, audiobookId]
}
</script>

<style scoped>
.repair-results {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

/* The list's own gap spaces the selection */
.repair-results .repair-group {
  margin-bottom: 0;
}

/* Only the room takes the height kept: everything else keeps its own */
.repair-results > * {
  flex: none;
}

.repair-results > .repair-results-room {
  flex: 1 1 auto;
  min-height: 0;
  border: 1px dashed #333;
  border-radius: 6px;
}

.repair-results-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}
</style>
