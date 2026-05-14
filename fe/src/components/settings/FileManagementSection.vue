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
  <div class="form-section">
    <h3><PhFolder /> File Management</h3>
    <div class="form-body">
      <FormRow label="Folder Naming Pattern" help="Pattern for organizing audiobook folders">
        <div class="input-group">
          <input
            v-model="folderPattern"
            type="text"
            placeholder="{Author}/{Series}/{Title}"
            @change="
              (e) => updateField('folderNamingPattern', (e.target as HTMLInputElement).value)
            "
          />
          <button class="help-button" @click="openModal('folder')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="folderPattern">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(folderPattern) }}</code>
        </div>
      </FormRow>

      <FormRow
        label="Single File Naming Pattern"
        help="Pattern for naming audiobooks as single files"
      >
        <div class="input-group">
          <input
            v-model="filePatternSingleFile"
            type="text"
            placeholder="{Title}"
            @change="(e) => updateField('fileNamingPattern', (e.target as HTMLInputElement).value)"
          />
          <button class="help-button" @click="openModal('file')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="filePatternSingleFile">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(filePatternSingleFile, 'file') }}.ext</code>
        </div>
      </FormRow>

      <FormRow
        label="Multi-File Naming Pattern"
        help="Pattern for naming audiobooks split across multiple files"
      >
        <div class="input-group">
          <input
            v-model="filePatternMultiFile"
            type="text"
            placeholder="{Title}-{DiskNumber:00}"
            @change="
              (e) => updateField('multiFileNamingPattern', (e.target as HTMLInputElement).value)
            "
          />
          <button class="help-button" @click="openModal('file')" title="View pattern help">
            <PhQuestion :size="18" />
          </button>
        </div>
        <div class="pattern-preview" v-if="filePatternMultiFile">
          <span class="preview-label">Preview:</span>
          <code>{{ applyPattern(filePatternMultiFile, 'file', true) }}</code>
        </div>
      </FormRow>

      <!-- Path length warning -->
      <div v-if="pathLengthWarning" class="path-length-warning">
        <PhWarning :size="18" />
        <div class="warning-content">
          <strong>Path may exceed Windows limit (260 characters)</strong>
          <p>{{ pathLengthWarning }}</p>
        </div>
      </div>
      <div v-else-if="estimatedPathLength > 0" class="path-length-ok">
        <span>Estimated sample path length: {{ estimatedPathLength }} / 259 characters</span>
      </div>

      <FormRow
        label="Completed File Action"
        help="Choose whether completed downloads should be moved into the library output path or copied and left in the client's folder."
      >
        <select
          :value="settings.completedFileAction"
          @change="(e) => updateField('completedFileAction', (e.target as HTMLSelectElement).value)"
        >
          <option value="Move">Move</option>
          <option value="Hardlink/Copy">Hardlink/Copy</option>
        </select>
      </FormRow>

      <FormRow
        label="Import Blacklist Extensions"
        help="Extensions to skip during library import and completed-download import. Separate values with commas or new lines, for example: .nfo, .sfv, .jpg"
      >
        <textarea
          class="blacklist-textarea"
          :value="formatExtensionList(settings.importBlacklistExtensions)"
          placeholder=".nfo&#10;.sfv&#10;.jpg"
          rows="4"
          @change="
            (e) =>
              updateField(
                'importBlacklistExtensions',
                parseExtensionList((e.target as HTMLTextAreaElement).value),
              )
          "
        />
      </FormRow>
    </div>

    <!-- Pattern Help Modal -->
    <div v-if="showModal" class="modal-overlay" @click.self="closeModal">
      <div class="modal-content">
        <div class="modal-header">
          <h2>{{ modalTitle }}</h2>
          <button class="close-btn" @click="closeModal">
            <PhX :size="24" />
          </button>
        </div>
        <div class="modal-body">
          <div v-if="activePatternType === 'folder'" class="pattern-help">
            <p><strong>Available Variables:</strong></p>
            <ul>
              <li><code>{Author}</code> - Author name</li>
              <li><code>{Narrator}</code> - Narrator name(s)</li>
              <li><code>{Series}</code> - Series name</li>
              <li><code>{Title}</code> - Book title</li>
              <li><code>{Subtitle}</code> - Book subtitle</li>
              <li><code>{Edition}</code> - User-defined edition label</li>
              <li><code>{Publisher}</code> - Publisher name</li>
              <li><code>{Language}</code> - Metadata language</li>
              <li><code>{Asin}</code> - Audible ASIN</li>
              <li>
                <code>{SeriesNumber}</code> or <code>{SeriesNumber:00}</code> - Position in series
                (00 = zero-padded; decimal positions like 4.5 are not padded)
              </li>
              <li><code>{Year}</code> - Publication year</li>
            </ul>
            <p class="example"><strong>Example:</strong> <code>{Author}/{Series}/{Title}</code></p>
            <p class="result">
              <strong>Result:</strong> <code>Stephen King/The Dark Tower/The Gunslinger</code>
            </p>
          </div>
          <div v-else-if="activePatternType === 'file'" class="pattern-help">
            <p><strong>Available Variables:</strong></p>
            <ul>
              <li><code>{Author}</code> - Author name</li>
              <li><code>{Narrator}</code> - Narrator name(s)</li>
              <li><code>{Series}</code> - Series name</li>
              <li><code>{Title}</code> - Book title</li>
              <li><code>{Subtitle}</code> - Book subtitle</li>
              <li><code>{Edition}</code> - User-defined edition label</li>
              <li><code>{Publisher}</code> - Publisher name</li>
              <li><code>{Language}</code> - Metadata language</li>
              <li><code>{Asin}</code> - Audible ASIN</li>
              <li>
                <code>{SeriesNumber}</code> or <code>{SeriesNumber:00}</code> - Position in series
                (00 = zero-padded; decimal positions like 4.5 are not padded)
              </li>
              <li>
                <code>{DiskNumber}</code> or <code>{DiskNumber:00}</code> - Disk/part number (00 =
                zero-padded)
              </li>
              <li>
                <code>{ChapterNumber}</code> or <code>{ChapterNumber:00}</code> - Chapter number (00
                = zero-padded)
              </li>
              <li><code>{Year}</code> - Publication year</li>
              <li><code>{Quality}</code> - Audio quality (bitrate or format)</li>
            </ul>
            <p class="example">
              <strong>Example:</strong> <code>{Title} - {Edition} ({Language}) [{Asin}]</code>
            </p>
            <p class="result">
              <strong>Result:</strong>
              <code>The Gunslinger - Revised Edition (English) [B000FC1R84].m4b</code>
            </p>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { ref, computed } from 'vue'
