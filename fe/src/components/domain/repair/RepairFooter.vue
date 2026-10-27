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
<!-- Repair Books' footer, steps 1 and 2: Cancel (Back once the repairs are over), the bar colours'
     legend and, on step 2, the warning when a repair went wrong, then the skipped count and Repair
     (Organize on step 2). Along the dialog's bottom edge, the repairs' progress. Nothing in it
     appears or goes: every place is kept from the start, so the footer never changes shape. -->
<template>
  <ModalFooter
    ref="footerRef"
    class="repair-footer"
    :class="{ 'repair-footer-legend-above': legendAbove || repairStep }"
    :showCancel="false"
  >
    <template #left>
      <!-- Cancel on step 1. Red while the repairs run: it cancels the books still waiting (the one
           being repaired ends first). Once they are over, Back to step 1, loaded afresh (the books
           may have been repaired) -->
      <button
        ref="cancelRef"
        type="button"
        class="btn cancel-button"
        :class="{ 'btn-danger': executing }"
        :disabled="canceling"
        data-testid="repair-cancel"
        @click="executing ? emit('cancelRepair') : finished ? emit('back') : emit('close')"
      >
        <SteadySwap :current="cancelLabel" inline current-test-id="repair-cancel-label">
          <template #cancel><PhX :size="16" />Cancel</template>
          <template #canceling><PhSpinner :size="16" class="ph-spin" />Canceling…</template>
          <template #back><PhArrowLeft :size="16" />Back</template>
        </SteadySwap>
      </button>
      <!-- Always in sight: between Cancel and the skipped count when the line has room for it, on
           its own line above them otherwise. There from the start, loading included -->
      <MatchLegend
        v-if="!repairStep"
        ref="legendRef"
        :class="{ 'repair-legend-above': legendAbove }"
        :style="legendAbove ? undefined : { marginLeft: `${legendShift}px` }"
      />
      <template v-else>
        <!-- Step 2's bar colours, always on their own line above the buttons: the line of the
             buttons keeps its room for the warning -->
        <MatchLegend
          :entries="REPAIR_LEGEND"
          class="repair-legend-above"
          data-testid="repair-legend"
        />
        <!-- The warning's place, kept from the start of step 2: between Close and the skipped count
             when the line has room for its longest text, on its own line above them otherwise.
             Filled as soon as a book's repair goes wrong (failed, done in part, canceled) -->
        <span
          class="repair-problems"
          :class="{ 'repair-problems-above': problemsAbove }"
          data-testid="repair-problems-slot"
        >
          <template v-if="problemText">
            <PhWarningCircle :size="16" />
            <span data-testid="repair-problems">{{ problemText }}</span>
          </template>
        </span>
        <!-- The longest warning step 2 can show, unseen: what the place is measured with -->
        <span
          ref="problemsSizerRef"
          class="repair-problems repair-problems-sizer"
          aria-hidden="true"
        >
          <PhWarningCircle :size="16" />
          {{ longestRepairProblemText(repairTotal) }}
        </span>
      </template>
    </template>
    <template #default>
      <!-- Always shown, 0 included, from the start: a count that appears and disappears would move
           Repair. Once repaired, the books left out of Organize -->
      <span
        ref="countRef"
        class="repair-count"
        :class="{ 'repair-count-wraps': countWraps }"
        data-testid="repair-count"
      >
        {{ skippedCount }} skipped
      </span>
      <button
        v-if="!repairStep"
        type="button"
        class="btn btn-primary"
        data-testid="repair-confirm"
        :disabled="repairDisabled"
        @click="emit('repair')"
      >
        <PhWrench :size="16" />
        Repair {{ selectedCount }}
      </button>
      <button
        v-else
        type="button"
        class="btn btn-primary"
        data-testid="repair-organize"
        :disabled="executing || !organizeCount"
        :title="
          executing
            ? 'Wait for the repairs to end'
            : organizeCount
              ? undefined
              : 'Tick a repaired book to organize it'
        "
        @click="emit('organize')"
      >
        <PhFolderOpen :size="16" />
        Organize {{ organizeCount }}
      </button>
      <!-- Unseen on step 1 (standby, its room kept), grey from Repair on, then filled from left to
           right as each book's repair ends, breathing until the last one. Blue, orange as soon as a
           repair goes wrong. Once canceled, the books left out count as over, so it fills up when
           the running one ends -->
      <StatusBar
        :tone="problemText ? 'warning' : 'progress'"
        :breathing="executing"
        :standby="!repairStep"
        :percent="repairTotal ? (repairCompleted / repairTotal) * 100 : 0"
        role="progressbar"
        aria-label="Repair progress"
        :aria-valuemax="repairTotal"
        :aria-valuenow="repairCompleted"
        data-testid="repair-progress"
      />
    </template>
  </ModalFooter>
</template>

