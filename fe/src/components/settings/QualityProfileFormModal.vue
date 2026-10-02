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
  <Modal :visible="visible" size="lg" @close="closeModal">
    <template #header>
      <ModalHeader
        :title="profile ? 'Edit Quality Profile' : 'Create Quality Profile'"
        :icon="PhStar"
        @close="closeModal"
      />
    </template>

    <template #default>
      <ModalBody>
        <form @submit.prevent="handleSubmit">
          <!-- Basic Information -->
          <FormSection title="Basic Information" :icon="PhInfo">
            <FormRow label="Profile Name *" labelFor="name">
              <input
                id="name"
                v-model="formData.name"
                type="text"
                required
                placeholder="e.g., High Quality, Any Quality, Space Saver"
              />
            </FormRow>

            <FormRow label="Description" labelFor="description">
              <textarea
                id="description"
                v-model="formData.description"
                rows="2"
                placeholder="Optional description of this quality profile"
              ></textarea>
            </FormRow>

            <CheckboxCard v-model="formData.isDefault" title="Set as default profile" />
            <small class="info-text">
              The default profile will be automatically assigned to new audiobooks
            </small>
          </FormSection>

          <!-- Quality Definitions (Hierarchical Codec/Bitrate Selection) -->
          <FormSection title="Quality Definitions" :icon="PhCheckSquare">
            <p class="section-description">
              Select which codecs you want, then choose specific bitrates for lossy formats.
              Qualities higher in the list are preferred. Drag to reorder.
            </p>

            <!-- Codec Selection -->
            <div class="codec-selection">
              <h4 class="subsection-title">Select Codecs</h4>
              <div class="codec-grid">
                <label v-for="codec in availableCodecs" :key="codec.codec" class="codec-checkbox">
                  <input
                    type="checkbox"
                    :checked="isCodecEnabled(codec.codec)"
                    @change="toggleCodec(codec.codec, ($event.target as HTMLInputElement).checked)"
                  />
                  <span class="codec-label">
                    {{ codec.label }}
                    <span v-if="codec.isLossless" class="lossless-badge">Lossless</span>
                  </span>
                </label>
              </div>
            </div>

            <CheckboxCard
              v-if="showPreferM4b"
              v-model="preferM4b"
              title="Prefer M4B container"
              description="Prefer .m4b when multiple containers match (useful for chapters)."
            />

            <!-- Quality Groups (Draggable) -->
            <div class="quality-groups-container">
              <h4 class="subsection-title">Quality Priority Order</h4>
              <p class="info-text-small">
                <PhInfo />
                Qualities higher in the list are more preferred. Only checked qualities will be
                downloaded.
              </p>

              <div class="quality-groups">
                <!-- Lossless Qualities -->
                <div v-if="losslessQualities.length > 0" class="quality-group">
                  <div class="quality-group-header">
                    <PhMusicNotesSimple class="group-icon" />
                    <input
                      v-if="editingGroupName === 'FLAC'"
                      v-model="tempGroupName"
                      @blur="saveGroupName('FLAC')"
                      @keyup.enter="saveGroupName('FLAC')"
                      class="group-name-input"
                      type="text"
                      ref="groupNameInput"
                    />
                    <span v-else class="group-title">{{
                      customGroupNames.get('FLAC') || 'Lossless'
                    }}</span>
                    <button
                      v-if="editingGroupName !== 'FLAC'"
                      type="button"
                      @click="
                        startEditingGroupName('FLAC', customGroupNames.get('FLAC') || 'Lossless')
                      "
                      class="edit-group-btn"
                      title="Rename group"
                    >
                      <PhPencilSimple />
                    </button>
                  </div>
                  <div class="quality-items">
                    <div
                      v-for="quality in losslessQualities"
                      :key="quality.id"
                      class="quality-item"
                      draggable="true"
                      @dragstart="handleDragStart($event, quality)"
                      @dragover.prevent
                      @drop="handleDrop($event, quality)"
                    >
                      <PhDotsSixVertical class="drag-handle" />
                      <Checkbox
                        :modelValue="quality.enabled"
                        @update:modelValue="(val: boolean) => toggleQualityItem(quality.id, val)"
                      >
                        <span class="quality-label">{{ quality.label }}</span>
                      </Checkbox>
                    </div>
                  </div>
                </div>

                <!-- Lossy Codecs with Bitrates -->
                <div
                  v-for="codecGroup in lossyCodecGroups"
                  :key="codecGroup.codec"
                  v-show="codecGroup.qualities.length > 0"
                  class="quality-group"
                >
                  <div class="quality-group-header">
                    <PhMusicNotesSimple class="group-icon" />
                    <input
                      v-if="editingGroupName === codecGroup.codec"
                      v-model="tempGroupName"
                      @blur="saveGroupName(codecGroup.codec)"
                      @keyup.enter="saveGroupName(codecGroup.codec)"
                      class="group-name-input"
                      type="text"
                    />
                    <span v-else class="group-title">{{
                      customGroupNames.get(codecGroup.codec) || codecGroup.label
                    }}</span>
                    <button
                      v-if="editingGroupName !== codecGroup.codec"
                      type="button"
                      @click="
                        startEditingGroupName(
                          codecGroup.codec,
                          customGroupNames.get(codecGroup.codec) || codecGroup.label,
                        )
                      "
                      class="edit-group-btn"
                      title="Rename group"
                    >
                      <PhPencilSimple />
                    </button>
                  </div>
                  <div class="quality-items">
                    <div
                      v-for="quality in codecGroup.qualities"
                      :key="quality.id"
                      class="quality-item"
                      draggable="true"
                      @dragstart="handleDragStart($event, quality)"
                      @dragover.prevent
                      @drop="handleDrop($event, quality)"
                    >
                      <PhDotsSixVertical class="drag-handle" />
                      <Checkbox
                        :modelValue="quality.enabled"
                        @update:modelValue="(val: boolean) => toggleQualityItem(quality.id, val)"
                      >
                        <span class="quality-label">
                          {{ quality.label }}
                          <span v-if="quality.bitrate" class="bitrate-badge"
                            >{{ quality.bitrate }} kbps</span
                          >
                        </span>
                      </Checkbox>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <!-- Cutoff Selection -->
            <div class="cutoff-selection">
              <h4 class="subsection-title">Upgrade Until</h4>
              <CheckboxCard
                v-model="upgradesEnabled"
                title="Enable Quality Upgrades"
                description="If disabled, qualities will not be upgraded"
              />
              <FormRow v-if="upgradesEnabled" label="Upgrade Until" labelFor="cutoff-quality">
                <select id="cutoff-quality" v-model="formData.cutoffQuality">
                  <option value="">No Cutoff (Always Upgrade)</option>
                  <option v-for="quality in enabledQualities" :key="quality.id" :value="quality.id">
                    {{ quality.label }}
                  </option>
                </select>
              </FormRow>
              <small v-if="upgradesEnabled" class="info-text">
                <PhInfo />
                Listenarr will stop upgrading once this quality is reached
              </small>
            </div>
          </FormSection>

          <!-- Size Limits -->
          <FormSection title="Size Limits" :icon="PhRuler">
            <p class="section-description">
              Set minimum and maximum file sizes in megabytes (leave blank for no limit).
            </p>

            <div class="form-row">
              <FormRow label="Minimum Size (MB)" labelFor="minimumSize">
                <input
                  id="minimumSize"
                  v-model.number="formData.minimumSize"
                  type="number"
                  min="0"
                  placeholder="No minimum"
                />
              </FormRow>

              <FormRow label="Maximum Size (MB)" labelFor="maximumSize">
                <input
                  id="maximumSize"
                  v-model.number="formData.maximumSize"
                  type="number"
                  min="0"
                  placeholder="No maximum"
                />
              </FormRow>
            </div>
          </FormSection>

          <!-- Word Filters -->
          <FormSection title="Word Filters" :icon="PhTextAa">
            <!-- Preferred Words -->
            <div class="filter-group">
              <h4><PhSparkle /> Preferred Words (Bonus Points)</h4>
              <p class="section-description">
                Releases containing these words will receive bonus points in scoring.
              </p>
              <div class="tag-input-group">
                <div
                  :class="[
                    'tags-list',
                    { 'tags-list-empty': (formData.preferredWords?.length || 0) === 0 },
                  ]"
                >
                  <div
                    v-for="(word, index) in formData.preferredWords"
                    :key="index"
                    class="tag positive removable"
                  >
                    {{ word }}
                    <button type="button" @click="removePreferredWord(index)" class="tag-remove">
                      <PhX />
                    </button>
                  </div>
                </div>
                <div class="tag-input">
                  <input
                    v-model="newPreferredWord"
                    @keypress.enter.prevent="addPreferredWord"
                    type="text"
                    placeholder="e.g., unabridged, complete"
                  />
                  <button
                    type="button"
                    @click="addPreferredWord"
                    :disabled="!newPreferredWord.trim()"
                    :aria-disabled="!newPreferredWord.trim()"
                    class="btn icon-btn btn-primary btn-sm"
                    title="Add preferred word"
                    aria-label="Add preferred word"
                  >
                    <PhPlus />
                  </button>
                </div>
              </div>
            </div>

            <!-- Must Contain -->
            <div class="filter-group">
              <h4><PhCheck /> Must Contain (Required)</h4>
              <p class="section-description">
                Releases MUST contain at least one of these words (case-insensitive).
              </p>
              <div class="tag-input-group">
                <div
                  :class="[
                    'tags-list',
                    { 'tags-list-empty': (formData.mustContain?.length || 0) === 0 },
                  ]"
                >
                  <div
                    v-for="(word, index) in formData.mustContain"
                    :key="index"
                    class="tag required removable"
                  >
                    {{ word }}
                    <button type="button" @click="removeMustContain(index)" class="tag-remove">
                      <PhX />
                    </button>
                  </div>
                </div>
                <div class="tag-input">
                  <input
                    v-model="newMustContain"
                    @keypress.enter.prevent="addMustContain"
                    type="text"
                    placeholder="e.g., audiobook"
                  />
                  <button
                    type="button"
                    @click="addMustContain"
                    :disabled="!newMustContain.trim()"
                    :aria-disabled="!newMustContain.trim()"
                    class="btn icon-btn btn-primary btn-sm"
                    title="Add required word"
                    aria-label="Add required word"
                  >
                    <PhPlus />
                  </button>
                </div>
              </div>
            </div>

            <!-- Must Not Contain -->
            <div class="filter-group">
              <h4><PhX /> Must Not Contain (Forbidden)</h4>
              <p class="section-description">
                Releases containing any of these words will be rejected (case-insensitive).
              </p>
              <div class="tag-input-group">
                <div
                  :class="[
                    'tags-list',
                    { 'tags-list-empty': (formData.mustNotContain?.length || 0) === 0 },
                  ]"
                >
                  <div
                    v-for="(word, index) in formData.mustNotContain"
                    :key="index"
                    class="tag forbidden removable"
                  >
                    {{ word }}
                    <button type="button" @click="removeMustNotContain(index)" class="tag-remove">
                      <PhX />
                    </button>
                  </div>
                </div>
                <div class="tag-input">
                  <input
                    v-model="newMustNotContain"
                    @keypress.enter.prevent="addMustNotContain"
                    type="text"
                    placeholder="e.g., abridged, radio"
                  />
                  <button
                    type="button"
                    @click="addMustNotContain"
                    :disabled="!newMustNotContain.trim()"
                    :aria-disabled="!newMustNotContain.trim()"
                    class="btn icon-btn btn-primary btn-sm"
                    title="Add forbidden word"
                    aria-label="Add forbidden word"
                  >
                    <PhPlus />
                  </button>
                </div>
              </div>
            </div>
          </FormSection>

          <!-- Language Preferences -->
          <FormSection title="Language Preferences" :icon="PhTranslate">
            <p class="section-description">Preferred languages in order of preference.</p>

            <div class="tag-input-group">
              <div
                :class="[
                  'tags-list',
                  { 'tags-list-empty': (formData.preferredLanguages?.length || 0) === 0 },
                ]"
              >
                <div
                  v-for="(lang, index) in formData.preferredLanguages"
                  :key="index"
                  class="tag removable"
                >
                  {{ lang }}
                  <button type="button" @click="removeLanguage(index)" class="tag-remove">
                    <PhX />
                  </button>
                </div>
              </div>
              <div class="tag-input">
                <input
                  v-model="newLanguage"
                  @keypress.enter.prevent="addLanguage"
                  type="text"
                  placeholder="e.g., English, Spanish"
                />
                <button
                  type="button"
                  @click="addLanguage"
                  :disabled="!newLanguage.trim()"
                  :aria-disabled="!newLanguage.trim()"
                  class="btn icon-btn btn-primary btn-sm"
                  title="Add language"
                  aria-label="Add language"
                >
                  <PhPlus />
                </button>
              </div>
            </div>
          </FormSection>

          <!-- Release Preferences -->
          <FormSection title="Release Preferences" :icon="PhClockCounterClockwise">
            <FormRow label="Minimum Seeders (Torrents)" labelFor="minimumSeeders">
              <input
                id="minimumSeeders"
                v-model.number="formData.minimumSeeders"
                type="number"
                min="0"
                placeholder="0 = no minimum"
              />
            </FormRow>

            <FormRow label="Minimum Score Threshold" labelFor="minimumScore">
              <input
                id="minimumScore"
                v-model.number="formData.minimumScore"
                type="number"
                min="0"
                max="100"
                placeholder="0 = allow any score"
              />
            </FormRow>

            <CheckboxCard
              v-model="formData.preferNewerReleases"
              title="Prefer newer releases"
              description="Give bonus points to more recent releases (torrent upload date)"
            />

            <!--
              Maximum Age is not part of the checkbox above. It is a hard reject applied by
              SearchResultScorer whenever it is greater than zero, and the scorer never reads
              PreferNewerReleases. Hiding this input therefore hid a filter that stayed on, and
              the only way back to it was to tick a box that claims to do something else.
            -->
            <FormRow
              label="Maximum Age (Days)"
              labelFor="maximumAge"
              help="Reject releases older than this many days (0 = no limit)"
            >
              <input
                id="maximumAge"
                v-model.number="formData.maximumAge"
                type="number"
                min="0"
                placeholder="0 = no maximum"
              />
            </FormRow>
          </FormSection>
        </form>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter
        :showCancel="true"
        :showSave="true"
        :saving="saving"
        :saveLabel="profile ? 'Save' : 'Create Profile'"
        @cancel="closeModal"
        @save="handleSubmit"
      />
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, watch, computed, nextTick } from 'vue'
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback'
import FormSection from './FormSection.vue'
import FormRow from '@/components/settings/FormRow.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import {
  PhX,
  PhStar,
  PhCheck,
  PhPlus,
  PhInfo,
  PhCheckSquare,
  PhRuler,
  PhPencilSimple,
  PhSparkle,
  PhTextAa,
  PhTranslate,
  PhClockCounterClockwise,
  PhDotsSixVertical,
  PhMusicNotesSimple,
} from '@phosphor-icons/vue'
import type { QualityProfile, CodecDefinition, QualityItem, QualityDefinition } from '@/types'
import { useToast } from '@/services/toastService'

