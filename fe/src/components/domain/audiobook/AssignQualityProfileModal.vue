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
<template>
  <Modal :visible="visible" size="md" @close="onCancel">
    <template #header>
      <ModalHeader title="Assign Quality Profile" :icon="PhStar" @close="onCancel" />
    </template>

    <template #default>
      <ModalBody>
        <p class="assign-intro">
          {{ pendingTargets.length }} wanted
          {{ pendingTargets.length === 1 ? 'audiobook has' : 'audiobooks have' }} no quality
          profile. Automatic search skips a book until it has one. Choose the profile to assign;
          books that already have a valid profile are left untouched.
        </p>

        <div v-if="profiles.length === 0" class="assign-warning" data-testid="assign-no-profiles">
          No quality profiles exist yet. Create one in
          <RouterLink to="/settings#quality-profiles">Settings → Quality Profiles</RouterLink>
          first.
        </div>

        <div v-else class="form-group">
          <label for="assign-quality-profile">Quality profile</label>
          <select
            id="assign-quality-profile"
            v-model="selectedProfileId"
            class="form-select"
            :disabled="assigning"
          >
            <option :value="null" disabled>Choose a profile…</option>
            <option v-for="profile in profiles" :key="profile.id" :value="profile.id">
              {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
            </option>
          </select>
          <p v-if="!hasDefaultProfile" class="help-text">
            No profile is marked as default, so none is preselected.
          </p>
        </div>

        <div v-if="failures.length > 0" class="assign-failures" data-testid="assign-failures">
          <p class="assign-failures-title">
            {{ failures.length }} of {{ lastAttemptCount }} could not be updated:
          </p>
          <ul>
            <li v-for="failure in failures" :key="failure.id">
              <strong>{{ failure.title }}</strong>
              <span v-if="failure.errors.length"> — {{ failure.errors.join('; ') }}</span>
            </li>
          </ul>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="true" @cancel="onCancel">
        <button
          type="button"
          class="btn btn-primary"
          data-testid="assign-confirm"
          :disabled="!canAssign"
          @click="assign"
        >
          <PhSpinner v-if="assigning" class="ph-spin" />
          <PhCheck v-else />
          {{ assigning ? 'Assigning…' : `Assign to ${pendingTargets.length}` }}
        </button>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'
import type { Audiobook, QualityProfile } from '@/types'
import { PhStar, PhSpinner, PhCheck } from '@phosphor-icons/vue'

export interface AssignQualityProfileFailure {
  id: number
  title: string
  errors: string[]
}

export interface AssignQualityProfileResult {
  profile: QualityProfile
  assigned: number
  /** Ids that were updated by this attempt. */
  assignedIds: number[]
  failures: AssignQualityProfileFailure[]
}

const props = withDefaults(
  defineProps<{
    visible?: boolean
    /** The wanted audiobooks that have no valid quality profile. */
    audiobooks: Audiobook[]
    profiles: QualityProfile[]
    /**
     * Awaited after each assignment attempt, before the picker becomes usable again — the
     * parent refreshes its list here. While it is pending the picker and the assign action
     * stay disabled, so a second submission cannot run against the stale target list.
     */
    afterAssign?: (result: AssignQualityProfileResult) => Promise<void> | void
  }>(),
  { visible: false, afterAssign: undefined },
)

const emit = defineEmits<{
  cancel: []
  assigned: [result: AssignQualityProfileResult]
}>()

const selectedProfileId = ref<number | null>(null)
const assigning = ref(false)
const failures = ref<AssignQualityProfileFailure[]>([])
const lastAttemptCount = ref(0)
/** Books this picker session already assigned; a retry only resubmits the rest. */
const assignedIds = ref(new Set<number>())

const pendingTargets = computed(() =>
  props.audiobooks.filter((audiobook) => !assignedIds.value.has(audiobook.id)),
)

const hasDefaultProfile = computed(() => props.profiles.some((profile) => profile.isDefault))

const selectedProfile = computed(
  () => props.profiles.find((profile) => profile.id === selectedProfileId.value) ?? null,
)

const canAssign = computed(
  () => !assigning.value && selectedProfile.value !== null && pendingTargets.value.length > 0,
)

function preselectDefault() {
  const stillValid = props.profiles.some((profile) => profile.id === selectedProfileId.value)
  if (stillValid && selectedProfileId.value !== null) return
  const defaultProfile = props.profiles.find((profile) => profile.isDefault && profile.id != null)
  selectedProfileId.value = defaultProfile?.id ?? null
}

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      failures.value = []
      lastAttemptCount.value = 0
      assignedIds.value = new Set<number>()
    }
  },
  { immediate: true },
)

