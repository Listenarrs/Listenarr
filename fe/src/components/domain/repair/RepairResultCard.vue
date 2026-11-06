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
<!-- One book on Repair's step 2: it waits in a loading state for its turn; the bar along its bottom
     breathes in blue while it is repaired, then turns green, orange or red by how it ended. It
     shows the record it is to get or got, the parts that change in green; once repaired it can be
     ticked to be organized. A failed repair, or one done in part, offers Retry. -->
<template>
  <div
    class="form-control-card repair-result"
    :class="`repair-result-${repairOutcome.state}`"
    :data-result-state="repairOutcome.state"
  >
    <div class="repair-result-header">
      <label
        class="repair-result-heading"
        :title="
          repairOutcome.state === 'done' ? undefined : 'Only a repaired book can be organized'
        "
      >
        <input
          type="checkbox"
          class="repair-result-checkbox"
          data-testid="repair-result-include"
          :checked="isSelected"
          :disabled="repairOutcome.state !== 'done'"
          @change="emit('toggle')"
        />
        <span class="repair-result-title">{{
          repairOutcome.title || `Audiobook #${repairOutcome.audiobookId}`
        }}</span>
      </label>
      <!-- The records, closed at first to keep the list short; the tooltip gives them without
           opening (the open details mark what changes) -->
      <button
        type="button"
        class="btn btn-secondary repair-result-details-btn"
        :title="recordsText"
        :aria-expanded="isDetailsOpen"
        data-testid="repair-result-details"
        @click="isDetailsOpen = !isDetailsOpen"
      >
        <SteadySwap
          :current-slot="isDetailsOpen ? 'hide' : 'show'"
          is-inline
          gap="0.35rem"
          current-test-id="repair-result-details-label"
        >
          <template #show>
            Show details
            <PhCaretDown :size="14" />
          </template>
          <template #hide>
            Hide details
            <PhCaretUp :size="14" />
          </template>
        </SteadySwap>
      </button>
    </div>
    <!-- Two lines under the title from the start, so the row keeps its height: the record the book
         points at (the one it is to get while it waits, the one it got once repaired, its old one
         when the repair failed or was canceled), then where its repair is or how it ended -->
    <p class="repair-result-record" data-testid="repair-result-record">
      <BookRecordDiff
        :record="newBookRecord ?? repairOutcome.recordBefore"
        :base-record="repairOutcome.recordBefore"
      />
    </p>
    <p class="repair-result-message" data-testid="repair-result-message">
      <PhSpinner
        v-if="repairOutcome.state === 'waiting' || repairOutcome.state === 'running'"
        class="ph-spin"
        :size="14"
        data-testid="repair-result-loading"
      />
      {{ repairOutcome.message }}
    </p>
    <!-- At the row's bottom right, laid over the row's room (the status line always leaves it
         free), so showing it moves nothing -->
    <button
      v-if="repairOutcome.state === 'failed' || repairOutcome.state === 'warning'"
      type="button"
      class="btn btn-secondary repair-result-retry"
      :disabled="isRunning"
      :title="isRunning ? 'Wait for the repair running to end' : 'Repair this book again'"
      data-testid="repair-result-retry"
      @click="emit('retry')"
    >
      <PhArrowCounterClockwise :size="14" />
      Retry
    </button>
    <dl v-if="isDetailsOpen" class="repair-result-records">
      <dt>Old</dt>
      <dd data-testid="repair-result-old">
        <BookRecordDiff
          v-if="newBookRecord"
          :record="repairOutcome.recordBefore"
          :base-record="newBookRecord"
          is-replaced
        />
        <template v-else>{{ bookSummary(repairOutcome.recordBefore) || 'None' }}</template>
      </dd>
      <dt>New</dt>
      <dd v-if="newBookRecord" data-testid="repair-result-new">
        <BookRecordDiff :record="newBookRecord" :base-record="repairOutcome.recordBefore" />
      </dd>
      <dd v-else data-testid="repair-result-new">None: the book keeps its old record</dd>
    </dl>
    <StatusBar
      :tone="BAR_TONE_BY_STATE[repairOutcome.state]"
      :is-breathing="repairOutcome.state === 'running'"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { PhArrowCounterClockwise, PhCaretDown, PhCaretUp, PhSpinner } from '@phosphor-icons/vue'
import StatusBar, { type StatusTone } from '@/components/base/StatusBar.vue'
import SteadySwap from '@/components/base/SteadySwap.vue'
import BookRecordDiff from '@/components/domain/bookMatch/BookRecordDiff.vue'
import { bookSummary, type BookTaskState } from '@/utils/bookMatch'
import { newRecord, type RepairOutcome } from '@/utils/repair'

const props = defineProps<{
  repairOutcome: RepairOutcome
  // Ticked to be organized
  isSelected: boolean
  // The repairs aren't over yet
  isRunning: boolean
}>()

const emit = defineEmits<{
  toggle: []
  // Repair this book again
  retry: []
}>()

// Grey while it waits or when it isn't repaired, blue while it is repaired, then its outcome
const BAR_TONE_BY_STATE: Record<BookTaskState, StatusTone> = {
  waiting: 'idle',
  running: 'progress',
  done: 'success',
  warning: 'warning',
  failed: 'error',
  skipped: 'idle',
}

// Its records shown
const isDetailsOpen = ref(false)

const newBookRecord = computed(() => newRecord(props.repairOutcome))

// The records on two lines, for the details button's tooltip
const recordsText = computed(() => {
  const newRecordText = newBookRecord.value
    ? bookSummary(newBookRecord.value)
    : 'None: the book keeps its old record'
  return `Old: ${bookSummary(props.repairOutcome.recordBefore) || 'None'}\nNew: ${newRecordText}`
})
</script>

<style scoped>
/* Room at the bottom for the state bar */
.form-control-card.repair-result {
  position: relative;
  overflow: hidden;
  padding-bottom: calc(1rem + 3px);
}

.repair-result-skipped {
  opacity: 0.6;
}

.repair-result-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.repair-result-details-btn {
  /* Its own width on a phone too, where modal buttons stretch */
  width: auto;
  white-space: nowrap;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  flex: none;
  padding: 0.25rem 0.6rem;
  font-size: 0.8rem;
}

.repair-result-retry {
  position: absolute;
  right: 1rem;
  bottom: calc(0.55rem + 3px);
  /* Its own width on a phone too, where modal buttons stretch */
  width: auto;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.2rem 0.6rem;
  font-size: 0.8rem;
  white-space: nowrap;
}

.repair-result-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
}

.repair-result-title {
  color: #e0e0e0;
  font-weight: 500;
}

/* The record the book points at, drawn like New: grey, what changes in green */
.repair-result-record {
  margin: 0.4rem 0 0;
  font-size: 0.85rem;
  color: #aaa;
}

.repair-result-message {
  /* Room always left free on the right for Retry */
  padding-right: 6.5rem;
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0.4rem 0 0;
  font-size: 0.85rem;
  color: #aaa;
}

.repair-result-done .repair-result-message {
  color: var(--status-success);
}

.repair-result-warning .repair-result-message {
  color: var(--status-warning);
}

.repair-result-failed .repair-result-message {
  color: var(--status-error);
}

.repair-result-records {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 0.3rem 0.75rem;
  margin: 0.6rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

.repair-result-records dd {
  margin: 0;
  min-width: 0;
}
</style>
