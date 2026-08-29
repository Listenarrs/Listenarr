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
  <Modal :visible="isOpen" size="lg" @close="close">
    <template #header>
      <ModalHeader
        :title="`Edit Audiobook: ${audiobook?.title || 'Audiobook'}`"
        @close="close"
        :icon="PhPencil"
      />
    </template>

    <template #default>
      <ModalBody compact>
        <form @submit.prevent="handleSave" class="edit-form form-body">
          <!-- Monitored Status -->
          <div class="form-group">
            <label class="form-label">
              <PhEye></PhEye>
              Monitored Status
            </label>
            <div class="form-control-card">
              <div class="radio-group">
                <RadioCard
                  v-model="formData.monitored"
                  :value="true"
                  name="monitored"
                  title="Monitored"
                  description="Automatically search for and upgrade releases"
                />
                <RadioCard
                  v-model="formData.monitored"
                  :value="false"
                  name="monitored"
                  title="Unmonitored"
                  description="Do not search for new releases"
                />
              </div>
              <p class="help-text">
                Monitored audiobooks will be automatically upgraded when better quality releases are
                found
              </p>
            </div>
          </div>

          <!-- Metadata -->
          <div class="form-group">
            <label class="form-label" for="metadata-title">
              <PhInfo></PhInfo>
              Metadata
            </label>
            <div class="form-control-card">
              <div class="metadata-grid">
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-title">Title</label>
                  <input
                    id="metadata-title"
                    v-model="formData.title"
                    type="text"
                    class="form-input"
                    placeholder="Audiobook title"
                  />
                </div>
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-subtitle">Subtitle</label>
                  <input
                    id="metadata-subtitle"
                    v-model="formData.subtitle"
                    type="text"
                    class="form-input"
                    placeholder="Optional subtitle"
                  />
                </div>
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-authors">Authors</label>
                  <div class="tags-container author-tags-editor">
                    <div class="tags-list">
                      <span
                        v-for="(author, index) in formData.authors"
                        :key="`${author}-${index}`"
                        class="tag-item"
                      >
                        {{ author }}
                        <button
                          type="button"
                          class="tag-remove"
                          @click="removeAuthor(index)"
                          title="Remove author"
                        >
                          <PhX :size="16" weight="bold"></PhX>
                        </button>
                      </span>
                      <span v-if="formData.authors.length === 0" class="tags-empty">
                        No authors added yet
                      </span>
                    </div>
                    <div class="tag-input-group">
                      <input
                        id="metadata-authors"
                        v-model="newAuthor"
                        type="text"
                        class="tag-input"
                        placeholder="Add an author..."
                        @keypress.enter.prevent="addAuthor"
                      />
                      <button
                        type="button"
                        @click="addAuthor"
                        class="icon-btn btn-primary btn-add-tag"
                        :disabled="!newAuthor.trim()"
                        title="Add author"
                        aria-label="Add author"
                      >
                        <PhPlus :size="16"></PhPlus>
                      </button>
                    </div>
                  </div>
                </div>
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-narrators">Narrators</label>
                  <div class="tags-container narrator-tags-editor">
                    <div class="tags-list">
                      <span
                        v-for="(narrator, index) in formData.narrators"
                        :key="`${narrator}-${index}`"
                        class="tag-item"
                      >
                        {{ narrator }}
                        <button
                          type="button"
                          class="tag-remove"
                          @click="removeNarrator(index)"
                          title="Remove narrator"
                        >
                          <PhX :size="16" weight="bold"></PhX>
                        </button>
                      </span>
                      <span v-if="formData.narrators.length === 0" class="tags-empty">
                        No narrators added yet
                      </span>
                    </div>
                    <div class="tag-input-group">
                      <input
                        id="metadata-narrators"
                        v-model="newNarrator"
                        type="text"
                        class="tag-input"
                        placeholder="Add a narrator..."
                        @keypress.enter.prevent="addNarrator"
                      />
                      <button
                        type="button"
                        @click="addNarrator"
                        class="icon-btn btn-primary btn-add-tag"
                        :disabled="!newNarrator.trim()"
                        title="Add narrator"
                        aria-label="Add narrator"
                      >
                        <PhPlus :size="16"></PhPlus>
                      </button>
                    </div>
                  </div>
                </div>
                <div class="metadata-field metadata-field--full">
                  <label class="field-label" for="metadata-description">Description</label>
                  <textarea
                    id="metadata-description"
                    v-model="formData.description"
                    rows="5"
                    class="form-input metadata-textarea"
                    placeholder="Book description"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="metadata-publisher">Publisher</label>
                  <input
                    id="metadata-publisher"
                    v-model="formData.publisher"
                    type="text"
                    class="form-input"
                    placeholder="Publisher"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="metadata-language">Language</label>
                  <input
                    id="metadata-language"
                    v-model="formData.language"
                    type="text"
                    class="form-input"
                    placeholder="Language"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="metadata-published-date">Release Date</label>
                  <input
                    id="metadata-published-date"
                    v-model="formData.publishedDate"
                    type="text"
                    class="form-input"
                    placeholder="YYYY-MM-DD"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="metadata-publish-year">Publish Year</label>
                  <input
                    id="metadata-publish-year"
                    v-model="formData.publishYear"
                    type="text"
                    class="form-input"
                    placeholder="YYYY"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="metadata-runtime"
                    >Listening Length (minutes)</label
                  >
                  <input
                    id="metadata-runtime"
                    v-model="formData.runtime"
                    type="number"
                    min="0"
                    class="form-input"
                    placeholder="e.g. 600"
                  />
                </div>
                <div class="metadata-field">
                  <label class="field-label" for="edition">Edition</label>
                  <input
                    id="edition"
                    v-model="formData.edition"
                    type="text"
                    class="form-input"
                    placeholder="e.g. Revised Edition"
                  />
                  <p class="help-text">
                    Optional user-defined label exposed as <code>{Edition}</code> in file and folder
                    naming patterns.
                  </p>
                </div>
                <div class="metadata-field metadata-field--full">
                  <label class="field-label">Series Memberships</label>
                  <div class="series-memberships-editor">
                    <div
                      v-for="(membership, index) in formData.seriesMemberships"
                      :key="membership.localKey"
                      class="series-membership-row"
                    >
                      <div class="series-membership-fields">
                        <div class="series-membership-field series-membership-field--name">
                          <label class="field-label sr-only" :for="`metadata-series-name-${index}`">
                            Series name
                          </label>
                          <input
                            :id="`metadata-series-name-${index}`"
                            v-model="membership.seriesName"
                            type="text"
                            class="form-input"
                            placeholder="Series name"
                          />
                        </div>
                        <div class="series-membership-field series-membership-field--number">
                          <label
                            class="field-label sr-only"
                            :for="`metadata-series-number-${index}`"
                          >
                            Number in series
                          </label>
                          <input
                            :id="`metadata-series-number-${index}`"
                            v-model="membership.seriesNumber"
                            type="text"
                            class="form-input"
                            placeholder="e.g. 1"
                          />
                        </div>
                      </div>
                      <div class="series-membership-actions">
                        <label class="series-primary-toggle">
                          <input
                            type="radio"
                            name="primary-series-membership"
                            :checked="membership.isPrimary"
                            @change="setPrimarySeriesMembership(index)"
                          />
                          <span>Primary</span>
                        </label>
                        <button
                          type="button"
                          class="icon-btn btn-secondary"
                          @click="removeSeriesMembership(index)"
                          :disabled="
                            formData.seriesMemberships.length === 1 &&
                            !membership.seriesName.trim() &&
                            !membership.seriesNumber.trim()
                          "
                          title="Remove series membership"
                          aria-label="Remove series membership"
                        >
                          <PhX :size="16" weight="bold" />
                        </button>
                      </div>
                    </div>
                    <div
                      v-if="formData.seriesMemberships.length === 0"
                      class="series-memberships-empty"
                    >
                      No series memberships added yet
                    </div>
                    <button
                      type="button"
                      class="btn btn-secondary btn-add-series-membership"
                      @click="addSeriesMembership"
                    >
                      <PhPlus :size="16" />
                      Add Series
                    </button>
                  </div>
                  <p class="help-text">
                    A book can belong to multiple series. Mark one entry as primary for naming and
                    legacy compatibility.
                  </p>
                </div>
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-genres">Genres</label>
                  <div class="tags-container genre-tags-editor">
                    <div class="tags-list">
                      <span
                        v-for="(genre, index) in formData.genres"
                        :key="`${genre}-${index}`"
                        class="tag-item"
                      >
                        {{ genre }}
                        <button
                          type="button"
                          class="tag-remove"
                          @click="removeGenre(index)"
                          title="Remove genre"
                        >
                          <PhX :size="16" weight="bold"></PhX>
                        </button>
                      </span>
                      <span v-if="formData.genres.length === 0" class="tags-empty">
                        No genres added yet
                      </span>
                    </div>
                    <div class="tag-input-group">
                      <input
                        id="metadata-genres"
                        v-model="newGenre"
                        type="text"
                        class="tag-input"
                        placeholder="Add a genre..."
                        @keypress.enter.prevent="addGenre"
                      />
                      <button
                        type="button"
                        @click="addGenre"
                        class="icon-btn btn-primary btn-add-tag"
                        :disabled="!newGenre.trim()"
                        title="Add genre"
                        aria-label="Add genre"
                      >
                        <PhPlus :size="16"></PhPlus>
                      </button>
                    </div>
                  </div>
                </div>
                <div class="metadata-field metadata-field--wide">
                  <label class="field-label" for="metadata-image-url">Cover Image URL</label>
                  <input
                    id="metadata-image-url"
                    v-model="formData.imageUrl"
                    type="text"
                    class="form-input"
                    placeholder="https://..."
                  />
                </div>
              </div>
            </div>
          </div>

          <!-- Quality Profile -->
          <div class="form-group">
            <label class="form-label" for="quality-profile">
              <PhStar></PhStar>
              Quality Profile
            </label>
            <div class="form-control-card">
              <select id="quality-profile" v-model="formData.qualityProfileId" class="form-select">
                <option :value="null">Use Default Profile</option>
                <option v-for="profile in qualityProfiles" :key="profile.id" :value="profile.id">
                  {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
                </option>
              </select>
              <p class="help-text">
                Controls which quality standards to use for downloads and upgrades. Leave as "Use
                Default Profile" to automatically use the default profile.
              </p>
            </div>
          </div>

          <!-- Destination / Base Path -->
          <div class="form-group">
            <label class="form-label">
              <PhFolder></PhFolder>
              Destination Folder
            </label>
            <div class="form-control-card">
              <div class="destination-display">
                <!-- Read-only display mode -->
                <div v-if="!editingDestination" class="destination-readonly">
                  <input
                    type="text"
                    :value="displayDestinationPath || 'No destination set'"
                    class="form-input readonly-input"
                    readonly
                    disabled
                  />
                  <button
                    type="button"
                    class="icon-btn btn-primary btn-edit-destination"
                    @click="startEditingDestination"
                    :disabled="
                      filesystemReadinessStore.filesystemReady === false ||
                      Boolean(moveRecoveryState?.hasUnresolvedMove)
                    "
                    :title="
                      filesystemReadinessStore.filesystemReady === false
                        ? 'Available after library filesystem initialization completes'
                        : moveRecoveryState?.hasUnresolvedMove
                          ? 'Resolve the interrupted move before changing the destination'
                          : 'Edit destination'
                    "
                    aria-label="Edit destination"
                  >
                    <PhPencil :size="16"></PhPencil>
                  </button>
                </div>
                <!-- Edit mode -->
                <div v-else class="destination-edit">
                  <div class="destination-row">
                    <div class="root-select">
                      <RootFolderSelect
                        :hideLabel="true"
                        :inline="true"
                        v-model:rootId="selectedRootId"
                      />
                    </div>

                    <input
                      type="text"
                      v-model="formData.relativePath"
                      class="form-input relative-input"
                      placeholder="e.g. Author/Title"
                    />

                    <div class="destination-actions">
                      <button
                        type="button"
                        class="btn icon-btn btn-secondary btn-sm"
                        @click="editingDestination = false"
                        aria-label="Cancel destination edit"
                        title="Cancel"
                      >
                        <PhX :size="16"></PhX>
                      </button>
                      <button
                        type="button"
                        class="btn icon-btn btn-primary btn-sm"
                        @click="finishEditingDestination"
                        :disabled="Boolean(destinationPathValidationError)"
                        aria-label="Save destination"
                        :title="destinationPathValidationError || 'Done'"
                      >
                        <PhCheck :size="16"></PhCheck>
                      </button>
                    </div>
                  </div>
                </div>
                <p class="help-text">
                  <span v-if="!editingDestination && !moveRecoveryState?.hasUnresolvedMove"
                    >Click the edit button to change the destination folder.</span
                  >
                  <span v-else-if="editingDestination">
                    <strong>Choose a configured root folder</strong> from the dropdown. The right
                    field is the path relative to that root.
                  </span>
                </p>
                <div
                  v-if="moveRecoveryState?.hasUnresolvedMove"
                  class="move-recovery-notice"
                  data-testid="move-recovery-notice"
                >
                  <PhWarning :size="18" />
                  <div class="move-recovery-content">
                    <strong>
                      {{
                        moveRecoveryState.canRetry
                          ? 'An interrupted move needs to be resumed.'
                          : 'A previous move needs attention.'
                      }}
                    </strong>
                    <span v-if="moveRecoveryState.requestedPath">
                      Destination: <code>{{ moveRecoveryState.requestedPath }}</code>
                    </span>
                    <span v-if="moveRecoveryState.error">{{ moveRecoveryState.error }}</span>
                  </div>
                  <button
                    v-if="moveRecoveryState.canRetry && moveRecoveryState.jobId"
                    type="button"
                    class="btn btn-primary btn-sm"
                    data-testid="resume-move-button"
                    :disabled="resumingMove || filesystemReadinessStore.filesystemReady === false"
                    @click="resumeInterruptedMove"
                  >
                    <PhSpinner v-if="resumingMove" class="ph-spin" />
                    {{ resumingMove ? 'Resuming...' : 'Resume move' }}
                  </button>
                </div>
                <div
                  v-if="editingDestination && editDestinationPath"
                  class="destination-preview"
                  data-testid="effective-destination"
                >
                  <span>Effective destination:</span>
                  <code>{{ editDestinationPath }}</code>
                </div>
                <div v-if="destinationPathValidationError" class="path-validation-error">
                  <PhWarning :size="16" />
                  <span>{{ destinationPathValidationError }}</span>
                </div>
                <!-- Path length warning -->
                <div v-if="destinationPathWarning" class="path-length-warning">
                  <PhWarning :size="16" />
                  <span>{{ destinationPathWarning }}</span>
                </div>
              </div>
            </div>
          </div>

          <!-- Tags -->
          <div class="form-group">
            <label class="form-label">
              <PhTag></PhTag>
              Tags
            </label>
            <div class="form-control-card">
              <div class="tags-container">
                <div class="tags-list">
                  <span v-for="(tag, index) in formData.tags" :key="index" class="tag-item">
                    {{ tag }}
                    <button
                      type="button"
                      class="tag-remove"
                      @click="removeTag(index)"
                      title="Remove tag"
                    >
                      <PhX :size="16" weight="bold"></PhX>
                    </button>
                  </span>
                  <span v-if="formData.tags.length === 0" class="tags-empty">
                    No tags added yet
                  </span>
                </div>
                <div class="tag-input-group">
                  <input
                    type="text"
                    v-model="newTag"
                    @keypress.enter.prevent="addTag"
                    placeholder="Add a tag..."
                    class="tag-input"
                  />
                  <button
                    type="button"
                    @click="addTag"
                    class="icon-btn btn-primary btn-add-tag"
                    :disabled="!newTag.trim()"
                    title="Add tag"
                    aria-label="Add tag"
                  >
                    <PhPlus :size="16"></PhPlus>
                  </button>
                </div>
              </div>
              <p class="help-text">Custom tags for organizing and filtering audiobooks</p>
            </div>
          </div>

          <!-- External Identifiers -->
          <div class="form-group">
            <label class="form-label">
              <PhLink></PhLink>
              Identifiers
            </label>
            <div class="form-control-card">
              <div class="identifier-list">
                <div
                  v-for="(identifier, index) in formData.identifiers"
                  :key="identifier.localKey"
                  class="identifier-row"
                >
                  <select
                    v-model="identifier.type"
                    class="form-select identifier-type"
                    @change="onIdentifierTypeChanged(index)"
                  >
                    <option value="Asin">ASIN</option>
                    <option value="Isbn">ISBN</option>
                    <option value="OpenLibraryId">OpenLibrary ID</option>
                  </select>

                  <input
                    v-model="identifier.value"
                    type="text"
                    class="form-input identifier-value"
                    :placeholder="
                      identifier.type === 'Asin'
                        ? 'B0XXXXXXXX'
                        : identifier.type === 'Isbn'
                          ? '978... / 0...'
                          : 'OL12345M'
                    "
                  />

                  <input
                    v-if="identifier.type === 'Asin'"
                    v-model="identifier.region"
                    type="text"
                    class="form-input identifier-region"
                    placeholder="region"
                    maxlength="8"
                  />
                  <div v-else class="identifier-region identifier-region--spacer"></div>

                  <label class="identifier-primary">
                    <input
                      type="checkbox"
                      :checked="identifier.isPrimary"
                      @change="setPrimaryIdentifier(index)"
                    />
                    Primary
                  </label>

                  <span class="identifier-source">{{ identifier.source }}</span>

                  <button
                    type="button"
                    class="icon-btn btn-secondary btn-remove-identifier"
                    @click="removeIdentifier(index)"
                    title="Remove identifier"
                    aria-label="Remove identifier"
                  >
                    <PhX :size="16"></PhX>
                  </button>
                </div>

                <div v-if="formData.identifiers.length === 0" class="identifiers-empty">
                  No identifiers added yet
                </div>
              </div>

              <div class="identifier-actions">
                <button
                  type="button"
                  class="btn btn-secondary btn-sm"
                  @click="addIdentifier('Asin')"
                >
                  <PhPlus :size="14"></PhPlus>
                  Add ASIN
                </button>
                <button
                  type="button"
                  class="btn btn-secondary btn-sm"
                  @click="addIdentifier('Isbn')"
                >
                  <PhPlus :size="14"></PhPlus>
                  Add ISBN
                </button>
                <button
                  type="button"
                  class="btn btn-secondary btn-sm"
                  @click="addIdentifier('OpenLibraryId')"
                >
                  <PhPlus :size="14"></PhPlus>
                  Add OLID
                </button>
              </div>

              <p class="help-text">
                Add alternate or corrected identifiers to improve metadata and cover lookup. ASINs
                may include a region. Only one primary identifier is allowed per type.
              </p>
            </div>
          </div>

          <!-- Content Flags -->
          <div class="form-group form-group--compact">
            <label class="form-label">
              <PhInfo></PhInfo>
              Content Information
            </label>
            <div class="form-control-card">
              <div class="checkbox-group">
                <Checkbox v-model="formData.abridged">
                  <strong>Abridged</strong>
                  <small>This is an abridged (shortened) version</small>
                </Checkbox>
                <Checkbox v-model="formData.explicit">
                  <strong>Explicit Content</strong>
                  <small>Contains explicit language or mature content</small>
                </Checkbox>
              </div>
            </div>
          </div>

          <button type="submit" style="display: none" aria-hidden="true"></button>
        </form>
      </ModalBody>
    </template>

    <template #footer>
      <button
        type="button"
        class="btn btn-secondary cancel-button"
        @click="close"
        title="Close"
        aria-label="Close"
      >
        Close
      </button>
      <button
        type="button"
        class="btn btn-primary"
        @click="handleSave"
        :disabled="saving || !hasChanges || Boolean(destinationPathValidationError)"
        :title="saving ? 'Saving...' : destinationPathValidationError || 'Save'"
        :aria-label="saving ? 'Saving' : 'Save'"
      >
        <span v-if="saving"><PhSpinner class="ph-spin"></PhSpinner> Saving...</span>
        <span v-else>Save</span>
      </button>
    </template>
  </Modal>

  <MoveAudiobookModal
    :visible="showMoveConfirm"
    :pendingMove="pendingMove"
    :allowDeleteEmpty="!sourceIsManagedRoot"
    v-model:moveFiles="modalMoveFiles"
    v-model:deleteEmpty="modalDeleteEmpty"
    @cancel="cancelMoveConfirm"
    @confirm="handleMoveConfirm"
  />
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue'
import { useToast } from '@/services/toastService'
import { apiService } from '@/services/api'
import { getApiValidationError } from '@/services/apiErrors'
import { logger } from '@/utils/logger'
import type {
  Audiobook,
  AudiobookUpdateRequest,
  AudiobookSeriesMembership,
  QualityProfile,
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  AudiobookExternalIdentifierType,
  AudiobookExternalIdentifierSource,
} from '@/types'
import {
  PhX,
  PhPencil,
  PhSpinner,
  PhCheck,
  PhFolder,
  PhPlus,
  PhInfo,
  PhEye,
  PhStar,
  PhTag,
  PhLink,
  PhWarning,
} from '@phosphor-icons/vue'
import { useConfigurationStore } from '@/stores/configuration'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import RadioCard from '@/components/settings/RadioCard.vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
// FormRow and CheckboxCard not used in this component script; UI uses local markup
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useMoveJobsStore, type MoveRecoveryState } from '@/stores/moveJobs'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { usePathLengthCheck } from '@/composables/usePathLengthCheck'
import {
  confirmDetectedMutationSemantics,
  confirmMutationSemanticsForBlockedOperation,
  findMutationSemanticsRoot,
  refreshAudiobookFileIdentity,
} from '@/composables/useMutationSemanticsConfirmation'