const props = defineProps<{
  visible: boolean
  profile: QualityProfile | null
}>()

const emit = defineEmits<{
  close: []
  save: [profile: QualityProfile]
}>()

const toast = useToast()

/**
 * Available codec definitions with bitrate options
 */
const availableCodecs: CodecDefinition[] = [
  { codec: 'FLAC', label: 'FLAC', isLossless: true },
  {
    codec: 'MP3',
    label: 'MP3',
    isLossless: false,
    bitrates: [320, 256, 192, 128, 64],
    supportsVBR: true,
  },
  { codec: 'AAC', label: 'AAC', isLossless: false, bitrates: [320, 256, 192, 128, 96, 64] },
  { codec: 'OPUS', label: 'OPUS', isLossless: false, bitrates: [192, 128, 96, 64] },
  { codec: 'OGG Vorbis', label: 'OGG Vorbis', isLossless: false, bitrates: [320, 256, 192, 128] },
]

// Quality items for drag-and-drop
const qualityItems = ref<QualityItem[]>([])

// Track which codecs are enabled
const enabledCodecs = ref<Set<string>>(new Set())

// Track upgrades enabled
const upgradesEnabled = ref(true)

// Drag state
const draggedQuality = ref<QualityItem | null>(null)

// Custom group names
const customGroupNames = ref<Map<string, string>>(new Map())
const editingGroupName = ref<string | null>(null)
const tempGroupName = ref('')

