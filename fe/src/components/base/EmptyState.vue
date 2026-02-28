<template>
  <div class="empty-state">
    <div class="empty-icon">
      <slot name="icon" />
    </div>
    <h3 class="empty-title">{{ title }}</h3>
    <p v-if="message" class="empty-message">{{ message }}</p>
    <div v-if="$slots.action" class="empty-actions">
      <slot name="action" />
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * EmptyState - Display a placeholder when no data is available
 *
 * Shows an icon, title, message, and optional action button to guide users
 * when a list or view has no content to display.
 *
 * @example
 * <EmptyState title="No audiobooks found" message="Add your first audiobook to get started">
 *   <template #icon><PhBooks :size="48" /></template>
 *   <template #action>
 *     <button @click="addAudiobook">Add Audiobook</button>
 *   </template>
 * </EmptyState>
 */
defineProps({
  /** Main heading text for the empty state */
  title: { type: String, required: true },
  /** Optional descriptive message */
  message: String,
})
</script>

<style scoped>
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 3rem 1rem;
  text-align: center;
  color: #999;
  min-height: 300px;
}

.empty-icon {
  margin-bottom: 1.5rem;
  color: #666;
  font-size: 3rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.empty-icon :deep(svg) {
  width: 48px;
  height: 48px;
  color: inherit;
}

.empty-title {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.25rem;
  font-weight: 500;
}

.empty-message {
  margin: 0 0 2rem 0;
  color: #999;
  font-size: 0.95rem;
  max-width: 400px;
  line-height: 1.5;
}

.empty-actions {
  display: flex;
  gap: 1rem;
  justify-content: center;
  flex-wrap: wrap;
}
</style>
