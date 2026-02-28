<template>
  <div class="form-group checkbox-group">
    <Checkbox :modelValue="modelValue" @update:modelValue="$emit('update:modelValue', $event)">
      <span>
        <slot />
        <span class="checkbox-text">
          <strong>{{ title }}</strong>
          <small v-if="description">{{ description }}</small>
        </span>
      </span>
    </Checkbox>
  </div>
</template>

<script setup lang="ts">
import Checkbox from '@/components/form/Checkbox.vue'

const props = defineProps<{ modelValue?: boolean; title: string; description?: string }>()
const emit = defineEmits(['update:modelValue'])
</script>

<style scoped>
/* Reuse .checkbox-group styles from global.css; no extra styles needed here */
/* Layout override so icon slot and text appear on one row within this card */
.input-checkbox { align-items: center; }
.input-checkbox .checkbox-label { align-items: center; }
.input-checkbox .checkbox-label > span { display: flex; flex-direction: row; align-items: center; gap: 0.6rem; }
.input-checkbox .checkbox-label .checkbox-text { display: flex; flex-direction: row; align-items: center; gap: 0.5rem; }
.input-checkbox .checkbox-label .checkbox-text small { margin-left: 0.25rem; }
</style>
