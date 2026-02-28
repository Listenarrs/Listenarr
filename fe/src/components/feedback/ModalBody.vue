<template>
  <div data-modal-body="true" :class="['modal-body', { 'no-padding': noPadding, 'modal-body--compact': compact, 'modal-body--tight': tight }]" :style="maxHeightStyle">
    <slot />
  </div>
</template>

<script setup lang="ts">
import type { CSSProperties } from 'vue'

const props = defineProps({
  noPadding: { type: Boolean, default: false },
  compact: { type: Boolean, default: false },
  tight: { type: Boolean, default: false },
  maxHeight: { type: String, required: false },
})

const maxHeightStyle = props.maxHeight ? ({ maxHeight: props.maxHeight, overflowY: 'auto' } as CSSProperties) : undefined
</script>

<style scoped>
.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

.no-padding {
  padding: 0;
}
</style>