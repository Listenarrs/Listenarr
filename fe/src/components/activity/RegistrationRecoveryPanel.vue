<template>
  <section class="recovery-panel" aria-labelledby="registration-recovery-heading">
    <h2 id="registration-recovery-heading">File registration recovery</h2>
    <p>
      Review blocked move, copy and hardlink registrations individually, including restarted
      publications. Releasing a block preserves files and does not complete an import. Recovery of
      existing-file move and rename operations is separate and is not listed here.
    </p>
    <button class="btn btn-secondary" :disabled="busy" @click="refresh">
      Refresh recovery operations
    </button>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="!busy && !reviews.length && !error">No operations requiring review.</p>
    <ul>
      <li v-for="review in reviews" :key="review.operationId">
        {{ review.action }} · Book {{ review.audiobookId ?? 'unknown' }} · {{ review.state }}
        <code>{{ review.operationId }}</code>
        <span v-if="review.state === 'OperatorResolved'"
          >Block released by operator; import completeness remains unverified.</span
        >
        <button class="btn btn-secondary" :disabled="busy" @click="inspect(review.operationId)">
          Inspect operation
        </button>
      </li>
    </ul>
    <p v-if="reviews.length" role="status">
      Showing {{ reviews.length }} of {{ total }} operations.
    </p>
    <button
      v-if="reviews.length < total"
      class="btn btn-secondary"
      :disabled="busy"
      @click="loadMore"
    >
      Load more
    </button>
    <div v-if="selected" class="operation-review">
      <h3>Review {{ selected.operationId }}</h3>
      <p>{{ selected.error }}</p>
      <p>{{ selected.action }} · Protocol {{ selected.protocolVersion }} · {{ selected.state }}</p>
      <dl>
        <dt>Source</dt>
        <dd>
          {{ selected.sourcePath }}<br />{{ selected.source.status }} ·
          {{ selected.source.length ?? 'unknown' }} bytes<br /><code>{{
            selected.source.sha256
          }}</code>
        </dd>
        <dt>Destination</dt>
        <dd>
          {{ selected.destinationPath }}<br />{{ selected.destination.status }} ·
          {{ selected.destination.length ?? 'unknown' }} bytes<br /><code>{{
            selected.destination.sha256
          }}</code>
        </dd>
      </dl>
      <p role="status">
        <strong>{{ selected.contentEvidence }}</strong>
      </p>
      <h4>Library registrations for this book or destination</h4>
      <p v-if="!selected.registrations.length">No matching registrations.</p>
      <ul>
        <li v-for="file in selected.registrations" :key="file.id">
          Book {{ file.audiobookId }} · File {{ file.id }} · {{ file.path }}
        </li>
      </ul>
      <template v-if="selected.canResolve">
        <p>
          This releases only the recovery block. No files are changed or registered. Missing tracks,
          incomplete copies and leftover sources require manual attention. Files may change after
          inspection.
        </p>
        <label
          >Resolution note<textarea
            v-model="note"
            :disabled="busy"
            maxlength="2000"
            placeholder="What did you inspect or repair, and what remains unresolved?"
          />
        </label>
        <label
          ><input v-model="acknowledged" :disabled="busy" type="checkbox" /> I reviewed this
          operation and accept that content may still be incomplete. This does not mark the import
          successful.</label
        >
        <button
          class="btn btn-primary"
          :disabled="busy || !acknowledged || !note.trim()"
          @click="resolve"
        >
          Release this recovery block
        </button>
      </template>
      <p v-else>
        Resolved by {{ selected.resolvedBy }} at {{ selected.resolvedAtUtc }}.
        {{ selected.resolutionNote }}
      </p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { apiService } from '@/services/api'
import type { RegistrationReview } from '@/types/registrationRecovery'

const reviews = ref<RegistrationReview[]>([])
const total = ref(0)
const selected = ref<RegistrationReview | null>(null)
const busy = ref(false)
const error = ref('')
const note = ref('')
const acknowledged = ref(false)

async function refresh() {
  busy.value = true
  error.value = ''
  selected.value = null
  try {
    const list = await apiService.getRegistrationReviews()
    reviews.value = list.reviews
    total.value = list.total
  } catch {
    error.value = 'Could not load recovery operations. Try refreshing.'
  } finally {
    busy.value = false
  }
}

async function loadMore() {
  busy.value = true
  error.value = ''
  try {
    const list = await apiService.getRegistrationReviews(reviews.value.length)
    const known = new Set(reviews.value.map((r) => r.operationId))
    const fresh = list.reviews.filter((r) => !known.has(r.operationId))
    const merged = reviews.value.length + fresh.length
    const repeated = list.reviews.length > fresh.length
    if (merged < list.total && (repeated || !fresh.length)) {
      // Rows moved between pages (another client resolved or added operations), so the offset no
      // longer lines up with the list. Re-read from the start instead of trusting this page.
      await reloadFromStart(merged + 1)
    } else {
      reviews.value = [...reviews.value, ...fresh]
      total.value = list.total
    }
  } catch {
    error.value = 'Could not load more recovery operations. Try again.'
  } finally {
    busy.value = false
  }
}

// Pages by the number of rows the server returned, not by the number of distinct rows held,
// because a row listed twice must not shift the next offset.
async function reloadFromStart(minimum: number) {
  const reloaded: RegistrationReview[] = []
  const known = new Set<string>()
  let offset = 0
  let list = await apiService.getRegistrationReviews()
  for (;;) {
    appendNew(reloaded, known, list.reviews)
    offset += list.reviews.length
    if (reloaded.length >= minimum || !list.reviews.length || offset >= list.total) break
    list = await apiService.getRegistrationReviews(offset)
  }
  reviews.value = reloaded
  total.value = list.total
}

async function inspect(id: string) {
  busy.value = true
  error.value = ''
  selected.value = null
  note.value = ''
  acknowledged.value = false
  try {
    selected.value = await apiService.inspectRegistration(id)
  } catch {
    error.value = 'Could not inspect this operation. Refresh and try again.'
  } finally {
    busy.value = false
  }
}

async function resolve() {
  if (!selected.value || busy.value || !acknowledged.value || !note.value.trim()) return
  busy.value = true
  error.value = ''
  try {
    selected.value = await apiService.resolveRegistration(
      selected.value.operationId,
      selected.value.reviewToken,
      note.value,
    )
  } catch {
    selected.value = null
    error.value =
      'Resolution was not confirmed. Inspect the operation again; its state or files may have changed.'
    busy.value = false
    return
  }
  try {
    await reloadFromStart(reviews.value.length)
  } catch {
    error.value = 'Resolved, but the list could not be refreshed.'
  } finally {
    busy.value = false
  }
}

function appendNew(target: RegistrationReview[], known: Set<string>, rows: RegistrationReview[]) {
  for (const r of rows) {
    if (known.has(r.operationId)) continue
    known.add(r.operationId)
    target.push(r)
  }
}

onMounted(refresh)
</script>

<style scoped>
.recovery-panel {
  padding: 1.25rem;
  margin-bottom: 1.5rem;
  border: 1px solid var(--border-color, #666);
  border-radius: 8px;
}
.recovery-panel li {
  padding: 0.6rem 0;
}
.recovery-panel code,
dd {
  overflow-wrap: anywhere;
}
.recovery-panel code,
.recovery-panel label {
  display: block;
  margin: 0.5rem 0;
}
.recovery-panel textarea {
  display: block;
  width: 100%;
  min-height: 5rem;
  margin-top: 0.5rem;
}
.operation-review {
  margin-top: 1rem;
  padding-top: 1rem;
  border-top: 1px solid var(--border-color, #666);
}
</style>
