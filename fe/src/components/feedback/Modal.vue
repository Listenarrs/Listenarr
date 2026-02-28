<template>
  <teleport to="body">
    <div v-if="visible" class="modal-overlay" @click.self="onClose">
      <div
        ref="contentRef"
        class="modal-content"
        v-bind="$attrs"
        :class="sizeClass"
        @click.stop
        role="dialog"
        aria-modal="true"
        :aria-labelledby="ariaLabelledBy"
      >
        <div class="modal-header">
          <slot name="header">
            <h3 v-if="title">{{ title }}</h3>
            <button v-if="showClose" @click="onClose" class="close-btn" aria-label="Close modal">
              <slot name="close-icon">✕</slot>
            </button>
          </slot>
        </div>

        <template v-if="!hasCustomBody">
          <div class="modal-body">
            <slot />
          </div>
        </template>
        <template v-else>
          <slot />
        </template>

        <template v-if="!hasCustomFooter">
          <div class="modal-footer">
            <slot name="footer" />
          </div>
        </template>
        <template v-else>
          <slot name="footer" />
        </template>
      </div>
    </div>
  </teleport>
</template>

<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch, nextTick, useSlots } from 'vue'

defineOptions({ inheritAttrs: false })

const props = defineProps({
  visible: { type: Boolean, required: true },
  title: { type: String, default: '' },
  showClose: { type: Boolean, default: true },
  size: { type: String as () => 'sm' | 'md' | 'lg', default: 'md' },
})
const emit = defineEmits(['close'])

function onClose() {
  emit('close')
}

const sizeClass = computed(() => {
  return props.size === 'sm' ? 'modal-sm' : props.size === 'lg' ? 'modal-lg' : 'modal-md'
})

const contentRef = ref<HTMLElement | null>(null)
const ariaLabelledBy = ref<string | undefined>(undefined)

// Synchronously inspect slot VNodes to decide whether to render default wrappers.
const slots = useSlots()

function vnodeHasDataAttr(nodes: any[] | undefined, attr: string): boolean {
  if (!nodes) return false
  for (const n of nodes) {
    if (!n) continue

    // If this VNode is a component that we know provides body/footer, treat as true.
    const typeName = n.type && (n.type.name || n.type.__name || (n.type as any).name)
    if (typeName === 'ModalBody' || typeName === 'ModalForm') return attr === 'data-modal-body'
    if (typeName === 'ModalFooter') return attr === 'data-modal-footer'

    const props = n.props || {}
    if (props && props[attr]) return true

    // deep check children (text/array slots etc.)
    if (Array.isArray(n.children)) {
      if (vnodeHasDataAttr(n.children as any[], attr)) return true
    }

    // some slots wrap VNode in .children or component.subTree
    if (n.component && n.component.subTree) {
      const sub = (n.component.subTree as any).children
      if (Array.isArray(sub) && vnodeHasDataAttr(sub, attr)) return true
    }
  }
  return false
}

const hasCustomBody = computed(() => vnodeHasDataAttr(slots.default ? slots.default() : undefined, 'data-modal-body'))
const hasCustomFooter = computed(() => vnodeHasDataAttr(slots.footer ? slots.footer() : undefined, 'data-modal-footer'))

function detectCustomRegions() {
  const el = contentRef.value
  if (!el) return
  // DOM fallback in case slots change after initial render
  // but computed above handles initial detect synchronously
  // nothing else needed here; keep for MutationObserver to update aria/other logic
}

function ensureHeaderLabel() {
  const el = contentRef.value
  if (!el) return
  const heading = el.querySelector('h1, h2, h3') as HTMLElement | null
  if (heading) {
    if (!heading.id) {
      heading.id = `modal-title-${Math.random().toString(36).slice(2, 9)}`
    }
    ariaLabelledBy.value = heading.id
  }
}

function onKeyDown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.visible) {
    onClose()
  }
}

onMounted(() => {
  nextTick().then(() => {
    ensureHeaderLabel()
    detectCustomRegions()
    // observe mutations to update detection (e.g., slot content changes)
    if (contentRef.value) {
      const mo = new MutationObserver(() => detectCustomRegions())
      mo.observe(contentRef.value, { childList: true, subtree: true })
      ;(contentRef.value as any).__modalObserver = mo
    }
  })
  document.addEventListener('keydown', onKeyDown)
})
onUnmounted(() => {
  document.removeEventListener('keydown', onKeyDown)
  if (contentRef.value && (contentRef.value as any).__modalObserver) {
    ;(contentRef.value as any).__modalObserver.disconnect()
  }
})

watch(() => props.visible, (v) => {
  if (v) nextTick().then(() => {
    ensureHeaderLabel()
    detectCustomRegions()
  })
})
</script>

<style scoped>
/* Component-level minimal layout adjustments rely on the global modals stylesheet
   Use the `size` prop to choose one of the standardized sizes: `sm` (420px), `md` (700px), `lg` (1000px).
*/
.modal-content {
  max-width: 700px; /* default; overridden by global classes */
}
.modal-sm { max-width: 420px }
.modal-md { max-width: 700px }
.modal-lg { max-width: 1000px }
</style>