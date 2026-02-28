<template>
  <div class="form-section">
    <h3>
      <component :is="icon" v-if="icon" />
      {{ title }}
    </h3>
    <div class="section-card">
      <slot />
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  title: string
  icon?: any // PhIcon component
}>()
</script>

<style scoped>
.form-section {
  margin-bottom: 2rem;
}

/* Card-like container for section content (follows the h3) */
.section-card {
  background-color: #232323;
  border: 1px solid #333;
  border-radius: 8px;
  padding: 1rem;
  margin-top: 0.75rem;
  box-shadow: 0 6px 18px rgba(0,0,0,0.25);
}

/* Reduce spacing for first/last children inside the card to keep alignment */
.section-card > *:first-child { margin-top: 0; }
.section-card > *:last-child { margin-bottom: 0; }
.form-section:last-of-type {
  margin-bottom: 0;
}

.form-section h3 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
</style>