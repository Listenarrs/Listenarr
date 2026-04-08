<template>
  <div :key="result.asin ?? result.title"
    class="result-item"
  >
    <img
      v-if="result.imageUrl"
      :src="getProtectedImageSrc(result.imageUrl, `library-import-search-${result.asin ?? result.title}`, placeholderUrl)"
      class="result-thumb"
      alt=""
    />
    <div class="result-info">
      <span class="result-title">{{ result.title }}</span>
      <span class="result-meta">
        {{ result.authors?.[0]?.name }}
        <span v-if="result.series"> · {{ Array.isArray(result.series) ? (result.series as any)[0]?.name : result.series }}</span>
        <span v-if="result.asin" class="result-asin"> · {{ result.asin }}</span>
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useProtectedImages } from '@/composables/useProtectedImages';
import type { SearchResult } from '@/types';

const { getProtectedImageSrc } = useProtectedImages()

defineProps<{
  result: SearchResult;
  placeholderUrl?: string;
}>();
</script>

<style scoped>
.result-item {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.5rem 0.75rem;
  cursor: pointer;
  transition: background 0.15s;
  border-bottom: 1px solid #2a2a2a;
}

.result-item:last-child {
  border-bottom: none;
}

.result-item:hover {
  background: #2a2a2a;
}

.result-thumb {
  width: 36px;
  height: 36px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

.result-info {
  min-width: 0;
  flex: 1;
}

.result-title {
  display: block;
  font-size: 0.875rem;
  color: #e0e0e0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-meta {
  display: block;
  font-size: 0.75rem;
  color: #888;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.result-asin {
  font-family: monospace;
  font-size: 0.7rem;
}
</style>