// Diagnostic: surface undefined imports that can cause `Invalid vnode type` warnings
if (typeof window !== 'undefined') {
  try {
    console.debug('EditAudiobookModal imports', {
      ModalExists: typeof Modal !== 'undefined',
      ModalBodyExists: typeof ModalBody !== 'undefined',
      RootFolderSelectExists: typeof RootFolderSelect !== 'undefined',
    })
  } catch {
    /* noop */
  }
}

interface Props {
  isOpen: boolean
  audiobook: Audiobook | null
}

interface FormData {
  monitored: boolean
  qualityProfileId: number | null
  title: string
  subtitle: string
  authors: string[]
  narrators: string[]
  description: string
  publisher: string
  language: string
  publishedDate: string
  publishYear: string
  runtime: string
  edition: string
  seriesMemberships: EditableSeriesMembership[]
  genres: string[]
  imageUrl: string
  tags: string[]
  identifiers: EditableIdentifierRow[]
  abridged: boolean
  explicit: boolean
  basePath?: string | null
  relativePath?: string | null
}

interface EditableIdentifierRow {
  localKey: string
  type: AudiobookExternalIdentifierType
  value: string
  region?: string | null
  isPrimary: boolean
  source: AudiobookExternalIdentifierSource
}

interface EditableSeriesMembership {
  localKey: string
  id?: number
  seriesName: string
  seriesNumber: string
  seriesAsin?: string | null
  isPrimary: boolean
  sortOrder: number
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  saved: []
}>()