const showPreferM4b = computed(() => enabledCodecs.value.has('AAC'))

/**
 * Check if a codec is enabled
 */
const isCodecEnabled = (codec: string): boolean => {
  return enabledCodecs.value.has(codec)
}

/**
 * Toggle a codec on/off
 */
const toggleCodec = (codec: string, enabled: boolean) => {
  if (enabled) {
    enabledCodecs.value.add(codec)
    // Add qualities for this codec
    addQualitiesForCodec(codec)
  } else {
    enabledCodecs.value.delete(codec)
    // Remove qualities for this codec
    qualityItems.value = qualityItems.value.filter((q) => q.codec !== codec)
    if (codec === 'AAC') {
      preferM4b.value = false
    }
  }
}

/**
 * Add quality items for a newly enabled codec
 */
const addQualitiesForCodec = (codecName: string) => {
  const codec = availableCodecs.find((c) => c.codec === codecName)
  if (!codec) return

  const maxPriority =
    qualityItems.value.length > 0 ? Math.max(...qualityItems.value.map((q) => q.priority)) : -1

  if (codec.isLossless) {
    // Add single lossless quality
    qualityItems.value.push({
      id: codec.codec,
      codec: codec.codec,
      label: codec.label,
      isLossless: true,
      enabled: true,
      priority: maxPriority + 1,
    })
  } else {
    // Add bitrate variants
    codec.bitrates?.forEach((bitrate, index) => {
      qualityItems.value.push({
        id: `${codec.codec} ${bitrate}kbps`,
        codec: codec.codec,
        bitrate,
        label: `${codec.label} ${bitrate} kbps`,
        isLossless: false,
        enabled: true,
        priority: maxPriority + 1 + index,
      })
    })

    // Add VBR if supported
    if (codec.supportsVBR) {
      qualityItems.value.push({
        id: `${codec.codec} VBR`,
        codec: codec.codec,
        label: `${codec.label} Variable Bitrate`,
        isLossless: false,
        enabled: true,
        priority: maxPriority + 1 + (codec.bitrates?.length || 0),
      })
    }
  }

  // Sort by priority
  qualityItems.value.sort((a, b) => a.priority - b.priority)
}