import { PhFolder, PhQuestion, PhX, PhWarning } from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

const showModal = ref(false)
const activePatternType = ref<'folder' | 'file'>('folder')
const folderPattern = ref(props.settings.folderNamingPattern || '')
const filePatternSingleFile = ref(props.settings.fileNamingPattern || '')
const filePatternMultiFile = ref(props.settings.multiFileNamingPattern || '{Title}-{DiskNumber:00}')

// Sample values for testing patterns
const sampleVariables = {
  Author: 'Stephen King',
  Narrator: 'George Guidall',
  Series: 'The Dark Tower',
  Title: 'The Gunslinger',
  Subtitle: 'The Dark Tower Begins',
  Edition: 'Revised Edition',
  Publisher: 'Penguin Audio',
  Language: 'English',
  Asin: 'B000FC1R84',
  SeriesNumber: '1',
  Year: '1982',
  DiskNumber: '3',
  ChapterNumber: '3',
  Quality: '128kbps',
}

const modalTitle = computed(() =>
  activePatternType.value === 'folder' ? 'Folder Naming Pattern Help' : 'File Naming Pattern Help',
)

function applyPattern(
  pattern: string,
  type: 'folder' | 'file' = 'folder',
  multiFile: boolean = false,
): string {
  if (!pattern) return ''

  let result = pattern

  // Replace all variables with sample values
  for (const [key, value] of Object.entries(sampleVariables)) {
    if (key === 'DiskNumber' || key === 'ChapterNumber' || key === 'SeriesNumber') {
      // Handle zero-padding for disk, chapter, and series numbers using the sample value
      const paddedRegex = new RegExp(`\\{${key}:00\\}`, 'g')
      const paddedSample = value.toString().padStart(2, '0')
      result = result.replace(paddedRegex, paddedSample)
      result = result.replace(new RegExp(`\\{${key}\\}`, 'g'), value)
    } else {
      const regex = new RegExp(`\\{${key}\\}`, 'g')
      result = result.replace(regex, value)
    }
  }

  // For multi-file preview, show how files would be named
  if (type === 'file' && multiFile) {
    // Check if pattern includes DiskNumber or ChapterNumber for uniqueness
    const hasDiskNumber = pattern.includes('{DiskNumber')
    const hasChapterNumber = pattern.includes('{ChapterNumber')

    if (!hasDiskNumber && !hasChapterNumber) {
      // Pattern doesn't differentiate files - show warning
      return `⚠️ ${result}.ext (all files would have the same name!)`
    }

    // Generate unique filenames by simulating different disk/chapter numbers
    let file1 = result
    let file2 = result
    const file3 = result

    // Replace DiskNumber variants
    if (hasDiskNumber) {
      const diskSample = sampleVariables.DiskNumber.toString().padStart(2, '0')
      const diskRegex = new RegExp(diskSample, 'g')
      file1 = file1.replace(diskRegex, '01')
      file2 = file2.replace(diskRegex, '02')
      // file3 already contains the sample value for the third file — no-op replacement removed
    }

    // Replace ChapterNumber variants
    if (hasChapterNumber) {
      const chapterSample = sampleVariables.ChapterNumber.toString().padStart(2, '0')
      const chapterRegex = new RegExp(chapterSample, 'g')
      file1 = file1.replace(chapterRegex, '01')
      file2 = file2.replace(chapterRegex, '02')
      // file3 already contains the sample value for the third file — no-op replacement removed
    }

    return `${file1}.ext, ${file2}.ext, ${file3}.ext...`
  }

  return result
}

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function parseExtensionList(value: string): string[] {
  return value
    .split(/[\r\n,;]+/)
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => (entry.startsWith('.') ? entry : `.${entry}`))
}

