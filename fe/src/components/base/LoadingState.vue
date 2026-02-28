<template>
  <div class="loading-state">
    <div class="spinner">
      <PhSpinner :size="size" />
    </div>
    <p v-if="message" class="loading-message">{{ message }}</p>
  </div>
</template>

<script setup lang="ts">
import { PhSpinner } from '@phosphor-icons/vue'

/**
 * LoadingState - Display a loading indicator with optional message
 *
 * Shows a spinning icon and customizable message while data is being fetched
 * or processed. Provides consistent loading UX across the application.
 *
 * @example
 * <LoadingState />
 *
 * @example
 * <LoadingState message="Loading audiobooks..." />
 */
defineProps({
  /** Loading message to display (default: 'Loading...') */
  message: { type: String, default: 'Loading...' },
  /** Size of the spinner icon in pixels */
  size: { type: Number, default: 32 },
})
</script>

<style scoped>
.loading-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  gap: 1rem;
  min-height: 200px;
}

.spinner {
  display: flex;
  align-items: center;
  justify-content: center;
}

.spinner :deep(svg) {
  animation: spin 1s linear infinite;
  color: var(--brand-500, #2196f3);
}

.loading-message {
  margin: 0;
  color: #ccc;
  font-size: 0.95rem;
}

/* @keyframes spin is centralized in src/assets/animations.css */
</style>