/**
 * Start editing a group name
 */
const startEditingGroupName = (codec: string, currentName: string) => {
  editingGroupName.value = codec
  tempGroupName.value = currentName
  // Focus input on next tick
  nextTick(() => {
    const input = document.querySelector('.group-name-input') as HTMLInputElement
    if (input) {
      input.focus()
      input.select()
    }
  })
}

/**
 * Save edited group name
 */
const saveGroupName = (codec: string) => {
  if (tempGroupName.value.trim()) {
    customGroupNames.value.set(codec, tempGroupName.value.trim())
  } else {
    customGroupNames.value.delete(codec)
  }
  editingGroupName.value = null
  tempGroupName.value = ''
}

/**
 * Toggle individual quality item
 */
const toggleQualityItem = (qualityId: string, enabled: boolean) => {
  const quality = qualityItems.value.find((q) => q.id === qualityId)
  if (quality) {
    quality.enabled = enabled
  }
}

/**
 * Get lossless qualities
 */
const losslessQualities = computed(() => {
  return qualityItems.value
    .filter((q) => q.isLossless && enabledCodecs.value.has(q.codec))
    .sort((a, b) => a.priority - b.priority)
})

/**
 * Get lossy codec groups
 */
const lossyCodecGroups = computed(() => {
  const groups = new Map<string, { codec: string; label: string; qualities: QualityItem[] }>()

  qualityItems.value
    .filter(
      (q) =>
        !q.isLossless &&
        enabledCodecs.value.has(q.codec) &&
        (q.codec !== 'M4B' || enabledCodecs.value.has('AAC')),
    )
    .forEach((quality) => {
      if (!groups.has(quality.codec)) {
        const codec = availableCodecs.find((c) => c.codec === quality.codec)
        groups.set(quality.codec, {
          codec: quality.codec,
          label: codec?.label || quality.codec,
          qualities: [],
        })
      }
      groups.get(quality.codec)!.qualities.push(quality)
    })

  // Sort qualities within each group by priority
  groups.forEach((group) => {
    group.qualities.sort((a, b) => a.priority - b.priority)
  })

  return Array.from(groups.values())
})

/**
 * Get enabled qualities for cutoff dropdown
 */
const enabledQualities = computed(() => {
  return qualityItems.value.filter((q) => q.enabled).sort((a, b) => a.priority - b.priority)
})

/**
 * Drag and drop handlers
 */
const handleDragStart = (event: DragEvent, quality: QualityItem) => {
  draggedQuality.value = quality
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
  }
}