function formatExtensionList(value: string[] | undefined): string {
  return Array.isArray(value) ? value.join('\n') : ''
}

// --- Path length estimation ---
const WINDOWS_MAX_PATH = 259

/** Build a sample full path from the current patterns and output path to estimate length. */
const estimatedPathLength = computed(() => {
  const outputPath = (props.settings.outputPath || 'D:\\Audiobooks').replace(/[\/\\]+$/, '')
  const folder = folderPattern.value ? applyPattern(folderPattern.value) : ''
  const file = filePatternSingleFile.value
    ? applyPattern(filePatternSingleFile.value, 'file') + '.m4b'
    : 'Unknown Title.m4b'

  const parts = [outputPath, folder, file].filter(Boolean)
  const fullPath = parts.join('\\')
  return fullPath.length
})

const pathLengthWarning = computed<string | null>(() => {
  const len = estimatedPathLength.value
  if (len <= WINDOWS_MAX_PATH) return null

  const excess = len - WINDOWS_MAX_PATH
  return (
    `With the sample values, the generated path is ${len} characters — ${excess} over the Windows MAX_PATH limit. ` +
    'Long author names, series, or titles will be automatically truncated by the server, but shorter patterns are recommended to avoid surprises.'
  )
})

function openModal(type: 'folder' | 'file') {
  activePatternType.value = type
  showModal.value = true
}

function closeModal() {
  showModal.value = false
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0.5rem 0;
  font-size: 1.2rem;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 0.625rem;
  color: #fff;
  letter-spacing: 0.01em;
}

