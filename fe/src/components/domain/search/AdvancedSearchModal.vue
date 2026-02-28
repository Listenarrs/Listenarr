<template>
  <Modal :visible="show" size="md" @close="closeModal">
    <template #header>
      <ModalHeader :title="'Advanced Search'" :icon="PhFunnelSimple" @close="closeModal" />
    </template>

    <template #default>
      <ModalBody>
        <p class="help-text">
          Enter multiple search criteria to find audiobooks. When both Title and Author are
          provided, searches using Audimeta's combined search for more accurate results.
        </p>

        <div class="form-group">
          <label for="title">Title</label>
          <input
            id="title"
            v-model="searchParams.title"
            type="text"
            placeholder="e.g., Dune"
            class="form-input"
            @keyup.enter="performSearch"
          />
        </div>

        <div class="form-group">
          <label for="author">Author</label>
          <input
            id="author"
            v-model="searchParams.author"
            type="text"
            placeholder="e.g., Frank Herbert"
            class="form-input"
            @keyup.enter="performSearch"
          />
        </div>

        <div class="form-group">
          <label for="series">Series</label>
          <input
            id="series"
            v-model="searchParams.series"
            type="text"
            placeholder="e.g., The Empyrean"
            class="form-input"
            @keyup.enter="performSearch"
          />
        </div>

        <div class="form-group">
          <label for="isbn">ISBN</label>
          <input
            id="isbn"
            v-model="searchParams.isbn"
            type="text"
            placeholder="e.g., 9780441172719"
            class="form-input"
            @keyup.enter="performSearch"
          />
        </div>

        <div class="form-group">
          <label for="asin">ASIN</label>
          <input
            id="asin"
            v-model="searchParams.asin"
            type="text"
            placeholder="e.g., B08G9PRS1K"
            class="form-input"
            @keyup.enter="performSearch"
          />
        </div>

        <div class="form-group">
          <label for="language">Region</label>
          <select id="language" v-model="searchParams.language" class="form-input">
            <option value="">Any Region</option>
            <option value="english">United States (US)</option>
            <option value="german">Germany (DE)</option>
            <option value="french">France (FR)</option>
            <option value="spanish">Spain (ES)</option>
            <option value="italian">Italy (IT)</option>
            <option value="portuguese">Brazil (BR)</option>
            <option value="japanese">Japan (JP)</option>
            <option value="chinese">China (CN)</option>
          </select>
        </div>

        <div v-if="error" class="error-message">
          <PhWarningCircle />
          {{ error }}
        </div>
      </div>

    </template>

    <template #footer>
      <button @click="clearForm" class="btn btn-secondary"><PhTrash /> Clear</button>
      <button
        @click="performSearch"
        :disabled="!isValidSearch || isSearching"
        class="btn btn-primary"
      >
        <PhSpinner v-if="isSearching" class="ph-spin" />
        <PhMagnifyingGlass v-else />
        {{ isSearching ? 'Searching...' : 'Search' }}
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import {
  PhFunnelSimple,
  PhX,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhTrash,
} from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import type { SearchResult } from '@/types'

defineProps<{
  show: boolean
}>()

const emit = defineEmits<{
  close: []
  results: [results: SearchResult[]]
}>()

const searchParams = ref({
  title: '',
  author: '',
  series: '',
  isbn: '',
  asin: '',
  language: '',
})

const isSearching = ref(false)
const error = ref('')

const isValidSearch = computed(() => {
  return !!(
    searchParams.value.title ||
    searchParams.value.author ||
    searchParams.value.isbn ||
    searchParams.value.asin ||
    searchParams.value.series
  )
})

const closeModal = () => {
  if (!isSearching.value) {
    emit('close')
  }
}

const clearForm = () => {
  searchParams.value = {
    title: '',
    author: '',
    series: '',
    isbn: '',
    asin: '',
    language: '',
  }
  error.value = ''
}

const performSearch = async () => {
  if (!isValidSearch.value) {
    error.value = 'Please enter at least one search parameter'
    return
  }

  error.value = ''
  isSearching.value = true

  try {
    const params: Record<string, string> = {}
    if (searchParams.value.title) params.title = searchParams.value.title
    if (searchParams.value.author) params.author = searchParams.value.author
    if (searchParams.value.series) params.series = searchParams.value.series
    if (searchParams.value.isbn) params.isbn = searchParams.value.isbn
    if (searchParams.value.asin) params.asin = searchParams.value.asin
    if (searchParams.value.language) params.language = searchParams.value.language

    const results = await apiService.advancedSearch(params)
    emit('results', results)
    emit('close')
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Search failed'
  } finally {
    isSearching.value = false
  }
}
</script>

<style scoped>


.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.modal-header h2 {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: white;
  font-size: 1.5rem;
}

.close-button {
  background: none;
  border: none;
  color: #999;
  cursor: pointer;
  padding: 0.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  transition: all 0.2s;
}

.close-button:hover {
  background-color: rgba(255, 255, 255, 0.1);
  color: white;
}

.modal-body {
  padding: 1.5rem;
}

.help-text {
  color: #999;
  font-size: 0.875rem;
  margin-bottom: 1.5rem;
  line-height: 1.5;
}

.form-group {
  margin-bottom: 1.25rem;
}

.form-group label {
  display: block;
  color: white;
  font-weight: 500;
  margin-bottom: 0.5rem;
  font-size: 0.875rem;
}

.form-input {
  width: 100%;
  padding: 0.75rem;
  border: 2px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background-color: rgba(0, 0, 0, 0.2);
  color: white;
  font-size: 1rem;
  font-family: inherit;
  transition: all 0.2s ease;
}

.form-input:focus {
  outline: none;
  border-color: #4dabf7;
  background-color: rgba(0, 0, 0, 0.3);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.form-input::placeholder {
  color: #6c757d;
}

select.form-input {
  cursor: pointer;
}

select.form-input option {
  background-color: #1a1a1a;
  color: white;
}

.error-message {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fa5252;
  font-size: 0.875rem;
  padding: 0.75rem;
  background-color: rgba(250, 82, 82, 0.1);
  border-radius: 6px;
  margin-top: 1rem;
}

/* modal-footer styles are centralized in src/assets/modals.css; no local overrides required */
.modal-footer { display:flex; gap:0.75rem; justify-content:flex-end }

/* Buttons are centralized in `src/assets/buttons.css` and modal color variants are in `src/assets/modals.css`. Use `.btn` / `.btn-primary` here to avoid duplication. */

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

/* Mobile Responsive */
@media (max-width: 640px) {
  .modal-content {
    max-width: 100%;
    max-height: 100vh;
    border-radius: 6px;
  }

  .modal-header h2 {
    font-size: 1.25rem;
  }

  .modal-body,
  .modal-header,
  .modal-footer {
    padding: 1rem;
  }

  /* Button sizing in modals is centralized in src/assets/modals.css; no local adjustment required */
}
</style>