const handleDrop = (event: DragEvent, targetQuality: QualityItem) => {
  event.preventDefault()

  if (!draggedQuality.value || draggedQuality.value.id === targetQuality.id) {
    return
  }

  // Get current indices
  const draggedIndex = qualityItems.value.findIndex((q) => q.id === draggedQuality.value!.id)
  const targetIndex = qualityItems.value.findIndex((q) => q.id === targetQuality.id)

  if (draggedIndex === -1 || targetIndex === -1) return

  // Reorder array
  const items = [...qualityItems.value]
  const removed = items.splice(draggedIndex, 1)
  if (removed.length === 0) return
  const draggedItem = removed[0]!
  items.splice(targetIndex, 0, draggedItem)

  // Reassign priorities based on new order
  items.forEach((item, index) => {
    item.priority = index
  })

  qualityItems.value = items
  draggedQuality.value = null
}

// Form data
const formData = ref<QualityProfile>({
  name: '',
  description: '',
  qualities: [],
  cutoffQuality: '',
  minimumSize: undefined,
  maximumSize: undefined,
  preferredFormats: [],
  preferredWords: [],
  mustNotContain: [],
  mustContain: [],
  preferredLanguages: [],
  minimumSeeders: 0,
  minimumScore: 0,
  isDefault: false,
  preferNewerReleases: false,
  maximumAge: 0,
})

const preferM4b = ref(false)
// Tag input refs
const newPreferredWord = ref('')
const newMustContain = ref('')
const newMustNotContain = ref('')
const newLanguage = ref('')

// Initialize form when profile changes
watch(
  () => props.profile,
  (newProfile) => {
    if (newProfile) {
      // Assign properties directly to maintain reactivity
      formData.value.id = newProfile.id // CRITICAL: Preserve ID for update operations
      formData.value.name = newProfile.name
      formData.value.description = newProfile.description
      formData.value.cutoffQuality = newProfile.cutoffQuality || ''
      formData.value.minimumSize = newProfile.minimumSize
      formData.value.maximumSize = newProfile.maximumSize
      formData.value.preferredFormats = newProfile.preferredFormats
        ? [...newProfile.preferredFormats]
        : []
      formData.value.preferredWords = newProfile.preferredWords
        ? [...newProfile.preferredWords]
        : []
      formData.value.mustNotContain = newProfile.mustNotContain
        ? [...newProfile.mustNotContain]
        : []
      formData.value.mustContain = newProfile.mustContain ? [...newProfile.mustContain] : []
      formData.value.preferredLanguages = newProfile.preferredLanguages
        ? [...newProfile.preferredLanguages]
        : []
      formData.value.minimumSeeders = newProfile.minimumSeeders
      formData.value.minimumScore = newProfile.minimumScore || 0
      formData.value.isDefault = newProfile.isDefault
      formData.value.preferNewerReleases = newProfile.preferNewerReleases
      formData.value.maximumAge = newProfile.maximumAge

      preferM4b.value = (formData.value.preferredFormats || []).some(
        (format) => (format || '').toLowerCase().trim() === 'm4b',
      )

      // Load custom group names if present
      customGroupNames.value.clear()
      if (newProfile.customGroupNames) {
        Object.entries(newProfile.customGroupNames).forEach(([codec, name]) => {
          customGroupNames.value.set(codec, name as string)
        })
      }

      // Initialize quality items from saved qualities
      initializeQualitiesFromProfile(newProfile)

      // Check if upgrades are disabled
      upgradesEnabled.value = !!newProfile.cutoffQuality
    } else {
      // Reset to defaults
      formData.value = {
        name: '',
        description: '',
        qualities: [],
        cutoffQuality: '',
        minimumSize: undefined,
        maximumSize: undefined,
        preferredFormats: [],
        preferredWords: [],
        mustNotContain: [],
        mustContain: [],
        preferredLanguages: [],
        minimumSeeders: 0,
        minimumScore: 0,
        isDefault: false,
        preferNewerReleases: false,
        maximumAge: 0,
      }

      preferM4b.value = false
      customGroupNames.value.clear()

      // Reset quality items
      qualityItems.value = []
      enabledCodecs.value = new Set()
      upgradesEnabled.value = true
    }
  },
  { immediate: true },
)

/**
 * Initialize quality items from profile
 */
// A function declaration, not a const arrow. The watch above runs with `immediate: true`,
// so it calls this during setup, before a const at this point in the file has been
// initialised. As an arrow it threw ReferenceError on every mount that had a profile,
// Vue caught it, and the qualities were silently left empty.
function initializeQualitiesFromProfile(profile: QualityProfile) {
  // Clear existing
  qualityItems.value = []
  enabledCodecs.value = new Set()

  if (!profile.qualities || profile.qualities.length === 0) {
    return
  }

  // Build quality items from profile
  profile.qualities.forEach((qualityDef) => {
    const typedQualityDef = qualityDef as QualityDefinition & {
      codec?: string
      bitrate?: number
      isLossless?: boolean
    }

    const parsed = parseQualityString(qualityDef.quality)
    const codec = typedQualityDef.codec || parsed?.codec
    const bitrate = typedQualityDef.bitrate ?? parsed?.bitrate
    const isLossless = typedQualityDef.isLossless ?? parsed?.isLossless

    if (!codec || isLossless === undefined) return

    const label = parsed?.label || (bitrate ? `${codec} ${bitrate} kbps` : codec)

    const id = qualityDef.quality || (bitrate ? `${codec} ${bitrate}kbps` : codec)

    // Track enabled codec
    if (qualityDef.allowed) {
      enabledCodecs.value.add(codec)
    }

    // Add to quality items
    qualityItems.value.push({
      id,
      codec,
      bitrate,
      label,
      isLossless,
      enabled: qualityDef.allowed,
      priority: qualityDef.priority,
    })
  })

  // Sort by priority
  qualityItems.value.sort((a, b) => a.priority - b.priority)
}

