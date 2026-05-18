<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <teleport to="body">
    <div
      v-if="visible"
      class="modal-overlay"
      :style="overlayStyle"
      @click.self="onClose"
    >
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
import type { VNode } from 'vue'

defineOptions({ name: 'BaseModal', inheritAttrs: false })

const props = defineProps({
  visible: { type: Boolean, required: true },
  title: { type: String, default: '' },
  showClose: { type: Boolean, default: true },
  size: { type: String as () => 'sm' | 'md' | 'lg', default: 'md' },
  // Optional inline-style override for the overlay's z-index. Useful when this
  // Modal is opened from inside another modal whose overlay has bumped its
  // z-index above the global default of 3000; pass a value higher than the
  // parent's overlay so the nested Modal stacks on top instead of rendering
  // behind a darker (compounded) parent overlay. Omit to keep the default.
  overlayZIndex: { type: Number, default: undefined },
})
const emit = defineEmits(['close'])

function onClose() {
  emit('close')
}

const sizeClass = computed(() => {
  return props.size === 'sm' ? 'modal-sm' : props.size === 'lg' ? 'modal-lg' : 'modal-md'
})

const overlayStyle = computed(() =>
  props.overlayZIndex != null ? { zIndex: props.overlayZIndex } : undefined,
)

const contentRef = ref<HTMLElement | null>(null)
const ariaLabelledBy = ref<string | undefined>(undefined)
let modalObserver: MutationObserver | null = null

// Synchronously inspect slot VNodes to decide whether to render default wrappers.
const slots = useSlots()

function asVNodes(value: unknown): VNode[] {
  if (!Array.isArray(value)) return []
  return value.filter(
    (entry): entry is VNode => typeof entry === 'object' && entry !== null && 'type' in entry,
  )
}

function getTypeName(node: VNode): string | undefined {
  const type = node.type
  if (typeof type === 'object' && type !== null) {
    const componentType = type as { name?: string; __name?: string }
    return componentType.name || componentType.__name
  }
  return undefined
}

function vnodeHasDataAttr(nodes: readonly VNode[] | undefined, attr: string): boolean {
  if (!nodes) return false
  for (const node of nodes) {
    if (!node) continue

    // If this VNode is a component that we know provides body/footer, treat as true.
    const typeName = getTypeName(node)
    if (typeName === 'ModalBody' || typeName === 'ModalForm') return attr === 'data-modal-body'
    if (typeName === 'ModalFooter') return attr === 'data-modal-footer'

    const vnodeProps = (node.props as Record<string, unknown> | null) ?? null
    if (vnodeProps && vnodeProps[attr] !== undefined) return true

    // deep check children (text/array slots etc.)
    const childNodes = asVNodes(node.children)
    if (childNodes.length > 0 && vnodeHasDataAttr(childNodes, attr)) {
      return true
    }

    // some slots wrap VNode in .children or component.subTree
    const subTreeChildren = asVNodes(node.component?.subTree?.children)
    if (subTreeChildren.length > 0 && vnodeHasDataAttr(subTreeChildren, attr)) {
      return true
    }
  }
  return false
}

const hasCustomBody = computed(() =>
  vnodeHasDataAttr(slots.default ? slots.default() : undefined, 'data-modal-body'),
)
const hasCustomFooter = computed(() =>
  vnodeHasDataAttr(slots.footer ? slots.footer() : undefined, 'data-modal-footer'),
)

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
      modalObserver = new MutationObserver(() => detectCustomRegions())
      modalObserver.observe(contentRef.value, { childList: true, subtree: true })
    }
  })
  document.addEventListener('keydown', onKeyDown)
})
onUnmounted(() => {
  document.removeEventListener('keydown', onKeyDown)
  if (modalObserver) {
    modalObserver.disconnect()
    modalObserver = null
  }
})

watch(
  () => props.visible,
  (v) => {
    if (v)
      nextTick().then(() => {
        ensureHeaderLabel()
        detectCustomRegions()
      })
  },
)
</script>

<style scoped>
/* Component-level minimal layout adjustments rely on the global modals stylesheet
   Use the `size` prop to choose one of the standardized sizes: `sm` (420px), `md` (700px), `lg` (1000px).
*/
.modal-content {
  max-width: 700px; /* default; overridden by global classes */
}
.modal-sm {
  max-width: 420px;
}
.modal-md {
  max-width: 700px;
}
.modal-lg {
  max-width: 1000px;
}
</style>
