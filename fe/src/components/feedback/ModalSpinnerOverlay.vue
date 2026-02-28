<template>
  <div v-if="active" class="modal-spinner-overlay" role="status" aria-live="polite">
    <div class="spinner-wrap">
      <PhSpinner class="ph-spin big" />
      <div v-if="message" class="spinner-message">{{ message }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { PhSpinner } from '@phosphor-icons/vue'
const props = defineProps({ active: { type: Boolean, default: false }, message: { type: String, required: false } })
</script>

<style scoped>
.modal-spinner-overlay {
  position: absolute;
  inset: 0;
  background: rgba(0,0,0,0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 30;
}
.spinner-wrap { display:flex; flex-direction:column; align-items:center; gap:0.75rem; }
.ph-spin.big { width: 48px; height: 48px; }
.spinner-message { color:#fff; }
</style>