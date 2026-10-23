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
<!-- The repair, book by book, from the moment it starts: the books ticked for it on step 1. They
     wait in a loading state for their turn, one repair at a time; the
     bar along each card's bottom breathes in blue while its book is repaired, then turns green,
     orange or red by how it ended. A book to repair shows its record before and the one it gets,
     the parts that change in green; once repaired it can be ticked to be organized (ticked at
     first). A book whose repair failed or was canceled says why, and can't be. -->
<template>
  <div class="repair-results">
    <div
      class="repair-results-message"
      :class="{
        'repair-results-message-running': running,
        'repair-results-message-none': !running && !repairedCount,
      }"
      data-testid="repair-results-message"
    >
      <PhSpinner v-if="running" class="ph-spin" />
      <PhFolderOpen v-else />
      <p v-if="running">
        <strong>Repairing {{ current }} of {{ toRepairCount }}…</strong> The books are repaired one
        after the other; Cancel stops before the next one.
      </p>
      <p v-else-if="repairedCount">
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
          <PhSpinner
            v-if="outcome.state === 'waiting' || outcome.state === 'running'"
            class="ph-spin"
            :size="14"
            data-testid="repair-result-loading"
          />
          {{ outcome.message }}
        </p>
        <!-- From the start, so the card keeps its height when its repair ends -->
        <dl class="repair-result-records">
          <dt>Old</dt>
          <dd data-testid="repair-result-old">{{ bookSummary(outcome.before) || 'None' }}</dd>
          <dt>New</dt>
          <dd v-if="newRecord(outcome)" data-testid="repair-result-new">
            <template v-for="(part, index) in newParts(outcome)" :key="part.key">
              <template v-if="index"> - </template>
              <span
                :class="{ 'repair-result-changed': part.changed }"
                :data-changed="part.changed || undefined"
                >{{ part.text }}</span
              >
            </template>
          </dd>
          <dd v-else data-testid="repair-result-new">None: the book keeps its old record</dd>
        </dl>
        <div class="repair-result-bar" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhFolderOpen, PhSpinner } from '@phosphor-icons/vue'
import { bookSummary, recordParts, type BookRecord, type BookTaskState } from '@/utils/bookMatch'

// One book to repair, from the moment the repair starts
export interface RepairOutcome {
  id: number
  title: string
  // Waiting, running, then how its repair ended: done, warning (in part), failed, or skipped
  // (canceled before its turn)
  state: BookTaskState
  // The line saying where its repair is or how it ended
  message: string
  // Its record before the repair; the record it is to get; and its record after the repair (null
  // until it is, or when it failed)
  before: BookRecord
  planned: BookRecord
  after: BookRecord | null
}

// running: the repairs aren't over yet
const props = defineProps<{ outcomes: RepairOutcome[]; running: boolean }>()

// The books to organize: repaired ones only
const selected = defineModel<number[]>('selected', { required: true })

const repairedCount = computed(() => props.outcomes.filter((o) => o.state === 'done').length)
const toRepairCount = computed(() => props.outcomes.length)
// The book being repaired, counted from 1 among the books to repair
const current = computed(
  () => props.outcomes.findIndex((o) => o.state === 'running') + 1 || toRepairCount.value,
)

// The record a book gets: the one it got, or until its repair ends the one it is to get. None once
// its repair failed or was canceled
function newRecord(outcome: RepairOutcome): BookRecord | null {
  if (outcome.after) return outcome.after
  return outcome.state === 'waiting' || outcome.state === 'running' ? outcome.planned : null
}

function toggle(id: number) {
  selected.value = selected.value.includes(id)
    ? selected.value.filter((s) => s !== id)
    : [...selected.value, id]
}

// The new record's parts, each marked when it differs from the old record's (the ASIN in any case)
function newParts(outcome: RepairOutcome) {
  const before = new Map(recordParts(outcome.before).map((part) => [part.key, part.text]))
  return recordParts(newRecord(outcome)!).map((part) => ({
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

.repair-results-message-running {
  background-color: color-mix(in srgb, var(--status-progress) 10%, transparent);
  border-color: color-mix(in srgb, var(--status-progress) 45%, transparent);
  color: var(--status-progress);
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

/* The book's state on the bar along the card's bottom, as on the book cards: grey while it
   waits or when it isn't repaired, blue breathing while it is repaired, then its outcome */
.form-control-card.repair-result {
  position: relative;
  overflow: hidden;
  padding-bottom: calc(1rem + 3px);
}

.repair-result-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--status-idle);
}

.repair-result-done .repair-result-bar {
  background: var(--status-success);
}

.repair-result-warning .repair-result-bar {
  background: var(--status-warning);
}

.repair-result-failed .repair-result-bar {
  background: var(--status-error);
}

.repair-result-running .repair-result-bar {
  background: var(--status-progress);
  animation: repair-result-bar-breathe 1.6s ease-in-out infinite alternate;
}

@keyframes repair-result-bar-breathe {
  from {
    opacity: 0.2;
  }
  to {
    opacity: 1;
  }
}

@media (prefers-reduced-motion: reduce) {
  .repair-result-running .repair-result-bar {
    animation: none;
  }
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

.repair-result-changed {
  color: var(--status-success);
  font-weight: 600;
}
</style>