h3 svg {
  color: var(--brand-500);
  filter: drop-shadow(0 0 8px rgba(33, 150, 243, 0.3));
}

.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
}

.blacklist-textarea {
  width: 100%;
  min-height: 7rem;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  font-family: inherit;
  resize: vertical;
}

.blacklist-textarea:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.08);
}

.form-group {
  margin-bottom: 1.25rem;
}
.form-group:last-child {
  margin-bottom: 0;
}

.form-group label {
  margin-bottom: 0.5rem;
  font-weight: 500;
  color: #fff;
}

.input-group {
  display: flex;
  gap: 0.5rem;
  align-items: center;
}

.input-group input[type='text'] {
  flex: 1;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.12s;
}

.input-group input[type='text']::placeholder {
  color: #6c757d;
}

.input-group input[type='text']:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.help-button {
  padding: 0.6rem 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #adb5bd;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
  flex-shrink: 0;
}

.help-button:hover {
  border-color: #666;
  color: #e0e0e0;
  background-color: rgba(255, 255, 255, 0.05);
}

.help-button:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.form-group select {
  width: 100%;
  padding: 0.9rem 0.85rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.12s;
}

.form-group select:focus {
  outline: none;
  border-color: var(--brand-500);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

/* Modal Styles */
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
  border-radius: 8px;
  max-width: 600px;
  width: 100%;
  max-height: 85vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.5);
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
  font-size: 1.3rem;
}

.close-btn {
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  padding: 0.25rem;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
}

.close-btn:hover {
  color: #fff;
}

.modal-body {
  padding: 1.5rem;
  overflow-y: auto;
  flex: 1;
}

.pattern-help ul {
  list-style: none;
  padding: 0;
  margin: 0.75rem 0;
}

.pattern-help li {
  padding: 0.5rem 0;
  color: #ddd;
  line-height: 1.6;
}

.pattern-help li code {
  background-color: #1a1a1a;
  padding: 0.2rem 0.4rem;
  border-radius: 3px;
  color: #4dd0e1;
  font-size: 0.9em;
}

.pattern-help p {
  margin: 1rem 0 0.5rem 0;
  color: #ddd;
  line-height: 1.6;
}

.pattern-help p code {
  background-color: #1a1a1a;
  padding: 0.2rem 0.4rem;
  border-radius: 3px;
  color: #4dd0e1;
}

.pattern-help .example {
  margin-top: 1.5rem;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border-left: 3px solid var(--brand-500);
  border-radius: 4px;
}

.pattern-help .result {
  margin: 0.75rem 0 0 0;
  padding: 0;
  background-color: transparent;
  border-left: none;
  color: #90caf9;
}

.pattern-preview {
  margin-top: 0.75rem;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border-left: 3px solid var(--brand-500);
  border-radius: 4px;
  font-size: 0.9rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.pattern-preview .preview-label {
  color: #adb5bd;
  font-weight: 500;
  white-space: nowrap;
}

.pattern-preview code {
  color: #4dd0e1;
  font-family: 'Courier New', monospace;
  flex: 1;
  word-break: break-all;
}

/* Path length feedback */
.path-length-warning {
  display: flex;
  align-items: flex-start;
  gap: 0.625rem;
  margin-top: 1rem;
  padding: 0.875rem 1rem;
  background-color: rgba(255, 152, 0, 0.08);
  border: 1px solid rgba(255, 152, 0, 0.35);
  border-radius: 6px;
  color: #ffb74d;
  font-size: 0.875rem;
  line-height: 1.5;
}

.path-length-warning svg {
  flex-shrink: 0;
  margin-top: 0.125rem;
}

.path-length-warning .warning-content {
  flex: 1;
}

.path-length-warning .warning-content strong {
  display: block;
  margin-bottom: 0.25rem;
  color: #ffa726;
}

.path-length-warning .warning-content p {
  margin: 0;
  color: #ffcc80;
}

.path-length-ok {
  margin-top: 0.75rem;
  font-size: 0.8rem;
  color: #6c757d;
}
</style>
