<template>
  <div class="score-popover">
    <slot>
      <span class="score">{{ score }}</span>
    </slot>
  </div>
</template>

<script setup lang="ts">
const props = withDefaults(defineProps<{ score?: number }>(), { score: 0 })
</script>

<style scoped>
.score-popover { display:inline-flex; align-items:center }
.score { background: rgba(255,255,255,0.04); padding: 0.15rem 0.5rem; border-radius:4px }
</style>