const qualityProfiles = ref<QualityProfile[]>([])
const configStore = useConfigurationStore()
const rootStore = useRootFoldersStore()
const moveJobsStore = useMoveJobsStore()
const filesystemReadinessStore = useFilesystemReadinessStore()
const moveRecoveryState = ref<MoveRecoveryState | null>(null)
const resumingMove = ref(false)
const selectedRootId = ref<number | null>(null)
const rootPath = ref<string | null>(null)
const unmanagedExistingDestination = ref(false)
const saving = ref(false)
const newAuthor = ref('')
const newNarrator = ref('')
const newTag = ref('')
const newGenre = ref('')
const editingDestination = ref(false)
const toast = useToast()
const originalIdentifierRows = ref<EditableIdentifierRow[]>([])
const isHydratingForm = ref(false)
const hasLocalEdits = ref(false)
const resolvedAudiobook = ref<Audiobook | null>(null)
const baselineAudiobook = computed(() => resolvedAudiobook.value ?? props.audiobook)

const formData = ref<FormData>({
  monitored: true,
  qualityProfileId: null,
  title: '',
  subtitle: '',
  authors: [],
  narrators: [],
  description: '',
  publisher: '',
  language: '',
  publishedDate: '',
  publishYear: '',
  runtime: '',
  edition: '',
  seriesMemberships: [],
  genres: [],
  imageUrl: '',
  tags: [],
  identifiers: [],
  abridged: false,
  explicit: false,
  basePath: null,
  relativePath: '',
})

function normalizeStringList(values: string[] | null | undefined): string[] {
  return (values || []).map((value) => value.trim()).filter((value) => value.length > 0)
}

function normalizeSeriesMembershipRows(
  memberships: AudiobookSeriesMembership[] | EditableSeriesMembership[] | null | undefined,
  legacySeries?: string | null,
  legacySeriesNumber?: string | null,
): EditableSeriesMembership[] {
  const normalized: EditableSeriesMembership[] = []
  const seen = new Set<string>()

  for (const [index, membership] of (memberships || []).entries()) {
    const seriesName = normalizeOptionalText(membership.seriesName)
    if (!seriesName) continue

    const seriesNumber = normalizeOptionalText(membership.seriesNumber)
    const seriesAsin = normalizeOptionalText(membership.seriesAsin)
    const dedupeKey = `${seriesName.toLowerCase()}|${seriesNumber.toLowerCase()}|${seriesAsin.toLowerCase()}`
    if (seen.has(dedupeKey)) continue
    seen.add(dedupeKey)

    normalized.push({
      localKey: `series-${membership.id ?? index}-${Math.random().toString(16).slice(2)}`,
      id: membership.id,
      seriesName,
      seriesNumber,
      seriesAsin: seriesAsin || null,
      isPrimary: Boolean(membership.isPrimary),
      sortOrder:
        typeof membership.sortOrder === 'number' ? membership.sortOrder : normalized.length,
    })
  }

  if (normalized.length === 0) {
    const fallbackSeries = normalizeOptionalText(legacySeries)
    if (fallbackSeries) {
      normalized.push({
        localKey: `series-legacy-${Math.random().toString(16).slice(2)}`,
        seriesName: fallbackSeries,
        seriesNumber: normalizeOptionalText(legacySeriesNumber),
        seriesAsin: null,
        isPrimary: true,
        sortOrder: 0,
      })
    }
  }

  if (normalized.length > 0 && !normalized.some((membership) => membership.isPrimary)) {
    normalized[0].isPrimary = true
  }

  return normalized
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((membership, index) => ({
      ...membership,
      sortOrder: index,
      isPrimary:
        membership.isPrimary || (index === 0 && !normalized.some((entry) => entry.isPrimary)),
    }))
}

function serializeSeriesMembershipRows(
  memberships: AudiobookSeriesMembership[] | EditableSeriesMembership[] | null | undefined,
  legacySeries?: string | null,
  legacySeriesNumber?: string | null,
): string {
  const normalized = normalizeSeriesMembershipRows(
    memberships,
    legacySeries,
    legacySeriesNumber,
  ).map((membership, index) => ({
    seriesName: membership.seriesName,
    seriesNumber: membership.seriesNumber,
    seriesAsin: normalizeOptionalText(membership.seriesAsin),
    isPrimary: Boolean(membership.isPrimary),
    sortOrder: index,
  }))

  return JSON.stringify(normalized)
}