watch(
  () => [props.visible, props.profiles] as const,
  ([visible]) => {
    if (visible) preselectDefault()
  },
  { immediate: true },
)

function onCancel() {
  if (assigning.value) return
  emit('cancel')
}

async function assign() {
  const profile = selectedProfile.value
  if (!profile || profile.id == null || assigning.value) return

  const targets = pendingTargets.value
  const ids = targets.map((audiobook) => audiobook.id)
  if (ids.length === 0) return

  assigning.value = true
  failures.value = []
  lastAttemptCount.value = ids.length
  let result: AssignQualityProfileResult
  try {
    const response = await apiService.bulkUpdateAudiobooks(ids, { qualityProfileId: profile.id })
    const outcomeById = new Map(response.results.map((row) => [row.id, row]))
    const failed: AssignQualityProfileFailure[] = []
    const succeeded: number[] = []
    for (const audiobook of targets) {
      const outcome = outcomeById.get(audiobook.id)
      if (!outcome || !outcome.success) {
        failed.push({
          id: audiobook.id,
          title: audiobook.title || `#${audiobook.id}`,
          errors: outcome?.errors ?? ['No result returned for this audiobook'],
        })
      } else {
        succeeded.push(audiobook.id)
      }
    }
    failures.value = failed
    // Remember what landed so a retry — even after a failed refresh leaves these books in
    // the parent's list — only resubmits the books that actually failed.
    for (const id of succeeded) assignedIds.value.add(id)
    result = { profile, assigned: succeeded.length, assignedIds: succeeded, failures: failed }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AssignQualityProfileModal',
      operation: 'assign',
      metadata: { count: ids.length, profileId: profile.id },
    })
    failures.value = targets.map((audiobook) => ({
      id: audiobook.id,
      title: audiobook.title || `#${audiobook.id}`,
      errors: ['The request failed before any book was updated'],
    }))
    result = { profile, assigned: 0, assignedIds: [], failures: failures.value }
  }

  emit('assigned', result)
  try {
    // Stay busy until the parent has refreshed (or closed): the target list is stale until then.
    await props.afterAssign?.(result)
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AssignQualityProfileModal',
      operation: 'afterAssign',
      metadata: { assigned: result.assigned, profileId: profile.id },
    })
  } finally {
    assigning.value = false
  }
}
</script>

<style scoped>
.assign-intro {
  color: #ddd;
  margin: 0 0 1rem;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.form-group label {
  color: #ccc;
  font-size: 0.9rem;
}

.help-text {
  color: #868e96;
  font-size: 0.85rem;
  margin: 0;
}

.assign-warning {
  background: rgba(250, 176, 5, 0.12);
  border: 1px solid rgba(250, 176, 5, 0.4);
  border-radius: 6px;
  color: #ffd43b;
  padding: 0.75rem 1rem;
}

.assign-warning a {
  color: #ffd43b;
  text-decoration: underline;
}

.assign-failures {
  margin-top: 1rem;
  background: rgba(250, 82, 82, 0.1);
  border: 1px solid rgba(250, 82, 82, 0.4);
  border-radius: 6px;
  color: #ffa8a8;
  padding: 0.75rem 1rem;
}

.assign-failures-title {
  margin: 0 0 0.5rem;
  font-weight: 500;
}

.assign-failures ul {
  margin: 0;
  padding-left: 1.25rem;
}
</style>
