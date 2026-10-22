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
<!-- The outcome of a repair, book by book: every book of the dialog, repaired or not. A repaired
     book shows its record before and after, the parts that changed in green, and can be ticked to
     be organized (ticked at first); the others say why they weren't repaired, and can't be. -->
<template>
  <div class="repair-results">
    <div
      class="repair-results-message"
      :class="{ 'repair-results-message-none': !repairedCount }"
      data-testid="repair-results-message"
    >
      <PhFolderOpen />
      <p v-if="repairedCount">
        <strong>{{ repairedCount }} {{ repairedCount === 1 ? 'book' : 'books' }} repaired</strong>,
        {{ repairedCount === 1 ? 'its' : 'their' }} metadata refreshed from the new Audible record.
        Organize {{ repairedCount === 1 ? 'it' : 'them' }} now?
        {{ repairedCount === 1 ? 'Its files were' : 'Their files were' }} last organized with the
        metadata from before, which may have been wrong.
      </p>
      <p v-else>No book was repaired: there is nothing to organize.</p>
    </div>

    <div class="repair-results-list">
      <div
        v-for="outcome in outcomes"
        :key="outcome.id"
        class="form-control-card repair-result"
        :class="`repair-result-${outcome.state}`"
        :data-result-state="outcome.state"
        :data-testid="`repair-result-${outcome.id}`"
      >
        <label
          class="repair-result-heading"
          :title="outcome.state === 'done' ? undefined : 'Only a repaired book can be organized'"
        >
          <input
            type="checkbox"
            class="repair-result-checkbox"
            data-testid="repair-result-include"
            :checked="selected.includes(outcome.id)"
            :disabled="outcome.state !== 'done'"
            @change="toggle(outcome.id)"
          />
          <span class="repair-result-title">{{ outcome.title || `Audiobook #${outcome.id}` }}</span>
        </label>
        <p class="repair-result-message" data-testid="repair-result-message">
          {{ outcome.message }}
        </p>
        <dl v-if="outcome.after" class="repair-result-records">
          <dt>Old</dt>
          <dd data-testid="repair-result-old">{{ bookSummary(outcome.before) || 'None' }}</dd>
          <dt>New</dt>
          <dd data-testid="repair-result-new">
            <template v-for="(part, index) in newParts(outcome)" :key="part.key">
              <template v-if="index"> - </template>
              <span
                :class="{ 'repair-result-changed': part.changed }"
                :data-changed="part.changed || undefined"
                >{{ part.text }}</span
              >
            </template>
          </dd>
        </dl>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhFolderOpen } from '@phosphor-icons/vue'
import { bookSummary, recordParts, type BookRecord, type BookTaskState } from '@/utils/bookMatch'

// One book of the dialog once the repair is over
export interface RepairOutcome {
  id: number
  title: string
  // How its repair ended: done, warning (in part), failed, or skipped (not ticked)
  state: BookTaskState
  // The line saying how it ended
  message: string
  // Its record before the repair, and after it when its ASIN changed (null otherwise)
  before: BookRecord
  after: BookRecord | null
}

const props = defineProps<{ outcomes: RepairOutcome[] }>()

// The books to organize: repaired ones only
const selected = defineModel<number[]>('selected', { required: true })

const repairedCount = computed(() => props.outcomes.filter((o) => o.state === 'done').length)

function toggle(id: number) {
  selected.value = selected.value.includes(id)
    ? selected.value.filter((s) => s !== id)
    : [...selected.value, id]
}

// The new record's parts, each marked when it differs from the old record's (the ASIN in any case)
function newParts(outcome: RepairOutcome) {
  const before = new Map(recordParts(outcome.before).map((part) => [part.key, part.text]))
  return recordParts(outcome.after!).map((part) => ({
    ...part,
    changed: (before.get(part.key) ?? '').toUpperCase() !== part.text.toUpperCase(),
  }))
}
</script>

<style scoped>
.repair-results {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

/* Not to be missed: what was repaired, and the question that comes next */
.repair-results-message {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: color-mix(in srgb, var(--status-warning) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--status-warning) 45%, transparent);
  border-radius: 6px;
  color: var(--status-warning);
}

.repair-results-message-none {
  background-color: rgba(255, 255, 255, 0.04);
  border-color: #444;
  color: #aaa;
}

.repair-results-message p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ddd;
}

.repair-results-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

/* The outcome's colour on the card's edge, as on the book cards during the repair */
.form-control-card.repair-result {
  border-left: 3px solid var(--status-idle);
}

.form-control-card.repair-result-done {
  border-left-color: var(--status-success);
}

.form-control-card.repair-result-warning {
  border-left-color: var(--status-warning);
}

.form-control-card.repair-result-failed {
  border-left-color: var(--status-error);
}

.repair-result-skipped {
  opacity: 0.6;
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

.repair-result-message {
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

.repair-result-changed {
  color: var(--status-success);
  font-weight: 600;
}
</style>
