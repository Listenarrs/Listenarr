<template>
  <div class="modal-title-wrap">
    <div class="modal-title">
      <h3 :id="headingId">
        <span v-if="$slots.icon || icon" class="modal-icon" :aria-hidden="!iconLabel">
          <slot name="icon" v-if="$slots.icon" />
          <component v-else-if="icon" :is="icon" />
        </span>
        <slot>{{ title }}</slot>
        <span v-if="iconLabel" class="visually-hidden">{{ iconLabel }}</span>
      </h3>
      <small v-if="$slots.subtitle" class="modal-subtitle"><slot name="subtitle" /></small>
    </div>

    <div class="modal-header-actions">
      <slot name="actions">
        <button v-if="showClose" class="close-btn" @click="$emit('close')" :aria-label="closeAriaLabel">
          <PhX />
        </button>
      </slot>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, type PropType, type Component } from 'vue'
import { PhX } from '@phosphor-icons/vue'

const props = defineProps({
  title: { type: String, required: false },
  showClose: { type: Boolean, default: true },
  closeAriaLabel: { type: String, default: 'Close dialog' },
  icon: { type: [Object, Function] as PropType<Component>, required: false },
  iconLabel: { type: String, required: false },
  headingId: { type: String, required: false },
})

const emit = defineEmits(['close'])

// generate an id if none provided - stable per component instance
const internalId = `modal-heading-${Math.random().toString(36).slice(2, 9)}`
const headingId = props.headingId || internalId
</script>

<style scoped>
.modal-title-wrap {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%; /* ensure the actions area sits at the far right of the header */
}

.modal-title {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex: 1; /* take remaining space so actions are pushed right */
}

.modal-title h3 {
  margin: 0;
  color: #fff;
  font-size: 1.125rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  line-height: 1;
}

/* Slightly reduce vertical padding so combined header (global + local) feels tighter */
:deep(.modal-header) {
  padding-top: 1rem;
  padding-bottom: 1rem;
}
.modal-subtitle {
  display: block;
  color: #999;
  font-size: 0.85rem;
}

.modal-header-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.modal-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  color: inherit;
}

.visually-hidden {
  position: absolute !important;
  height: 1px;
  width: 1px;
  overflow: hidden;
  clip: rect(1px, 1px, 1px, 1px);
  white-space: nowrap;
  border: 0;
  padding: 0;
  margin: -1px;
}

.close-btn {
  background: none;
  border: none;
  color: #999;
  cursor: pointer;
  padding: 0.5rem;
  font-size: 1.2rem;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  transition: all 0.2s;
}

.close-btn:hover {
  background-color: #333;
  color: #fff;
}
</style>