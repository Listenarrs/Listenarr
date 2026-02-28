<template>
  <div class="tab-content">
    <div class="form-section">
      <RootFoldersSettings ref="rootFoldersRef" :hide-header="false" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import RootFoldersSettings from '@/components/settings/RootFoldersSettings.vue'

// Ref to access child component methods
const rootFoldersRef = ref<InstanceType<typeof RootFoldersSettings> | null>(null)

// Expose method for parent to trigger add folder
function openAddRootFolder() {
  rootFoldersRef.value?.openAdd()
}

defineExpose({
  openAddRootFolder,
})
</script>

<style scoped>
.tab-content {
  animation: fadeIn 0.2s ease;
}

/* @keyframes fadeIn is centralized in src/assets/animations.css */

.form-section {
  background-color: transparent;
  border: none;
  padding: 0;
}
</style>