<script setup lang="ts">
import { computed, ref, type ComponentPublicInstance } from 'vue'
import {
  PhArrowLeft,
  PhFolderOpen,
  PhSpinner,
  PhWarningCircle,
  PhWrench,
  PhX,
} from '@phosphor-icons/vue'
import { ModalFooter } from '@/components/feedback'
import StatusBar from '@/components/base/StatusBar.vue'
import SteadySwap from '@/components/base/SteadySwap.vue'
import MatchLegend, { type LegendEntry } from '@/components/domain/bookMatch/MatchLegend.vue'
import { useRepairFooterLayout } from '@/composables/useRepairFooterLayout'
import { longestRepairProblemText } from '@/utils/repair'

const props = defineProps<{
  // Step 2, from Repair on
  repairStep: boolean
  executing: boolean
  canceling: boolean
  finished: boolean
  repairDisabled: boolean
  selectedCount: number
  organizeCount: number
  skippedCount: number
  // What went wrong on step 2, if anything
  problemText: string | null
  repairCompleted: number
  repairTotal: number
}>()

const emit = defineEmits<{
  close: []
  cancelRepair: []
  back: []
  repair: []
  organize: []
}>()

// What the bar along the bottom of each book means on step 2
const REPAIR_LEGEND: LegendEntry[] = [
  { tone: 'idle', label: 'Waiting or canceled' },
  { tone: 'progress', label: 'Repairing' },
  { tone: 'success', label: 'Repaired' },
  { tone: 'warning', label: 'Repaired in part' },
  { tone: 'error', label: 'Failed' },
]

const footerRef = ref<ComponentPublicInstance | null>(null)
const cancelRef = ref<HTMLElement | null>(null)
const legendRef = ref<ComponentPublicInstance | null>(null)
const problemsSizerRef = ref<HTMLElement | null>(null)
const countRef = ref<HTMLElement | null>(null)
const { legendAbove, legendShift, problemsAbove, countWraps } = useRepairFooterLayout({
  footer: footerRef,
  cancel: cancelRef,
  legend: legendRef,
  problemsSizer: problemsSizerRef,
  count: countRef,
})

// The left button: Cancel, Canceling… while the running repair ends, Back once they are over
const cancelLabel = computed(() =>
  props.canceling ? 'canceling' : props.finished && !props.executing ? 'back' : 'cancel',
)

// The cards take the narrow layout while the legend is above the buttons
defineExpose({ legendAbove })
</script>

<style scoped>
/* Always on one line, unless even Cancel, the count and Repair don't fit one line (measured):
   only then it wraps, centred */
.repair-count {
  font-size: 0.85rem;
  color: #aaa;
  white-space: nowrap;
  text-align: center;
}

.repair-count.repair-count-wraps {
  white-space: normal;
}

/* No room for the legend between Cancel and the skipped count: it takes a line of its own, above
   them */
.modal-footer.repair-footer-legend-above {
  flex-wrap: wrap;
  row-gap: 0.6rem;
}

.repair-footer-legend-above :deep(.modal-footer-left) {
  display: contents;
}

/* Cancel keeps its own width wherever the legend is (modal footers stretch their buttons below
   768px), and so does it in the measure. Set on both layouts, so nothing changes when the legend
   moves: a change here would play the footer buttons' transition, Cancel stretching for a moment */
.repair-footer .cancel-button {
  flex: 0 0 auto;
}

/* Red while the repairs run: the modal footer's grey Cancel would win over .btn-danger */
.modal-footer.repair-footer .cancel-button.btn-danger {
  background-color: #f44336;
  border-color: #f44336;
}

.modal-footer.repair-footer .cancel-button.btn-danger:hover:not(:disabled) {
  background-color: #e53935;
}

/* The progress bar along the dialog's bottom edge, under the footer */
.modal-footer.repair-footer {
  position: relative;
}

.repair-problems {
  flex: 1 1 auto;
  min-width: 0;
  min-height: 1.3em;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.85rem;
  color: var(--status-warning);
}

.repair-legend-above,
.repair-problems-above {
  order: -1;
  flex-basis: 100%;
}

/* Above the buttons, centred on the dialog */
.repair-legend-above {
  justify-content: center;
}

.repair-problems-sizer {
  position: absolute;
  top: 0;
  left: 0;
  visibility: hidden;
  white-space: nowrap;
  pointer-events: none;
  flex: none;
}

/* On a phone the footer stays one line: Cancel on the left, "n skipped" and Repair on the right
   (the shared footer stacks its buttons in a column there). Only the results' three buttons may
   wrap, when they don't fit */
@media (max-width: 480px) {
  .modal-footer.repair-footer {
    flex-direction: row;
    flex-wrap: wrap;
    align-items: center;
    padding: 0.75rem 1rem;
  }

  .repair-footer :deep(.modal-footer-left) {
    margin-bottom: 0;
  }

  .repair-footer :deep(.modal-footer-actions .btn) {
    width: auto;
  }
}
</style>