/**
 * Parse a quality string into structured data
 */
function parseQualityString(qualityStr: string): QualityItem | null {
  // FLAC
  if (qualityStr === 'FLAC') {
    return {
      id: 'FLAC',
      codec: 'FLAC',
      label: 'FLAC',
      isLossless: true,
      enabled: false,
      priority: 0,
    }
  }

  // MP3 with bitrate
  const mp3Match = qualityStr.match(/MP3\s+(\d+)\s?kbps/i)
  if (mp3Match) {
    const bitrate = parseInt(mp3Match[1]!)
    return {
      id: qualityStr,
      codec: 'MP3',
      bitrate,
      label: `MP3 ${bitrate} kbps`,
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // MP3 VBR
  if (qualityStr === 'MP3 VBR') {
    return {
      id: 'MP3 VBR',
      codec: 'MP3',
      label: 'MP3 Variable Bitrate',
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // AAC with bitrate
  const aacMatch = qualityStr.match(/AAC\s+(\d+)\s?kbps/i)
  if (aacMatch) {
    const bitrate = parseInt(aacMatch[1]!)
    return {
      id: qualityStr,
      codec: 'AAC',
      bitrate,
      label: `AAC ${bitrate} kbps`,
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // AAC without bitrate
  if (qualityStr === 'AAC') {
    return { id: 'AAC', codec: 'AAC', label: 'AAC', isLossless: false, enabled: false, priority: 0 }
  }

  // M4B with bitrate
  const m4bMatch = qualityStr.match(/M4B\s+(\d+)\s?kbps/i)
  if (m4bMatch) {
    const bitrate = parseInt(m4bMatch[1]!)
    return {
      id: qualityStr,
      codec: 'M4B',
      bitrate,
      label: `M4B ${bitrate} kbps`,
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // M4B without bitrate
  if (qualityStr === 'M4B') {
    return {
      id: 'M4B',
      codec: 'M4B',
      label: 'M4B (AAC)',
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // OPUS with bitrate
  const opusMatch = qualityStr.match(/OPUS\s+(\d+)\s?kbps/i)
  if (opusMatch) {
    const bitrate = parseInt(opusMatch[1]!)
    return {
      id: qualityStr,
      codec: 'OPUS',
      bitrate,
      label: `OPUS ${bitrate} kbps`,
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // OPUS without bitrate
  if (qualityStr === 'OPUS') {
    return {
      id: 'OPUS',
      codec: 'OPUS',
      label: 'OPUS',
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // OGG Vorbis with bitrate
  const oggMatch = qualityStr.match(/OGG Vorbis\s+(\d+)\s?kbps/i)
  if (oggMatch) {
    const bitrate = parseInt(oggMatch[1]!)
    return {
      id: qualityStr,
      codec: 'OGG Vorbis',
      bitrate,
      label: `OGG Vorbis ${bitrate} kbps`,
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  // OGG Vorbis without bitrate
  if (qualityStr === 'OGG Vorbis') {
    return {
      id: 'OGG Vorbis',
      codec: 'OGG Vorbis',
      label: 'OGG Vorbis',
      isLossless: false,
      enabled: false,
      priority: 0,
    }
  }

  return null
}

/**
 * Build quality definitions array for saving
 */
const buildQualityDefinitions = () => {
  return qualityItems.value.map((quality, index) => ({
    quality: quality.id,
    allowed: quality.enabled,
    priority: index, // Use array index as priority after drag-drop reordering
    codec: quality.codec,
    bitrate: quality.bitrate,
    isLossless: quality.isLossless,
  }))
}

// Preferred words management
const addPreferredWord = () => {
  const word = newPreferredWord.value.trim()
  if (word && !formData.value.preferredWords?.includes(word)) {
    if (!formData.value.preferredWords) {
      formData.value.preferredWords = []
    }
    formData.value.preferredWords.push(word)
    newPreferredWord.value = ''
  }
}

const removePreferredWord = (index: number) => {
  formData.value.preferredWords?.splice(index, 1)
}

// Must contain management
const addMustContain = () => {
  const word = newMustContain.value.trim()
  if (word && !formData.value.mustContain?.includes(word)) {
    if (!formData.value.mustContain) {
      formData.value.mustContain = []
    }
    formData.value.mustContain.push(word)
    newMustContain.value = ''
  }
}

const removeMustContain = (index: number) => {
  formData.value.mustContain?.splice(index, 1)
}

// Must not contain management
const addMustNotContain = () => {
  const word = newMustNotContain.value.trim()
  if (word && !formData.value.mustNotContain?.includes(word)) {
    if (!formData.value.mustNotContain) {
      formData.value.mustNotContain = []
    }
    formData.value.mustNotContain.push(word)
    newMustNotContain.value = ''
  }
}

const removeMustNotContain = (index: number) => {
  formData.value.mustNotContain?.splice(index, 1)
}

// Language management
const addLanguage = () => {
  const lang = newLanguage.value.trim()
  if (lang && !formData.value.preferredLanguages?.includes(lang)) {
    if (!formData.value.preferredLanguages) {
      formData.value.preferredLanguages = []
    }
    formData.value.preferredLanguages.push(lang)
    newLanguage.value = ''
  }
}

const removeLanguage = (index: number) => {
  formData.value.preferredLanguages?.splice(index, 1)
}

// Modal actions
const closeModal = () => {
  emit('close')
}

const saving = ref(false)

const handleSubmit = () => {
  saving.value = true

  // Build quality definitions from current quality items
  formData.value.qualities = buildQualityDefinitions()

  formData.value.preferredFormats = showPreferM4b.value && preferM4b.value ? ['m4b'] : []

  // Save custom group names
  formData.value.customGroupNames = Object.fromEntries(customGroupNames.value)

  // Validate at least one quality is selected
  if (!qualityItems.value.some((q) => q.enabled)) {
    toast.error('Validation', 'Please select at least one quality')
    saving.value = false
    return
  }

  // Validate cutoff quality if upgrades are enabled
  if (upgradesEnabled.value) {
    if (!formData.value.cutoffQuality) {
      toast.error('Validation', 'Please select an upgrade cutoff quality')
      saving.value = false
      return
    }

    if (!qualityItems.value.some((q) => q.id === formData.value.cutoffQuality && q.enabled)) {
      toast.error('Validation', 'Cutoff quality must be one of the enabled qualities')
      saving.value = false
      return
    }
  } else {
    // Clear cutoff if upgrades disabled
    formData.value.cutoffQuality = ''
  }

  emit('save', formData.value)
  saving.value = false
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}

.modal-content {
  background-color: #2a2a2a;
  border-radius: 6px;
  max-width: 800px;
  width: 100%;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 4px 6px rgba(0, 0, 0, 0.3);
}

.quality-profile-modal {
  max-width: 900px;
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem;
  border-bottom: 1px solid #444;
}

.modal-header h2 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.modal-body {
  padding: 1.5rem;
  overflow-y: auto;
  flex: 1;
}

.form-section h4 {
  margin: 1rem 0 0.5rem 0;
  color: #fff;
  font-size: 1rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.section-description {
  margin: 0.5rem 0 1rem 0;
  color: #999;
  font-size: 0.9rem;
  line-height: 1.4;
}

.form-group {
  margin-bottom: 1rem;
}

.form-group label {
  display: block;
  margin-bottom: 0.5rem;
  color: #ddd;
  font-weight: 500;
}

.form-group input[type='text'],
.form-group input[type='url'],
.form-group input[type='number'],
.form-group textarea,
.form-group select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 1rem;
}

.form-group textarea {
  resize: vertical;
  font-family: inherit;
}

.form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
}

.checkbox-label {
  display: flex;
  align-items: center;
  gap: 1rem;
  color: #ddd;
  cursor: pointer;
  user-select: none;
  padding: 0.5rem 0;
  text-align: left;
}

.checkbox-label input[type='checkbox'] {
  width: 18px;
  height: 18px;
  margin: 0;
  cursor: pointer;
  flex-shrink: 0;
  -webkit-appearance: none;
  appearance: none;
  background-color: #1a1a1a;
  border: 2px solid #555;
  border-radius: 6px;
  position: relative;
  transition: all 0.2s ease;
  vertical-align: sub;
}

.checkbox-label input[type='checkbox']:hover {
  border-color: var(--brand-focus);
}

.checkbox-label input[type='checkbox']:checked {
  background-color: var(--brand-focus);
  border-color: var(--brand-focus);
}

.checkbox-label input[type='checkbox']:checked::after {
  content: '';
  position: absolute;
  left: 5px;
  top: 2px;
  width: 4px;
  height: 8px;
  border: solid white;
  border-width: 0 2px 2px 0;
  transform: rotate(45deg);
}

.checkbox-label input[type='checkbox']:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.3);
  outline-offset: 2px;
}

.checkbox-label span {
  line-height: 1.4;
  font-size: 0.95rem;
  margin-left: 0.25rem;
}

.info-text {
  display: block;
  margin-top: 0.5rem;
  color: #999;
  font-size: 0.85rem;
  line-height: 1.4;
}

.info-text i {
  color: var(--brand-500);
}

.info-text-small {
  font-size: 0.85rem;
  color: #999;
  margin: 0.5rem 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

/* Subsection Titles */
.subsection-title {
  margin: 1.5rem 0 1rem 0;
  color: #fff;
  font-size: 1rem;
  font-weight: 600;
}

/* Codec Selection */
.codec-selection {
  margin-bottom: 2rem;
  padding-bottom: 2rem;
  border-bottom: 1px solid #333;
}

.codec-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 0.75rem;
  margin-top: 1rem;
}

.codec-checkbox {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  background-color: #1a1a1a;
  border: 2px solid #444;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
}

.codec-checkbox:hover {
  border-color: var(--brand-500);
  background-color: #222;
}

.codec-checkbox input[type='checkbox'] {
  width: 18px;
  height: 18px;
  cursor: pointer;
  accent-color: var(--brand-500);
}

.codec-label {
  flex: 1;
  color: #fff;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.lossless-badge {
  font-size: 0.75rem;
  padding: 0.15rem 0.5rem;
  background-color: rgba(52, 211, 153, 0.2);
  color: #34d399;
  border-radius: 4px;
  font-weight: 600;
  text-transform: uppercase;
}

/* Quality Groups Container */
.quality-groups-container {
  margin-bottom: 2rem;
  padding-bottom: 2rem;
  border-bottom: 1px solid #333;
}

.quality-groups {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  margin-top: 1rem;
}

.quality-group {
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  overflow: hidden;
}

.quality-group-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  background-color: #252525;
  border-bottom: 1px solid #333;
}

.group-icon {
  width: 20px;
  height: 20px;
  color: var(--brand-500);
}

.group-title {
  font-size: 0.95rem;
  font-weight: 600;
  color: #fff;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  flex: 1;
}

.group-name-input {
  flex: 1;
  font-size: 0.95rem;
  font-weight: 600;
  padding: 0.25rem 0.5rem;
  background-color: #1a1a1a;
  border: 1px solid var(--brand-500);
  border-radius: 4px;
  color: #fff;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.group-name-input:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 2px rgba(33, 150, 243, 0.2);
}

.edit-group-btn {
  background: none;
  border: none;
  color: #666;
  cursor: pointer;
  padding: 0.25rem;
  display: flex;
  align-items: center;
  transition: color 0.2s;
  margin-left: 0.5rem;
}

.edit-group-btn:hover {
  color: var(--brand-500);
}

.edit-group-btn svg {
  width: 16px;
  height: 16px;
}

.quality-items {
  display: flex;
  flex-direction: column;
}

.quality-item {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid #2a2a2a;
  cursor: move;
  transition: all 0.2s;
  background-color: #1a1a1a;
}

.quality-item:last-child {
  border-bottom: none;
}

.quality-item:hover {
  background-color: #222;
}

.quality-item:active {
  cursor: grabbing;
}

.drag-handle {
  width: 20px;
  height: 20px;
  color: #666;
  cursor: grab;
  flex-shrink: 0;
}

.drag-handle:hover {
  color: var(--brand-500);
}

.quality-label {
  flex: 1;
  color: #fff;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.bitrate-badge {
  font-size: 0.8rem;
  padding: 0.15rem 0.4rem;
  background-color: rgba(59, 130, 246, 0.2);
  color: #3b82f6;
  border-radius: 4px;
  font-weight: 600;
}

/* Cutoff Selection */
.cutoff-selection {
  margin-top: 1rem;
}

.cutoff-selection select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.95rem;
  cursor: pointer;
}

.cutoff-selection select:focus {
  outline: none;
  border-color: var(--brand-500);
}

/* OLD QUALITY STYLES (FOR REFERENCE - REMOVE ONCE VERIFIED) */
.quality-list-OLD {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  margin-bottom: 1rem;
}

.quality-group-title-OLD {
  margin: 0;
  color: #999;
  font-size: 0.85rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  padding: 0 0.75rem;
  padding-bottom: 0.5rem;
  border-bottom: 1px solid #333;
}

.quality-controls {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.priority-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ddd;
  font-size: 0.9rem;
}

.priority-input {
  width: 70px;
  padding: 0.4rem;
  background-color: #2a2a2a;
  border: 1px solid #555;
  border-radius: 6px;
  color: #fff;
}

.radio-label {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  color: #ddd;
  font-size: 0.9rem;
  cursor: pointer;
  padding: 0.25rem 0.5rem;
  border-radius: 4px;
  transition: background-color 0.2s;
}

.radio-label:hover {
  background-color: rgba(255, 152, 0, 0.1);
}

.radio-label:has(input[type='radio']:checked) {
  background-color: rgba(255, 152, 0, 0.2);
  border: 1px solid #ff9800;
}

.radio-label input[type='radio'] {
  cursor: pointer;
  width: 18px;
  height: 18px;
  accent-color: #ff9800;
}

.radio-label input[type='radio']:checked {
  accent-color: #ff9800;
}

.cutoff-text {
  color: #ff9800;
}

/* Tag Input */
.tag-input-group {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.tags-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  min-height: 2rem;
}

/* Collapsed state when no tags are present */
.tags-list.tags-list-empty {
  min-height: 0;
  height: 0;
  padding: 0;
  margin: 0;
  overflow: hidden;
  transition:
    height 0.12s ease,
    opacity 0.12s ease;
  opacity: 0;
}

.tag {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 0.8rem;
  background-color: var(--brand-500);
  color: #fff;
  border-radius: var(--btn-radius);
  font-size: 0.9rem;
}

.tag.removable {
  padding-right: 0.4rem;
}

.tag.positive {
  background-color: #4caf50;
}

.tag.required {
  background-color: #ff9800;
}

.tag.forbidden {
  background-color: #f44336;
}

.tag-remove {
  background: none;
  border: none;
  color: #fff;
  cursor: pointer;
  padding: 0.2rem;
  display: flex;
  align-items: center;
  justify-content: center;
  opacity: 0.8;
  transition: opacity 0.2s;
}

.tag-remove:hover {
  opacity: 1;
}

.tag-input {
  display: flex;
  gap: 0.5rem;
}

.tag-input input {
  flex: 1;
  padding: 0.6rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
}

.add-button {
  padding: 0.6rem 1rem;
  background-color: var(--brand-500);
  color: #fff;
  border: none;
  border-radius: var(--btn-radius);
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.3rem;
  transition: background-color 0.2s;
}

.add-button:hover {
  background-color: var(--brand-600);
}

.filter-group {
  margin-bottom: 1.5rem;
}

.filter-group:last-child {
  margin-bottom: 0;
}

/* Form Actions */
.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 1rem;
  margin-top: 2rem;
  padding-top: 1.5rem;
  border-top: 1px solid #444;
}

/* Button styles for cancel/submit are centralized in src/assets/modals.css */
/* Modal-specific parts moved to shared modals.css */

.quality-profile-modal {
  max-width: 900px;
}

.modal-header h2 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.modal-body {
  padding: 1.5rem;
  overflow-y: auto;
  flex: 1;
}
</style>
