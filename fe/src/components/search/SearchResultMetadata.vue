<template>
  <div v-if="hasAnyMetadata" class="metadata-badges">
    <span v-if="publisher" class="metadata-badge">
      <PhBuilding />
      {{ safeText(publisher) }}
    </span>
    <span v-if="publishedDate" class="metadata-badge">
      <PhCalendar />
      {{ formatDate(publishedDate) }}
    </span>
    <span v-else-if="publishYear" class="metadata-badge">
      <PhCalendar />
      {{ publishYear }}
    </span>
    <span v-if="asin" class="metadata-badge">
      <PhBarcode />
      {{ asin }}
    </span>
    <span v-if="isbn" class="metadata-badge">
      <PhBarcode />
      {{ isbn }}
    </span>
    <span v-if="openLibraryId && !asin" class="metadata-badge">
      <PhBarcode />
      {{ openLibraryId }}
    </span>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhBuilding, PhCalendar, PhBarcode } from '@phosphor-icons/vue'
import { formatDate } from '@/utils/searchResultFormatting'
import { safeText } from '@/utils/textUtils'

interface Props {
  publisher?: string
  publishedDate?: string
  publishYear?: number | string
  asin?: string
  isbn?: string
  openLibraryId?: string
}

const props = withDefaults(defineProps<Props>(), {
  publisher: undefined,
  publishedDate: undefined,
  publishYear: undefined,
  asin: undefined,
  isbn: undefined,
  openLibraryId: undefined,
})

const hasAnyMetadata = computed(() => {
  // Check if any metadata is present
  return (
    props.publisher || props.publishedDate || props.publishYear || props.asin || props.isbn || props.openLibraryId
  )
})
</script>

<style scoped>
.metadata-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.metadata-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.375rem;
  padding: 0.375rem 0.75rem;
  background: var(--color-surface-subtle, #f5f5f5);
  border-radius: 0.25rem;
  font-size: 0.875rem;
  color: var(--color-text-secondary, #666);
}

.metadata-badge :deep(svg) {
  width: 0.875rem;
  height: 0.875rem;
  flex-shrink: 0;
}
</style>