function createEditableSeriesMembership(): EditableSeriesMembership {
  return {
    localKey: `series-new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    seriesName: '',
    seriesNumber: '',
    seriesAsin: null,
    isPrimary: false,
    sortOrder: formData.value.seriesMemberships.length,
  }
}

function derivePrimarySeriesMembership(memberships: EditableSeriesMembership[]): {
  series: string
  seriesNumber: string
} {
  const normalized = normalizeSeriesMembershipRows(memberships)
  const primary = normalized.find((membership) => membership.isPrimary) ?? normalized[0]

  return {
    series: primary?.seriesName || '',
    seriesNumber: primary?.seriesNumber || '',
  }
}

function splitStringList(value: string | null | undefined): string[] {
  return (value || '')
    .split(/[\r\n,]+/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
}

function normalizeLanguageText(value: string | null | undefined): string {
  const normalized = normalizeOptionalText(value)
  if (!normalized) return ''
  if (!/^[a-z\s]+$/i.test(normalized)) return normalized

  return normalized
    .split(/\s+/)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1).toLowerCase())
    .join(' ')
}

async function resolveAudiobookForEditing(audiobook: Audiobook): Promise<Audiobook> {
  if (typeof apiService.getAudiobook !== 'function') {
    return audiobook
  }

  try {
    const detailed = await apiService.getAudiobook(audiobook.id)
    return {
      ...audiobook,
      ...detailed,
    }
  } catch (error) {
    logger.debug('Failed to load full audiobook details for edit modal', error)
    return audiobook
  }
}

function hydrateFormFromAudiobook(audiobook: Audiobook) {
  formData.value = {
    monitored: Boolean(audiobook.monitored),
    qualityProfileId: audiobook.qualityProfileId ?? null,
    title: audiobook.title || '',
    subtitle: audiobook.subtitle || '',
    authors: [...(audiobook.authors || [])],
    narrators: [...(audiobook.narrators || [])],
    description: audiobook.description || '',
    publisher: audiobook.publisher || '',
    language: normalizeLanguageText(audiobook.language),
    publishedDate: audiobook.publishedDate || '',
    publishYear: audiobook.publishYear || '',
    runtime: audiobook.runtime != null ? String(audiobook.runtime) : '',
    edition: audiobook.edition || '',
    seriesMemberships: normalizeSeriesMembershipRows(
      audiobook.seriesMemberships,
      audiobook.series,
      audiobook.seriesNumber,
    ),
    genres: [...(audiobook.genres || [])],
    imageUrl: audiobook.imageUrl || '',
    tags: [...(audiobook.tags || [])],
    identifiers: [],
    abridged: Boolean(audiobook.abridged),
    explicit: Boolean(audiobook.explicit),
    basePath: audiobook.basePath ?? null,
    relativePath: null,
  }

  newAuthor.value = ''
  newNarrator.value = ''
  newGenre.value = ''
  newTag.value = ''
}

watch(
  formData,
  () => {
    if (props.isOpen && !isHydratingForm.value) {
      hasLocalEdits.value = true
    }
  },
  { deep: true },
)

async function refreshMoveRecoveryState(audiobookId: number) {
  try {
    const recovery = await moveJobsStore.getRecoveryStateForAudiobook(audiobookId)
    moveRecoveryState.value = recovery.hasUnresolvedMove ? recovery : null
    if (recovery.hasUnresolvedMove) {
      editingDestination.value = false
    }
  } catch (error) {
    logger.debug('Failed to load durable move recovery state', error)
    moveRecoveryState.value = null
  }
}

async function resumeInterruptedMove() {
  const audiobook = baselineAudiobook.value
  const recovery = moveRecoveryState.value
  if (!audiobook || !recovery?.jobId || !recovery.canRetry || resumingMove.value) return

  resumingMove.value = true
  try {
    const jobId = await moveJobsStore.requeueMoveJob(
      recovery.jobId,
      audiobook.id,
      recovery.requestedPath,
    )
    toast.info('Move resumed', `Move job ${jobId} was queued to resume its interrupted work.`)
    await refreshMoveRecoveryState(audiobook.id)
  } catch (error) {
    logger.error('Failed to resume interrupted move', error)
    const apiError = getApiValidationError(error)
    toast.error(
      'Move could not be resumed',
      apiError?.message || 'The interrupted move could not be requeued safely.',
    )
    await refreshMoveRecoveryState(audiobook.id)
  } finally {
    resumingMove.value = false
  }
}

async function syncFormFromAudiobook(audiobook: Audiobook, loadSupportingData: boolean) {
  isHydratingForm.value = true

  try {
    const resolved = await resolveAudiobookForEditing(audiobook)
    resolvedAudiobook.value = resolved
    hydrateFormFromAudiobook(resolved)

    if (loadSupportingData) {
      await loadData()
    }

    await initializeForm(resolved)
    await refreshMoveRecoveryState(resolved.id)
    hasLocalEdits.value = false
  } finally {
    await nextTick()
    isHydratingForm.value = false
  }
}

function normalizeNumericInput(value: string | null | undefined): string {
  return (value || '').trim()
}

function parseRuntimeInput(value: string | null | undefined): number | undefined {
  const normalized = normalizeNumericInput(value)
  if (!normalized) return undefined
  const parsed = Number.parseInt(normalized, 10)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : undefined
}

function serializeStringList(values: string[] | null | undefined): string {
  return JSON.stringify(normalizeStringList(values))
}

// In-component move confirmation modal state
const showMoveConfirm = ref(false)
const pendingMove = ref<{ original?: string; combined?: string } | null>(null)
const modalMoveFiles = ref(true)
const modalDeleteEmpty = ref(true)
let moveConfirmResolver:
  | ((r: { proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }) => void)
  | null = null

function askMoveConfirmation(original: string, combined: string) {
  modalMoveFiles.value = true
  modalDeleteEmpty.value = !sourceIsManagedRoot.value
  pendingMove.value = { original, combined }
  showMoveConfirm.value = true
  return new Promise<{ proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }>(
    (resolve) => {
      moveConfirmResolver = resolve
    },
  )
}

function cancelMoveConfirm() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: false, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingMove.value = null
}

function confirmChangeWithoutMoving() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingMove.value = null
}

function confirmMove() {
  if (moveConfirmResolver)
    moveConfirmResolver({
      proceed: true,
      moveFiles: Boolean(modalMoveFiles.value),
      deleteEmptySource: Boolean(modalDeleteEmpty.value && !sourceIsManagedRoot.value),
    })

  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingMove.value = null
}

function handleMoveConfirm(payload: { moveFiles?: boolean } | null | undefined) {
  if (payload?.moveFiles) confirmMove()
  else confirmChangeWithoutMoving()
}

function normalizeOptionalText(value: string | null | undefined): string {
  return (value || '').trim()
}

const hasChanges = computed(() => {
  const audiobook = baselineAudiobook.value
  if (!audiobook) return false

  const tagsChanged =
    JSON.stringify([...formData.value.tags].sort()) !==
    JSON.stringify([...(audiobook.tags || [])].sort())

  const basePathChanged = destinationBasePathChanged()

  const identifiersChanged =
    serializeIdentifierRows(formData.value.identifiers) !==
    serializeIdentifierRows(originalIdentifierRows.value)

  const runtimeChanged = (() => {
    const runtimeInput = normalizeNumericInput(formData.value.runtime)
    if (!runtimeInput) return false
    return runtimeInput !== normalizeNumericInput(audiobook.runtime?.toString())
  })()

  const seriesMembershipsChanged =
    serializeSeriesMembershipRows(formData.value.seriesMemberships) !==
    serializeSeriesMembershipRows(
      audiobook.seriesMemberships,
      audiobook.series,
      audiobook.seriesNumber,
    )

  return (
    formData.value.monitored !== Boolean(audiobook.monitored) ||
    formData.value.qualityProfileId !== (audiobook.qualityProfileId ?? null) ||
    normalizeOptionalText(formData.value.title) !== normalizeOptionalText(audiobook.title) ||
    normalizeOptionalText(formData.value.subtitle) !== normalizeOptionalText(audiobook.subtitle) ||
    serializeStringList(formData.value.authors) !== serializeStringList(audiobook.authors) ||
    serializeStringList(formData.value.narrators) !== serializeStringList(audiobook.narrators) ||
    normalizeOptionalText(formData.value.description) !==
      normalizeOptionalText(audiobook.description) ||
    normalizeOptionalText(formData.value.publisher) !==
      normalizeOptionalText(audiobook.publisher) ||
    normalizeLanguageText(formData.value.language) !== normalizeLanguageText(audiobook.language) ||
    normalizeOptionalText(formData.value.publishedDate) !==
      normalizeOptionalText(audiobook.publishedDate) ||
    normalizeOptionalText(formData.value.publishYear) !==
      normalizeOptionalText(audiobook.publishYear) ||
    runtimeChanged ||
    normalizeOptionalText(formData.value.edition) !== normalizeOptionalText(audiobook.edition) ||
    seriesMembershipsChanged ||
    serializeStringList(formData.value.genres) !== serializeStringList(audiobook.genres) ||
    normalizeOptionalText(formData.value.imageUrl) !== normalizeOptionalText(audiobook.imageUrl) ||
    tagsChanged ||
    identifiersChanged ||
    formData.value.abridged !== Boolean(audiobook.abridged) ||
    formData.value.explicit !== Boolean(audiobook.explicit) ||
    basePathChanged
  )
})

watch(
  () => [props.isOpen, props.audiobook] as const,
  async ([isOpen, audiobook], previous) => {
    if (isOpen && audiobook) {
      const [wasOpen, previousAudiobook] = previous ?? []
      const isFreshOpen = !wasOpen || previousAudiobook?.id !== audiobook.id

      if (isFreshOpen) {
        await syncFormFromAudiobook(audiobook, true)
      } else if (!hasLocalEdits.value) {
        await syncFormFromAudiobook(audiobook, false)
      }
    } else if (!isOpen) {
      hasLocalEdits.value = false
      resolvedAudiobook.value = null
      moveRecoveryState.value = null
      resumingMove.value = false
    }
  },
  { immediate: true },
)

async function loadData() {
  try {
    // Load quality profiles
    qualityProfiles.value = await apiService.getQualityProfiles()

    // Load root folders from settings store
    await configStore.loadApplicationSettings()
    await rootStore.load()

    const appSettings = configStore.applicationSettings
    if (appSettings && appSettings.outputPath) {
      // Fallback default
      rootPath.value = appSettings.outputPath
    }

    // If there are named root folders, prefer them
    if (rootStore.folders.length > 0) {
      // Use default root if any
      const def = rootStore.folders.find((f) => f.isDefault) || rootStore.folders[0]
      rootPath.value = def?.path || rootPath.value
      // pre-select default
      selectedRootId.value = def?.id ?? null
    } else {
      selectedRootId.value = null
    }
  } catch (error) {
    console.error('Failed to load edit data:', error)
  }
}

async function initializeForm(audiobook: Audiobook) {
  unmanagedExistingDestination.value = false

  // Determine which configured root owns the existing base path. Legacy paths
  // outside every configured root remain visible, but cannot be reused as a
  // destination authority.
  if (audiobook.basePath && rootStore.folders.length > 0) {
    const matchingRoot = rootStore.folders
      .filter((folder) => {
        const pathKind = rootFolderPathKind(folder)
        const caseSensitivity = folder.resolvedCaseSensitivity ?? 'Unknown'
        return (
          pathsEqual(audiobook.basePath, folder.path, pathKind, caseSensitivity) ||
          pathIsInside(audiobook.basePath, folder.path, pathKind, caseSensitivity)
        )
      })
      .sort(
        (first, second) =>
          trimTrailingDirectorySeparators(second.path, rootFolderPathKind(second)).length -
          trimTrailingDirectorySeparators(first.path, rootFolderPathKind(first)).length,
      )[0]

    if (matchingRoot) {
      selectedRootId.value = matchingRoot.id
    } else {
      selectedRootId.value =
        (rootStore.folders.find((folder) => folder.isDefault) ?? rootStore.folders[0])?.id ?? null
      unmanagedExistingDestination.value = true
    }
  } else if (audiobook.basePath) {
    const outputPath = rootPath.value
    const pathKind = detectPathKind(outputPath)
    const isInsideOutputPath =
      Boolean(outputPath) &&
      (pathsEqual(audiobook.basePath, outputPath, pathKind) ||
        pathIsInside(audiobook.basePath, outputPath, pathKind))

    selectedRootId.value = null
    unmanagedExistingDestination.value = !isInsideOutputPath
  } else {
    selectedRootId.value = null
  }

  try {
    const chosenRoot = resolveSelectedRootPath()
    formData.value.relativePath =
      formData.value.basePath && chosenRoot && !unmanagedExistingDestination.value
        ? deriveRelativeFromBase(
            formData.value.basePath,
            chosenRoot,
            selectedDestinationCaseSensitivity(),
            selectedDestinationPathKind(),
          )
        : ''

    // Without a named root, expose the relative destination editor immediately
    // only when the stored path is already managed. Legacy unmanaged paths stay
    // read-only until the user explicitly chooses to relocate them.
    if (rootStore.folders.length === 0 && !unmanagedExistingDestination.value) {
      editingDestination.value = true
    }

    await loadIdentifiers()
    return
  } catch (err) {
    logger.debug('Preview path unavailable:', err)
  }
  await loadIdentifiers()
}

import {
  toForward,
  trimTrailingDirectorySeparators,
  normalizeForCompare,
  isRootedPath,
  joinPaths,
  validateLibraryDestinationPath,
  detectPathKind,
  pathsEqual,
  pathIsInside,
  type PathKind,
  type PathCaseSensitivity,
} from '@/utils/path'

function resolveSelectedRootPath(): string | null {
  if (selectedRootId.value && selectedRootId.value > 0) {
    const r = rootStore.folders.find((f) => f.id === selectedRootId.value)
    return r?.path ?? (rootPath.value || null)
  }
  return rootPath.value || null
}

function rootFolderPathKind(folder: {
  path: string
  pathSyntax?: 'Windows' | 'Unix' | null
}): PathKind {
  if (folder.pathSyntax === 'Windows') return 'windows'
  if (folder.pathSyntax === 'Unix') return 'unix'
  return detectPathKind(folder.path)
}

function selectedDestinationPathKind(): PathKind {
  if (selectedRootId.value && selectedRootId.value > 0) {
    const folder = rootStore.folders.find((item) => item.id === selectedRootId.value)
    if (folder) return rootFolderPathKind(folder)
  }

  const root =
    resolveSelectedRootPath() || rootPath.value || baselineAudiobook.value?.basePath || ''
  return detectPathKind(root)
}

function selectedDestinationCaseSensitivity() {
  if (selectedRootId.value && selectedRootId.value > 0) {
    return (
      rootStore.folders.find((folder) => folder.id === selectedRootId.value)
        ?.resolvedCaseSensitivity ?? 'Unknown'
    )
  }

  return 'Unknown' as const
}

function destinationBasePathChanged(): boolean {
  if (unmanagedExistingDestination.value && !editingDestination.value) return false

  const destination = combinedBasePath() || ''
  const source = baselineAudiobook.value?.basePath || ''
  if (!destination && !source) return false
  if (!destination || !source) return true
  return !pathsEqual(
    destination,
    source,
    selectedDestinationPathKind(),
    selectedDestinationCaseSensitivity(),
  )
}

const sourceIsManagedRoot = computed(() => {
  const source = baselineAudiobook.value?.basePath
  if (!source) return false

  return rootStore.folders.some((folder) =>
    pathsEqual(
      source,
      folder.path,
      rootFolderPathKind(folder),
      folder.resolvedCaseSensitivity ?? 'Unknown',
    ),
  )
})

function combinedBasePath(): string | null {
  const r = resolveSelectedRootPath() || ''
  const rel = formData.value.relativePath || ''
  if (!r && !rel) return null
  if (!r) return rel
  if (!rel) return r
  return joinPaths(r, rel, selectedDestinationPathKind())
}

// Path-length warning and validation for the destination path
const editDestinationPath = computed(() => combinedBasePath() || '')
const displayDestinationPath = computed(() =>
  unmanagedExistingDestination.value
    ? baselineAudiobook.value?.basePath || ''
    : editDestinationPath.value,
)
const serverDestinationValidationError = ref<string | null>(null)
const { pathLengthWarning: destinationPathWarning } = usePathLengthCheck(editDestinationPath)
const destinationPathValidationError = computed(() => {
  if (serverDestinationValidationError.value) return serverDestinationValidationError.value
  if (unmanagedExistingDestination.value && !editingDestination.value) return null
  if (unmanagedExistingDestination.value && !(formData.value.relativePath || '').trim()) {
    return 'Enter a path relative to the selected configured root folder.'
  }

  const destination = editDestinationPath.value
  const source = baselineAudiobook.value?.basePath || ''
  const pathKind = selectedDestinationPathKind()
  const relativePath = formData.value.relativePath || ''
  if (relativePath && isRootedPath(relativePath, pathKind)) {
    return 'Enter a path relative to the selected configured root folder.'
  }

  const basePathChanged = destinationBasePathChanged()

  return validateLibraryDestinationPath(destination, {
    pathKind,
    caseSensitivity: selectedDestinationCaseSensitivity(),
    sourcePath: basePathChanged ? source : null,
    allowFileSystemRoot: false,
  })
})

watch(editDestinationPath, () => {
  serverDestinationValidationError.value = null
})

// Helper: derive relative path from full base and configured root (moved to module scope so it can be reused)
function deriveRelativeFromBase(
  base: string | null | undefined,
  root: string | null | undefined,
  caseSensitivity: PathCaseSensitivity = 'Unknown',
  resolvedPathKind: PathKind = 'unknown',
): string {
  if (!base || !root) return ''

  const pathKind = resolvedPathKind === 'unknown' ? detectPathKind(root) : resolvedPathKind
  const normBase = pathKind === 'windows' ? toForward(base) : base
  const normRoot = pathKind === 'windows' ? toForward(root) : root
  const rootWithSlash = normRoot.endsWith('/') ? normRoot : normRoot + '/'

  if (
    normalizeForCompare(normBase, pathKind, caseSensitivity) ===
    normalizeForCompare(normRoot, pathKind, caseSensitivity)
  )
    return ''
  if (
    normalizeForCompare(normBase, pathKind, caseSensitivity).startsWith(
      normalizeForCompare(rootWithSlash, pathKind, caseSensitivity),
    )
  ) {
    const rel = normBase.slice(rootWithSlash.length).replace(/^\/+/, '')
    const useBackslash = pathKind === 'windows' && root.includes('\\')
    return useBackslash ? rel.replace(/\//g, '\\') : rel
  }

  // Paths outside configured roots are not valid destination authority.
  return ''
}

function previewPath() {
  try {
    const chosenRoot = resolveSelectedRootPath() || rootPath.value

    if (formData.value.basePath && chosenRoot) {
      formData.value.relativePath = deriveRelativeFromBase(
        formData.value.basePath,
        chosenRoot,
        selectedDestinationCaseSensitivity(),
        selectedDestinationPathKind(),
      )
    } else {
      formData.value.relativePath = ''
    }
  } catch (err) {
    logger.debug('Preview path unavailable:', err)
    formData.value.relativePath = ''
  }
}

function startEditingDestination() {
  // Only derive/overwrite the relative path if there isn't already a user-provided
  // unsaved relative path. This preserves what the user typed when toggling Done
  // and Edit back and forth before saving the whole audiobook.
  if (!formData.value.relativePath) {
    previewPath()
  }
  editingDestination.value = true
}

/**
 * Normalize the relative path when the user clicks Done so that the input
 * shows a path relative to the selected root (when possible) instead of
 * an absolute/full path. This makes the UI stable when toggling edit mode.
 */
function finishEditingDestination() {
  try {
    const chosenRoot = resolveSelectedRootPath() || rootPath.value
    const val = formData.value.relativePath || ''

    if (!chosenRoot) {
      toast.error(
        'Invalid destination',
        'Configure a root folder or output path before changing the destination.',
      )
      return
    }

    // The destination field is strictly relative to the selected configured root.
    formData.value.relativePath = val

    if (destinationPathValidationError.value) {
      toast.error('Invalid destination', destinationPathValidationError.value)
      return
    }

    unmanagedExistingDestination.value = false
    editingDestination.value = false
  } catch (err) {
    console.debug('Failed to normalize relative path on Done:', err)
    editingDestination.value = false
  }
}

async function confirmKnownMoveStorageSemantics(
  sourcePath: string,
  destinationPath: string,
): Promise<{ proceed: boolean; confirmedStorageSemantics: boolean }> {
  await rootStore.load()
  const candidates = [
    findMutationSemanticsRoot(rootStore.folders, destinationPath),
    findMutationSemanticsRoot(rootStore.folders, sourcePath),
  ].filter((root): root is NonNullable<typeof root> => root != null)
  const uniqueRoots = [...new Map(candidates.map((root) => [root.id, root])).values()]
  let confirmedStorageSemantics = false

  for (const root of uniqueRoots) {
    const outcome = await confirmDetectedMutationSemantics(root, 'the move')
    if (outcome === 'cancelled') {
      return { proceed: false, confirmedStorageSemantics }
    }
    if (outcome === 'retry') {
      confirmedStorageSemantics = true
    }
  }
  return { proceed: true, confirmedStorageSemantics }
}

async function moveAudiobookWithStorageConfirmation(
  audiobookId: number,
  destinationPath: string,
  options: { sourcePath?: string; moveFiles?: boolean; deleteEmptySource?: boolean },
) {
  let confirmedRetries = 0
  let identityRefreshAttempted = false
  while (true) {
    try {
      return await apiService.moveAudiobook(audiobookId, destinationPath, options)
    } catch (error: unknown) {
      if (options.moveFiles !== true) throw error

      const validationError = getApiValidationError(error)
      if (validationError?.code === 'move_source_unverified') {
        if (identityRefreshAttempted) throw error
        await refreshAudiobookFileIdentity(audiobookId)
        identityRefreshAttempted = true
        continue
      }
      if (confirmedRetries >= 2) throw error

      const affectedPath =
        validationError?.code === 'source_filesystem_mutation_unavailable'
          ? options.sourcePath
          : destinationPath
      const confirmation = await confirmMutationSemanticsForBlockedOperation(error, {
        path: affectedPath,
        operationLabel: 'the move',
      })
      if (confirmation === 'cancelled') return null
      if (confirmation !== 'retry') throw error

      await refreshAudiobookFileIdentity(audiobookId)
      identityRefreshAttempted = true
      confirmedRetries += 1
    }
  }
}

async function handleSave() {
  const audiobook = baselineAudiobook.value
  if (!audiobook || !hasChanges.value) return
  if (destinationPathValidationError.value) {
    toast.error('Invalid destination', destinationPathValidationError.value)
    return
  }

  // If the base path (destination) changed, prompt the user with rich options
  const combined = combinedBasePath()
  const originalBase = audiobook.basePath || ''
  const pathKind = selectedDestinationPathKind()
  const basePathChanged = destinationBasePathChanged()
  if (basePathChanged) {
    await refreshMoveRecoveryState(audiobook.id)
    const recovery = moveRecoveryState.value
    if (recovery?.hasUnresolvedMove) {
      toast.info(
        recovery.canRetry ? 'Resume interrupted move' : 'Move needs attention',
        recovery.canRetry
          ? 'Resume the interrupted move before changing the destination.'
          : 'Resolve the previous move before changing the destination.',
      )
      return
    }
  }

  const activeMoveJob = basePathChanged
    ? moveJobsStore.getActiveJobForAudiobook(audiobook.id)
    : undefined
  if (activeMoveJob) {
    toast.info(
      'Move already in progress',
      `Move job ${activeMoveJob.jobId} is still ${activeMoveJob.status.toLowerCase()}. Wait for it to finish before changing the destination again.`,
    )
    return
  }

  const destinationValidationMessage = basePathChanged
    ? validateLibraryDestinationPath(combined, {
        pathKind,
        caseSensitivity: selectedDestinationCaseSensitivity(),
        sourcePath: originalBase,
        allowFileSystemRoot: false,
      })
    : null
  if (destinationValidationMessage) {
    toast.error('Invalid destination', destinationValidationMessage)
    return
  }

  let userWantsMove = true
  let userWantsDeleteEmpty = true
  if (basePathChanged) {
    const choice = await askMoveConfirmation(originalBase || '', combined || '')
    if (!choice || !choice.proceed) return
    userWantsMove = Boolean(choice.moveFiles)
    userWantsDeleteEmpty = Boolean(choice.deleteEmptySource)
  }

  if (basePathChanged && userWantsMove) {
    const storageConfirmation = await confirmKnownMoveStorageSemantics(originalBase, combined || '')
    if (!storageConfirmation.proceed) return
    if (storageConfirmation.confirmedStorageSemantics) {
      try {
        await refreshAudiobookFileIdentity(audiobook.id)
      } catch (error: unknown) {
        toast.error(
          'Move preparation failed',
          error instanceof Error
            ? error.message
            : 'Listenarr could not refresh the audiobook file identity before moving.',
        )
        return
      }
    }
  }

  saving.value = true
  try {
    const identifiersChanged =
      serializeIdentifierRows(formData.value.identifiers) !==
      serializeIdentifierRows(originalIdentifierRows.value)

    const parsedRuntime = parseRuntimeInput(formData.value.runtime)
    const normalizedSeriesMemberships = normalizeSeriesMembershipRows(
      formData.value.seriesMemberships,
    )
    const primarySeries = derivePrimarySeriesMembership(normalizedSeriesMemberships)
    const normalizedTitle = normalizeOptionalText(formData.value.title)
    const normalizedSubtitle = normalizeOptionalText(formData.value.subtitle)
    const normalizedAuthors = normalizeStringList(formData.value.authors)
    const normalizedNarrators = normalizeStringList(formData.value.narrators)
    const normalizedDescription = normalizeOptionalText(formData.value.description)
    const normalizedPublisher = normalizeOptionalText(formData.value.publisher)
    const normalizedLanguage = normalizeLanguageText(formData.value.language)
    const normalizedPublishedDate = normalizeOptionalText(formData.value.publishedDate)
    const normalizedPublishYear = normalizeOptionalText(formData.value.publishYear)
    const normalizedEdition = normalizeOptionalText(formData.value.edition)
    const normalizedGenres = normalizeStringList(formData.value.genres)
    const normalizedImageUrl = normalizeOptionalText(formData.value.imageUrl)
    const normalizedTags = [...formData.value.tags].sort()
    const baselineTags = [...(audiobook.tags || [])].sort()
    const normalizedRuntimeInput = normalizeNumericInput(formData.value.runtime)
    const baselineRuntimeInput = normalizeNumericInput(audiobook.runtime?.toString())

    const updates: AudiobookUpdateRequest = {}
    if (formData.value.monitored !== Boolean(audiobook.monitored)) {
      updates.monitored = formData.value.monitored
    }

    if (formData.value.qualityProfileId !== (audiobook.qualityProfileId ?? null)) {
      updates.qualityProfileId =
        formData.value.qualityProfileId === null ? -1 : formData.value.qualityProfileId
    }

    if (normalizedTitle !== normalizeOptionalText(audiobook.title)) {
      updates.title = normalizedTitle
    }

    if (normalizedSubtitle !== normalizeOptionalText(audiobook.subtitle)) {
      updates.subtitle = normalizedSubtitle
    }

    if (serializeStringList(normalizedAuthors) !== serializeStringList(audiobook.authors)) {
      updates.authors = normalizedAuthors
    }

    if (serializeStringList(normalizedNarrators) !== serializeStringList(audiobook.narrators)) {
      updates.narrators = normalizedNarrators
    }

    if (normalizedDescription !== normalizeOptionalText(audiobook.description)) {
      updates.description = normalizedDescription
    }

    if (normalizedPublisher !== normalizeOptionalText(audiobook.publisher)) {
      updates.publisher = normalizedPublisher
    }

    if (normalizedLanguage !== normalizeLanguageText(audiobook.language)) {
      updates.language = normalizedLanguage
    }

    if (normalizedPublishedDate !== normalizeOptionalText(audiobook.publishedDate)) {
      updates.publishedDate = normalizedPublishedDate
    }

    if (normalizedPublishYear !== normalizeOptionalText(audiobook.publishYear)) {
      updates.publishYear = normalizedPublishYear
    }

    if (normalizedRuntimeInput !== '' && normalizedRuntimeInput !== baselineRuntimeInput) {
      if (parsedRuntime !== undefined) {
        updates.runtime = parsedRuntime
      }
    }

    if (normalizedEdition !== normalizeOptionalText(audiobook.edition)) {
      updates.edition = normalizedEdition
    }

    if (
      serializeSeriesMembershipRows(formData.value.seriesMemberships) !==
      serializeSeriesMembershipRows(
        audiobook.seriesMemberships,
        audiobook.series,
        audiobook.seriesNumber,
      )
    ) {
      updates.series = primarySeries.series
      updates.seriesNumber = primarySeries.seriesNumber
      updates.seriesMemberships = normalizedSeriesMemberships.map((membership, index) => ({
        id: membership.id,
        seriesName: membership.seriesName,
        seriesNumber: membership.seriesNumber || undefined,
        seriesAsin: membership.seriesAsin || undefined,
        isPrimary: Boolean(membership.isPrimary),
        sortOrder: index,
      }))
    }

    if (serializeStringList(normalizedGenres) !== serializeStringList(audiobook.genres)) {
      updates.genres = normalizedGenres
    }

    if (normalizedImageUrl !== normalizeOptionalText(audiobook.imageUrl)) {
      updates.imageUrl = normalizedImageUrl
    }

    if (JSON.stringify(normalizedTags) !== JSON.stringify(baselineTags)) {
      updates.tags = formData.value.tags
    }

    if (formData.value.abridged !== Boolean(audiobook.abridged)) {
      updates.abridged = formData.value.abridged
    }

    if (formData.value.explicit !== Boolean(audiobook.explicit)) {
      updates.explicit = formData.value.explicit
    }

    const hasNonIdentifierChanges = Object.keys(updates).length > 0

    // Persist metadata before enqueueing an asynchronous physical move. The move worker
    // reloads the current row for its narrow path rewrite, so this ordering prevents the
    // worker from racing the edits submitted from this same dialog.
    if (hasNonIdentifierChanges) {
      await apiService.updateAudiobook(audiobook.id, updates)
    }

    if (identifiersChanged) {
      await apiService.updateAudiobookIdentifiers(
        audiobook.id,
        formData.value.identifiers.map(toIdentifierWritePayload),
      )
      originalIdentifierRows.value = cloneIdentifierRows(formData.value.identifiers)
    }

    if (basePathChanged) {
      try {
        const res = await moveAudiobookWithStorageConfirmation(audiobook.id, combined ?? '', {
          sourcePath: originalBase || undefined,
          moveFiles: userWantsMove,
          deleteEmptySource: userWantsMove ? userWantsDeleteEmpty : false,
        })

        if (res == null) {
          return
        }

        if (userWantsMove) {
          const jobId = typeof res.jobId === 'string' ? res.jobId.trim() : ''
          const resolvedTarget = typeof res.target === 'string' ? res.target : ''
          if (!jobId || !resolvedTarget) {
            throw new Error(
              'The server did not return a durable move job ID and resolved destination.',
            )
          }

          toast.info('Move queued', `Move job queued (${jobId}). Moving files in background.`)

          moveJobsStore.trackQueuedJob({
            jobId,
            audiobookId: audiobook.id,
            status: 'Queued',
            target: resolvedTarget,
          })
        } else {
          toast.info('Destination updated', 'Destination changed without moving files.')
        }
      } catch (moveErr) {
        console.error('Failed to update destination:', moveErr)
        const validationError = getApiValidationError(moveErr, 'destinationPath')
        if (validationError) {
          serverDestinationValidationError.value = validationError.message
          editingDestination.value = true
          toast.error('Invalid destination', validationError.message)
          return
        }

        const relatedChangesSaved = hasNonIdentifierChanges || identifiersChanged
        const moveError = getApiValidationError(moveErr)
        if (
          moveError?.code === 'move_recovery_required' ||
          moveError?.code === 'move_repair_required' ||
          moveError?.code === 'move_recovery_ambiguous' ||
          moveError?.code === 'move_already_active' ||
          moveError?.code === 'move_active_options_conflict'
        ) {
          await refreshMoveRecoveryState(audiobook.id)
          toast.error(
            moveError.canRetry ? 'Resume interrupted move' : 'Move blocked',
            relatedChangesSaved
              ? `Your metadata changes were saved, but the move cannot start: ${moveError.message}`
              : moveError.message,
          )
          return
        }

        if (moveError) {
          toast.error(
            'Move failed',
            relatedChangesSaved
              ? `Your metadata changes were saved, but the move was blocked: ${moveError.message}`
              : moveError.message,
          )
          return
        }

        toast.error(
          'Move failed',
          relatedChangesSaved
            ? 'Your metadata changes were saved, but the destination update could not be confirmed.'
            : 'The destination update could not be confirmed. No move job was created.',
        )
        return
      }
    }

    emit('saved')
    close()
  } catch (error) {
    console.error('Failed to save audiobook edits:', error)
    toast.error('Save failed', 'Failed to save changes. Please try again.')
  } finally {
    saving.value = false
  }
}

async function loadIdentifiers() {
  const audiobook = baselineAudiobook.value
  if (!audiobook) return

  try {
    const response = await apiService.getAudiobookIdentifiers(audiobook.id)
    const rows = (response.identifiers || []).map(mapIdentifierToEditableRow)
    formData.value.identifiers = rows
    originalIdentifierRows.value = cloneIdentifierRows(rows)
  } catch (error) {
    logger.debug('Failed to load audiobook identifiers', error)
    formData.value.identifiers = []
    originalIdentifierRows.value = []
  }
}

function createIdentifierRow(type: AudiobookExternalIdentifierType): EditableIdentifierRow {
  return {
    localKey: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    type,
    value: '',
    region: type === 'Asin' ? 'us' : null,
    isPrimary: false,
    source: 'Manual',
  }
}

function mapIdentifierToEditableRow(
  identifier: AudiobookExternalIdentifier,
): EditableIdentifierRow {
  return {
    localKey: `id-${identifier.id}`,
    type: identifier.type,
    value: identifier.value || identifier.valueNormalized,
    region: identifier.region ?? null,
    isPrimary: Boolean(identifier.isPrimary),
    source: identifier.source || 'Manual',
  }
}

function cloneIdentifierRows(rows: EditableIdentifierRow[]): EditableIdentifierRow[] {
  return rows.map((row) => ({ ...row }))
}

function serializeIdentifierRows(rows: EditableIdentifierRow[]): string {
  const normalized = rows
    .map((row) => ({
      type: row.type,
      value: (row.value || '').trim(),
      region: row.type === 'Asin' ? (row.region || '').trim().toLowerCase() : '',
      isPrimary: Boolean(row.isPrimary),
      source: row.source || 'Manual',
    }))
    .sort((a, b) => {
      const ka = `${a.type}|${a.value.toLowerCase()}|${a.region}|${a.isPrimary ? '1' : '0'}|${a.source}`
      const kb = `${b.type}|${b.value.toLowerCase()}|${b.region}|${b.isPrimary ? '1' : '0'}|${b.source}`
      return ka.localeCompare(kb)
    })
  return JSON.stringify(normalized)
}

function addIdentifier(type: AudiobookExternalIdentifierType) {
  formData.value.identifiers.push(createIdentifierRow(type))
}

function removeIdentifier(index: number) {
  formData.value.identifiers.splice(index, 1)
}

function onIdentifierTypeChanged(index: number) {
  const row = formData.value.identifiers[index]
  if (!row) return
  if (row.type !== 'Asin') {
    row.region = null
  } else if (!row.region) {
    row.region = 'us'
  }
  if (row.isPrimary) {
    setPrimaryIdentifier(index)
  }
}

function setPrimaryIdentifier(index: number) {
  const row = formData.value.identifiers[index]
  if (!row) return
  const type = row.type
  formData.value.identifiers.forEach((r, i) => {
    if (r.type === type) {
      r.isPrimary = i === index
    }
  })
}

function toIdentifierWritePayload(row: EditableIdentifierRow): AudiobookExternalIdentifierInput {
  return {
    type: row.type,
    value: row.value,
    region: row.type === 'Asin' ? row.region || null : null,
    isPrimary: Boolean(row.isPrimary),
    source: row.source || 'Manual',
  }
}

function addTag() {
  const tag = newTag.value.trim()
  if (tag && !formData.value.tags.includes(tag)) {
    formData.value.tags.push(tag)
    newTag.value = ''
  }
}

function removeTag(index: number) {
  formData.value.tags.splice(index, 1)
}

function pushUniqueValues(target: string[], rawValue: string) {
  for (const value of splitStringList(rawValue)) {
    if (!target.some((existing) => existing.toLowerCase() === value.toLowerCase())) {
      target.push(value)
    }
  }
}

function addAuthor() {
  pushUniqueValues(formData.value.authors, newAuthor.value)
  newAuthor.value = ''
}

function removeAuthor(index: number) {
  formData.value.authors.splice(index, 1)
}

function addNarrator() {
  pushUniqueValues(formData.value.narrators, newNarrator.value)
  newNarrator.value = ''
}

function removeNarrator(index: number) {
  formData.value.narrators.splice(index, 1)
}

function addGenre() {
  pushUniqueValues(formData.value.genres, newGenre.value)
  newGenre.value = ''
}

function removeGenre(index: number) {
  formData.value.genres.splice(index, 1)
}

function addSeriesMembership() {
  const nextMembership = createEditableSeriesMembership()
  if (formData.value.seriesMemberships.length === 0) {
    nextMembership.isPrimary = true
  }
  formData.value.seriesMemberships.push(nextMembership)
}

function removeSeriesMembership(index: number) {
  const [removed] = formData.value.seriesMemberships.splice(index, 1)
  if (removed?.isPrimary && formData.value.seriesMemberships.length > 0) {
    formData.value.seriesMemberships[0].isPrimary = true
  }

  formData.value.seriesMemberships = formData.value.seriesMemberships.map(
    (membership, membershipIndex) => ({
      ...membership,
      sortOrder: membershipIndex,
      isPrimary:
        membership.isPrimary ||
        (membershipIndex === 0 &&
          !formData.value.seriesMemberships.some((entry) => entry.isPrimary)),
    }),
  )
}

function setPrimarySeriesMembership(index: number) {
  formData.value.seriesMemberships = formData.value.seriesMemberships.map(
    (membership, membershipIndex) => ({
      ...membership,
      isPrimary: membershipIndex === index,
      sortOrder: membershipIndex,
    }),
  )
}

function close() {
  emit('close')
}
</script>

<style scoped>
/* Modal layout is provided by shared `modals.css` - keep component-specific scrollbars and spacing tweaks */

.path-length-warning,
.path-validation-error {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 6px;
  padding: 6px 10px;
  border-radius: 6px;
  font-size: 0.82rem;
}

.path-length-warning {
  background: rgba(255, 152, 0, 0.12);
  border: 1px solid rgba(255, 152, 0, 0.3);
  color: #ffb74d;
}

.path-validation-error {
  background: rgba(244, 67, 54, 0.12);
  border: 1px solid rgba(244, 67, 54, 0.3);
  color: #ef9a9a;
}

/* Use global modal body padding variants instead of redefining .modal-body here */

.modal-body::-webkit-scrollbar {
  width: 8px;
}

.modal-body::-webkit-scrollbar-track {
  background: #1e1e1e;
}

.modal-body::-webkit-scrollbar-thumb {
  background: #555;
  border-radius: 6px;
}

.modal-body::-webkit-scrollbar-thumb:hover {
  background: #666;
}

.edit-form {
  display: flex;
  flex-direction: column;
}

.metadata-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.75rem;
}

.metadata-field {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.metadata-field--wide {
  grid-column: span 2;
}

.metadata-field--full {
  grid-column: 1 / -1;
}

.metadata-textarea {
  min-height: 7rem;
  resize: vertical;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.field-label {
  font-size: 0.85rem;
  font-weight: 600;
  color: #d4d4d4;
}

.form-group {
  display: flex;
  flex-direction: column;
}

.identifier-list {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.identifier-row {
  display: grid;
  grid-template-columns: 9.5rem minmax(0, 1fr) 5.5rem auto auto auto;
  gap: 0.5rem;
  align-items: center;
}

.identifier-type {
  min-width: 0;
}

.identifier-value {
  min-width: 0;
}

.identifier-region {
  min-width: 0;
}

.identifier-region--spacer {
  height: 0;
}

.identifier-primary {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.85rem;
  color: #d4d4d4;
  white-space: nowrap;
}

.identifier-source {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 4.5rem;
  padding: 0.2rem 0.45rem;
  border-radius: 999px;
  border: 1px solid #3b3b3b;
  background: #222;
  color: #bfbfbf;
  font-size: 0.75rem;
  text-transform: uppercase;
}

.identifier-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.identifier-actions .btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.identifiers-empty {
  color: #9b9b9b;
  font-size: 0.9rem;
  padding: 0.25rem 0;
}

.btn-remove-identifier {
  justify-self: end;
}

.series-memberships-editor {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.series-membership-row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  align-items: flex-start;
  padding: 0.85rem;
  background: #1e1e1e;
  border: 1px solid #3a3a3a;
  border-radius: 8px;
}

.series-membership-fields {
  display: grid;
  grid-template-columns: minmax(0, 1.75fr) minmax(7rem, 0.8fr);
  gap: 0.75rem;
  flex: 1;
  min-width: min(100%, 22rem);
}

.series-membership-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.series-membership-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-left: auto;
}

.series-primary-toggle {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  color: #d4d4d4;
  font-size: 0.85rem;
  white-space: nowrap;
}

.series-memberships-empty {
  color: #9b9b9b;
  font-size: 0.9rem;
  padding: 0.25rem 0;
}

.btn-add-series-membership {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  align-self: flex-start;
}

@media (max-width: 900px) {
  .metadata-grid {
    grid-template-columns: 1fr;
  }

  .metadata-field--wide,
  .metadata-field--full {
    grid-column: auto;
  }

  .identifier-row {
    grid-template-columns: 1fr;
    gap: 0.4rem;
    padding: 0.5rem;
    border: 1px solid #333;
    border-radius: 8px;
    background: #202020;
  }

  .identifier-region--spacer {
    display: none;
  }

  .btn-remove-identifier {
    justify-self: start;
  }

  .series-membership-fields {
    grid-template-columns: 1fr;
  }

  .series-membership-actions {
    width: 100%;
    justify-content: space-between;
    margin-left: 0;
  }
}

.info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: rgba(52, 152, 219, 0.09);
  border: 1px solid rgba(52, 152, 219, 0.28);
  border-radius: 6px;
  color: #3498db;
}

.info-section svg {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  margin-top: 0.125rem;
  fill: currentColor;
}

.info-section p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
}

.info-section strong {
  color: #5dade2;
}

/* Confirm modal visuals rely on shared modal styles; keep confirm-specific interior layout below */

.confirm-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0;
}

.confirm-header i {
  font-size: 1.5rem;
  color: var(--brand-focus);
}

.confirm-header h3 {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 500;
  color: #ffffff;
}

.confirm-body {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.confirm-description p {
  margin: 0;
  color: #cccccc;
  line-height: 1.5;
}

.path-comparison {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  background: #252526;
  border-radius: 8px;
  padding: 1rem;
  border: 1px solid #333;
}

.path-section {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.path-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  color: #ffffff;
  font-size: 0.9rem;
}

.path-label svg {
  color: var(--brand-focus);
  width: 16px;
  height: 16px;
}

.path-display {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 0.75rem;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 0.85rem;
  color: #cccccc;
  word-break: break-all;
  line-height: 1.4;
}

.path-display code {
  background: transparent;
  color: inherit;
  padding: 0;
  border: none;
  font-family: inherit;
}

.confirm-options {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.checkbox-row {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.75rem;
  background: #252526;
  border-radius: 8px;
  border: 1px solid #333;
  transition: all 0.2s ease;
}

.checkbox-row:hover {
  background: #2d2d30;
  border-color: var(--brand-focus);
}

.checkbox-row label {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  cursor: pointer;
  width: 100%;
  margin: 0;
}

.checkbox-row input[type='checkbox'] {
  margin-top: 0.125rem;
  width: 1rem;
  height: 1rem;
  accent-color: var(--brand-focus);
  cursor: pointer;
}

.checkbox-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  flex: 1;
}

.checkbox-label {
  color: #aaaaaa;
  font-size: 0.8rem;
  line-height: 1.3;
  text-align: left;
}
.checkbox-label small {
  color: #999;
}

.confirm-actions {
  display: flex;
  gap: 0.75rem;
  padding: 1rem 1.5rem 1.5rem;
  border-top: 1px solid #333;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.confirm-actions .btn {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1.25rem;
  border-radius: 6px;
  font-weight: 400; /* normal weight */
  font-size: 0.9rem;
  transition: all 0.2s ease;
  border: 1px solid transparent;
  cursor: pointer;
  min-width: fit-content;
}

.confirm-actions .btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Use centralized button color variants from `src/assets/modals.css` for confirm actions */

.confirm-actions .btn-primary {
  background: var(--brand-focus);
  color: #ffffff;
}

.confirm-actions .btn-primary:hover:not(:disabled) {
  background: var(--brand-700);
}

/* Mobile responsive adjustments */
@media (max-width: 640px) {
  .confirm-overlay.separate-modal {
    padding: 0.5rem;
  }

  .confirm-dialog {
    max-width: 100%;
    margin: 0;
  }

  .confirm-header {
    padding: 1rem 1rem 0.75rem;
  }

  .confirm-header h3 {
    font-size: 1.1rem;
  }

  .confirm-body {
    padding: 1rem;
    gap: 1rem;
  }

  .path-comparison {
    padding: 0.75rem;
  }

  .path-display {
    padding: 0.5rem;
    font-size: 0.8rem;
  }

  .checkbox-row {
    padding: 0.5rem;
  }

  .confirm-actions {
    padding: 0.75rem 1rem 1rem;
    gap: 0.5rem;
  }

  .confirm-actions .btn {
    padding: 0.625rem 1rem;
    font-size: 0.85rem;
    flex: 1;
    justify-content: center;
  }
}

/* Radio visuals are provided by shared RadioCard / global modal rules. Removed component-local radio styles to avoid duplication. */

.form-select {
  padding: 0.75rem 1rem;
  background-color: #1a1a1a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
  width: 100%;
}

.form-select:hover {
  border-color: #555;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

/* Use centralized .help-text spacing from src/assets/modals.css */

/* modal-footer centralized; keep this modal's layout preferences only */
.modal-footer {
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  margin-top: 1rem;
  flex-wrap: wrap;
}

.modal-footer > .btn {
  flex-shrink: 0;
}

/* Base .btn moved to src/assets/modals.css; keep shrink behavior and any modal-specific overrides */
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Button color variants are centralized in `src/assets/modals.css` */

.move-status {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 0.75rem 1rem;
  background-color: rgba(52, 152, 219, 0.1);
  border: 1px solid rgba(52, 152, 219, 0.3);
  border-radius: 6px;
  color: #3498db;
  font-size: 0.85rem;
  flex: 1;
  min-width: 200px;
}

.move-status small {
  color: #87ceeb;
  line-height: 1.3;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

.tags-container {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.tags-list {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  padding: 0.75rem;
  background-color: #1e1e1e;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  min-height: 3rem;
  align-items: flex-start;
  align-content: flex-start;
}

.tag-item {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.35rem 0.6rem;
  background-color: #2a2a2a;
  color: #e0e0e0;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
  border: 1px solid #3a3a3a;
  transition: all 0.2s ease;
}

.tag-item:hover {
  background-color: #333;
  border-color: var(--brand-focus);
  color: white;
}

.tag-item:hover::before {
  opacity: 1;
}

.tags-empty {
  color: #888;
  font-size: 0.875rem;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  padding: 0.5rem;
}

.tag-remove {
  background: rgba(0, 0, 0, 0.2);
  border: none;
  color: #ccc;
  cursor: pointer;
  padding: 0.25rem;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  transition: all 0.2s ease;
  flex-shrink: 0;
  margin-left: 0.25rem;
}

.tag-remove:hover {
  background: var(--danger-600, #e74c3c);
  color: #fff;
}

.tag-remove:active {
  background: rgba(255, 255, 255, 0.25);
}

.tag-input-group {
  display: flex;
  gap: 0.5rem;
}

.tag-input {
  flex: 1;
  padding: 0.75rem 1rem;
  background-color: #1a1a1a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  transition: all 0.2s ease;
}

.tag-input:hover {
  border-color: #555;
}

.tag-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  background-color: #2d2d2d;
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.tag-input::placeholder {
  color: #666;
}

.btn-add-tag {
  /* icon-only variant: use compact square size */
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0.5rem;
  background-color: var(--brand-focus);
  color: white;
  border: none;
  border-radius: var(--btn-radius);
  font-size: 0.95rem;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.2s ease;
  width: var(--control-height);
  height: var(--control-height);
}

.btn-add-tag:hover:not(:disabled) {
  background-color: #005fa3;
  transform: translateY(-1px);
}

.btn-add-tag:active:not(:disabled) {
  transform: translateY(0);
}

.btn-add-tag:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.checkbox-group {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

/* Visual card wrapper for checkbox rows is provided globally via .modal-content .checkbox-group .input-checkbox
   Keep checkbox-inner label minimal here to avoid double-card appearance */
.checkbox-label {
  color: #ccc;
  font-size: 0.95rem;
  text-align: left;
}

.checkbox-wrapper {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
}

.checkbox-label input[type='checkbox'] {
  width: 20px;
  height: 20px;
  cursor: pointer;
  accent-color: var(--brand-focus);
  margin-top: 0.125rem;
  flex-shrink: 0;
}

.checkbox-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  flex: 1;
}

.checkbox-title {
  color: #ccc;
  font-weight: 500;
  font-size: 0.95rem;
  transition: color 0.2s;
}

.checkbox-label:has(input[type='checkbox']:checked) .checkbox-title {
  color: white;
}

.checkbox-content small {
  color: #999;
  font-size: 0.85rem;
  line-height: 1.4;
}

/* @keyframes spin is centralized in src/assets/main.css */

/* Destination display styles (shared with AddLibraryModal) */
.destination-display {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.5rem 0;
}

.move-recovery-notice {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem;
  border: 1px solid rgba(255, 152, 0, 0.35);
  border-radius: 6px;
  background: rgba(255, 152, 0, 0.1);
}

.move-recovery-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
}

.move-recovery-content code {
  overflow-wrap: anywhere;
}

/* Read-only destination display */
.destination-readonly {
  display: flex;
  gap: 0.75rem;
  align-items: stretch;
  padding: 0.5rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
}

.readonly-input {
  flex: 1;
  color: #ccc !important;
  cursor: default;
  padding: 0.6rem 0.75rem;
}

.btn-edit-destination {
  /* More compact so the icon reads clearly in compact contexts (folder browser, inline rows) */
  padding: 0.35rem; /* reduce horizontal/vertical padding */
  min-width: 40px;
  min-height: 40px;
  border-radius: 6px;
  /* Default *fallback* styling for non-primary use (kept for backwards compatibility) */
  background-color: #333;
  border: 1px solid #555;
  color: #ccc;
  cursor: pointer;
  transition: all 0.2s;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  font-size: 0.9rem;
  font-weight: 500;
  white-space: nowrap;
}

/* When combined with .btn-primary, use the shared primary visuals instead of the fallback */
.btn-primary.btn-edit-destination {
  background-color: var(--brand-500);
  color: #fff;
  border: none;
}
.btn-primary.btn-edit-destination:hover:not(:disabled) {
  background-color: var(--brand-700);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.22);
}
.btn-primary.btn-edit-destination:active:not(:disabled) {
  background-color: var(--brand-800, #0056b3);
  transform: translateY(0);
}

.btn-edit-destination svg {
  width: 20px !important;
  height: 20px !important;
}

.btn-edit-destination:hover {
  border-color: var(--brand-focus);
  background-color: var(--brand-focus);
  color: #fff;
  transform: translateY(-1px);
  box-shadow: 0 2px 6px rgba(var(--brand-rgb), 0.3);
}

.btn-edit-destination:active {
  background-color: #0056b3;
  transform: translateY(0);
}

/* Edit mode destination */
.destination-edit {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  /* Increased gap for better separation */
  padding: 1rem;
  /* Increased padding */
  background-color: #1e1e1e;
  border: 1px solid #333;
  border-radius: 8px;
}

.destination-actions {
  display: flex;
  gap: 0.75rem;
  justify-content: flex-end;
}

.form-input {
  width: 100%;
  padding: 0.6rem 0.75rem;
  border-radius: 6px;
  border: 1px solid #3a3a3a;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
}

.form-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: var(--focus-ring);
  /* More visible focus ring */
}

.form-input::placeholder {
  color: #888;
  /* Subtle placeholder color */
}

/* Row layout for destination: browse + root + input + actions */
.destination-row {
  display: flex;
  gap: 0.5rem;
  /* Consistent gap */
  align-items: center;
  /* vertically center controls */
  flex-wrap: wrap;
}

.root-select .form-select {
  height: 42px;
  /* Slightly taller for better touch targets */
  box-sizing: border-box;
  background-color: #1a1a1a;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 0.75rem 1rem;
  min-width: 140px;
  /* Keep select usable while allowing input to grow */
  flex: 0 0 auto;
}

.root-select .form-select:focus {
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.2);
}

.root-select .form-label {
  display: none;
  /* Hide redundant label in modal context */
}

.relative-input {
  flex: 1;
  min-width: 180px;
  height: 42px;
  /* Match select height */
  box-sizing: border-box;
  padding: 0.75rem 1rem;
  /* Match select padding */
}

.destination-actions {
  flex-shrink: 0;
}

/* Responsive design */
@media (max-width: 768px) {
  /* Modal layout/responsive rules are centralized in `modals.css`.
     Keep only component-specific responsive adjustments here. */

  .destination-row {
    flex-direction: column;
    align-items: stretch;
  }

  .root-select,
  .relative-input {
    flex: 1;
    min-width: auto;
  }

  .destination-actions {
    justify-content: stretch;
    /* Full width buttons on mobile */
    gap: 0.75rem;
  }

  .destination-actions .btn {
    flex: 1;
    /* Equal width buttons */
  }

  .move-status {
    order: -1;
    width: 100%;
  }
}
</style>
