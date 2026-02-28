<template>
  <div class="library-import-view">
    <div class="page-header">
      <h1>Library Import</h1>
    </div>

    <div class="import-options">
      <div class="import-card" @click="importFromFolder">
        <div class="import-icon">📁</div>
        <h3>Import from Folder</h3>
        <p>Scan a local folder for audio files and import them into your library</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
const importFromFolder = () => {
  // TODO: Implement folder import functionality
}
</script>

<style scoped>
.library-import-view {
  padding: 2em;
}

.page-header h1 {
  margin: 0 0 2rem 0;
  color: white;
  font-size: 2rem;
}

.import-options {
  display: flex;
  justify-content: center;
}

.import-card {
  background-color: #2a2a2a;
  border-radius: 6px;
  padding: 2rem;
  text-align: center;
  cursor: pointer;
  transition: all 0.2s;
  border: 2px solid transparent;
}

.import-card:hover {
  background-color: #333;
  border-color: var(--brand-500);
  transform: translateY(-4px);
}

.import-icon {
  font-size: 3rem;
  margin-bottom: 1rem;
}

.import-card h3 {
  color: white;
  margin-bottom: 1rem;
}

.import-card p {
  color: #ccc;
  margin: 0;
}
</style>
