<template>
  <div class="modal-actions">
    <div class="modal-actions-left"><slot name="left" /></div>
    <div class="modal-actions-right"><slot name="right" /></div>
  </div>
</template>

<script setup lang="ts">
// simple layout wrapper
</script>

<style scoped>
.modal-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}
.modal-actions-left { display:flex; gap:0.75rem; }
.modal-actions-right { display:flex; gap:0.75rem; }
@media (max-width:480px) {
  .modal-actions { flex-direction: column-reverse; }
}
</style>