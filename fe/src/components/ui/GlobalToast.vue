<template>
  <div id="global-toast" aria-live="polite" class="global-toast" :style="{ top: toastTop }">
    <transition-group name="toast" tag="div">
      <div
        v-for="t in toasts"
        :key="t.id"
        :class="['toast-item', `toast-${t.level}`]"
        role="status"
        aria-atomic="true"
      >
        <div class="toast-content">
          <div class="toast-title">{{ t.title }}</div>
          <div class="toast-message">{{ t.message }}</div>
        </div>
        <button class="toast-close" @click="dismiss(t.id)" aria-label="Dismiss">×</button>
      </div>
    </transition-group>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { useToast } from '@/services/toastService'

const toast = useToast()

// `toast.toasts` is reactive; use it directly in template
const toasts = computed(() => toast.toasts)

// Compute a dynamic top offset so the toast sits below the fixed top nav.
const toastTop = ref('72px')
function computeTopOffset() {
  try {
    const topNav = document.querySelector('.top-nav') as HTMLElement | null
    toastTop.value = topNav ? `${topNav.offsetHeight + 12}px` : '72px'
  } catch {
    toastTop.value = '72px'
  }
}

onMounted(() => {
  computeTopOffset()
  window.addEventListener('resize', computeTopOffset)
})

onUnmounted(() => {
  window.removeEventListener('resize', computeTopOffset)
})

function dismiss(id: string) {
  try {
    toast.dismiss(id)
  } catch {}
}
</script>
