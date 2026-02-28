<template>
  <div :class="['config-card', { disabled: disabled, active: active }]">
    <div class="config-info">
      <h4 class="config-title">{{ title }}</h4>
      <p v-if="description" class="config-description">{{ description }}</p>
      <div v-if="$slots.meta" class="config-meta">
        <slot name="meta" />
      </div>
    </div>
    <div v-if="$slots.actions" class="config-actions">
      <slot name="actions" />
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps({
  title: { type: String, required: true },
  description: String,
  disabled: { type: Boolean, default: false },
  active: { type: Boolean, default: false },
})
</script>

<style scoped>
.config-card {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 1rem;
  background: var(--bg-tertiary, #2a2a2a);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: var(--radius-md, 6px);
  transition: all var(--transition-normal, 0.2s ease);
  gap: 1rem;
}

.config-card:hover:not(.disabled) {
  border-color: rgba(var(--brand-rgb, 33, 150, 243), 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(var(--brand-rgb, 33, 150, 243), 0.12);
}

.config-card.disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.config-card.active {
  border-color: var(--brand-500, #2196f3);
  background: rgba(var(--brand-rgb, 33, 150, 243), 0.05);
}

.config-info {
  flex: 1;
  min-width: 0;
}

.config-title {
  margin: 0 0 0.5rem 0;
  font-size: 1rem;
  font-weight: 500;
  color: var(--text-primary, #ffffff);
}

.config-description {
  margin: 0 0 0.75rem 0;
  color: var(--text-muted, #999999);
  font-size: 0.9rem;
  line-height: 1.4;
}

.config-meta {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  font-size: 0.85rem;
  color: var(--text-secondary, #cccccc);
}

.config-actions {
  display: flex;
  gap: 0.5rem;
  flex-shrink: 0;
  align-items: center;
}

@media (max-width: 768px) {
  .config-card {
    flex-direction: column;
  }

  .config-actions {
    width: 100%;
    justify-content: flex-end;
  }
}
</style>
