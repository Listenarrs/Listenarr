<template>
  <div class="form-group radio-group">
    <label :class="['radio-label', { active: isActive }]" @click="select">
      <input
        type="radio"
        :name="name"
        :checked="isActive"
        @change="onChange"
        :aria-checked="isActive"
      />
      <div class="radio-content">
        <span class="radio-title">{{ title }}</span>
        <small v-if="description">{{ description }}</small>
        <slot />
      </div>
    </label>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  modelValue?: any
  value: any
  title: string
  description?: string
  name?: string
}>()
const emit = defineEmits(['update:modelValue'])

const isActive = computed(() => props.modelValue === props.value)

function onChange() {
  emit('update:modelValue', props.value)
}

function select() {
  // Clicking the label should select the radio
  emit('update:modelValue', props.value)
}
</script>

<style scoped>
/* Visuals moved to global modal stylesheet to ensure consistency across modals. Keep minimal layout helpers here. */
.radio-group { display: flex; flex-direction: column; }
.radio-content { display:flex; flex-direction:column; gap:0.25rem; flex:1 }
</style>
