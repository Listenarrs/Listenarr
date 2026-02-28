<template>
  <div class="form-group">
    <label v-if="label" :for="labelFor" class="form-row-label">{{ label }}</label>
    <div class="form-row-control">
      <slot />
    </div>
    <span v-if="help" class="form-help">{{ help }}</span>
  </div>
</template>

<script setup lang="ts">
defineProps<{ label?: string; help?: string; labelFor?: string }>()
</script>

<style scoped>
.form-row-label { display:block; margin-bottom:0.5rem; font-weight:500; color:#fff }
.form-row-control { display:block }
.form-help { display:block; margin-top:0.5rem; font-size:0.85rem; color:#adb5bd; line-height:1.5 }
</style>
