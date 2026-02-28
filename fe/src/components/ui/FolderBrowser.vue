<template>
  <teleport to="body" v-if="!inline">
    <div :class="['folder-browser browser-modal', { 'no-inner-card': !props.useInnerCard }]" ref="root" role="dialog" aria-label="Folder Browser">
      <div class="folder-browser-backdrop" @click="closeBrowser" aria-hidden="true"></div>
      <div class="browser-wrapper">
        <div class="browser-content" role="region">
          <div class="browser-body">
            <div>Modal content here</div>
          </div>
        </div>
      </div>
    </div>
  </teleport>

  <div v-else :class="['folder-browser browser-inline', { 'no-inner-card': !props.useInnerCard }]" ref="root" role="dialog" aria-label="Folder Browser">
    <div v-if="showInput" class="browser-input-group">
      <input
        v-model="localPath"
        class="browser-input form-input"
        type="text"
        placeholder="Enter path..."
        @keydown.enter.prevent="browseDirectory(localPath)"
        aria-label="Path"
      />
      <button type="button" class="icon-btn btn-secondary btn-inline-browse" @click="openBrowser" title="Browse folders" aria-label="Browse folders"><PhFolder /></button>
    </div>

    <div v-if="validationMessage" :class="['validation-message', isValid ? 'success' : 'error']">
      <span>{{ validationMessage }}</span>
    </div>

    <div v-if="isOpen" class="browser-content" role="region">
      <div class="browser-body">
        <div v-if="breadcrumbs.length" class="breadcrumbs" role="navigation" aria-label="Breadcrumb">
          <button class="breadcrumb-item breadcrumb-home" @click="browseDirectory()" title="Root" aria-label="Go to root">
            <PhHouse class="breadcrumb-icon" />
          </button>
          <span class="breadcrumb-separator">/</span>
          <template v-for="(crumb, index) in breadcrumbs" :key="crumb.path">
            <button
              v-if="index < breadcrumbs.length - 1"
              class="breadcrumb-item"
              @click="browseDirectory(crumb.path)"
              :title="`Go to ${crumb.name}`"
              :aria-label="`Go to ${crumb.name}`"
            >
              {{ crumb.name }}
            </button>
            <span v-else class="breadcrumb-item current">{{ crumb.name }}</span>
            <span v-if="index < breadcrumbs.length - 1" class="breadcrumb-separator">/</span>
          </template>
        </div>

        <div v-if="isLoading" class="loading-state">
          <div class="spinner-container">
            <div class="spinner-ring"></div>
          </div>
          <div class="loading-text">Loading…</div>
        </div>

        <div v-else>
          <div v-if="showSearch" class="search-group">
            <div class="search-input-wrapper">
              <PhMagnifyingGlass class="search-icon" />
              <input
                v-model="searchQuery"
                class="search-input form-input"
                type="text"
                placeholder="Search folders..."
                @input="filterItems"
                aria-label="Search folders"
              />
            </div>
          </div>

          <div v-if="items.length === 0" class="empty-state"><PhFolderOpen class="empty-icon" /><div>No items found</div></div>

          <div v-else-if="filteredItems.length === 0" class="empty-state"><PhMagnifyingGlass class="empty-icon" /><div>No matches found</div></div>

          <transition-group name="list" tag="div" class="directory-list" role="list" tabindex="0" @keydown="handleKeydown">
            <div v-if="parentPath && !searchQuery" key="parent" class="directory-item parent-item" role="listitem" @click="selectParentPath">
              <div class="item-icon">⬆</div>
              <div class="directory-item-main">.. <span class="muted">(parent)</span></div>
            </div>

            <div
              v-for="(it, index) in filteredItems"
              :key="it.path"
              :class="['directory-item', it.isDirectory ? '' : 'file-item', { selected: selectedIndex === index }]"
              role="listitem"
              @click="handleItemClick(it)"
              @mouseenter="selectedIndex = index"
              :title="it.isDirectory ? `Open folder: ${it.name}` : `File: ${it.name} (${formatSize(it.size)})`"
            >
              <div class="item-icon" :aria-hidden="true"><PhFolder v-if="it.isDirectory" style="color: #ffc857" /><PhFile v-else /></div>
              <div class="directory-item-main">
                <div class="item-name">{{ it.name }}</div>
                <small v-if="it.size" class="item-meta">{{ formatSize(it.size) }}</small>
              </div>
            </div>
          </transition-group>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, watch, computed } from 'vue'
import { apiService } from '@/services/api'
import { PhFolder, PhFolderOpen, PhFile, PhHouse, PhMagnifyingGlass } from '@phosphor-icons/vue'

interface FileSystemItem {
  name: string
  path: string
  isDirectory: boolean
  size?: number | null
}

const props = defineProps({
  showFiles: { type: Boolean, default: false },
  modelValue: { type: String, required: false },
  showInput: { type: Boolean, default: true },
  /** Show search input. Default: true (always show). Set to false to hide. */
  showSearch: { type: Boolean, default: true },
  inline: { type: Boolean, default: false },
  autoBrowse: { type: Boolean, default: true },
  // If true, clicking a folder in inline mode selects immediately. Set to false when embedded in a modal.
  autoSelect: { type: Boolean, default: true },
  // Optional controlled open state (undefined = uncontrolled)
  open: { type: Boolean as () => boolean | undefined, required: false },
  // When embedded inside a modal, you may want to hide the browser's internal header
  showHeader: { type: Boolean, default: true },
  useInnerCard: { type: Boolean, default: true },
})

const { showInput } = props
const emit = defineEmits<{
  (e: 'update:modelValue', v: string | null): void
  (e: 'update:open', v: boolean): void
  (e: 'browser-opened'): void
  (e: 'browser-closed'): void
  (e: 'path-draft', v: string): void
  (e: 'open-modal'): void
}>()


const root = ref<HTMLElement | null>(null)
const localPath = ref(props.modelValue ?? '')

// Emit drafts when the input changes so parent modal can pick up typed path
watch(() => localPath.value, (v) => {
  emit('path-draft', v ?? '')
})

const currentPath = ref<string | null>(null)
const parentPath = ref<string | null>(null)
const items = ref<FileSystemItem[]>([])
const isLoading = ref(false)
const validationMessage = ref('')
const isValid = ref(false)
const selectedIndex = ref(-1)
const searchQuery = ref('')

const filteredItems = computed(() => {
  if (!searchQuery.value) return items.value
  const query = searchQuery.value.toLowerCase()
  return items.value.filter(item => item.name.toLowerCase().includes(query))
})

const breadcrumbs = computed(() => {
  if (!currentPath.value) return []
  const raw = currentPath.value
  const separator = raw.includes('\\') ? '\\' : '/' // keep as JS string
  const parts = raw.split(/[/\\]/).filter(p => p)
  const crumbs = []
  let path = ''
  const isUNC = raw.startsWith('\\\\')

  for (let i = 0; i < parts.length; i++) {
    const part = parts[i]
    if (i === 0 && isUNC) {
      // UNC root: \\server
      path = '\\\\' + part
      crumbs.push({ name: part, path })
      continue
    }

    if (i === 0 && typeof part === 'string' && /^[A-Za-z]:$/.test(part)) {
      // Drive letter: ensure we point to root (C:\)
      path = part + separator
      crumbs.push({ name: part, path })
      continue
    }

    // Normal segment
    path += (path && !path.endsWith(separator) ? separator : '') + part
    crumbs.push({ name: part, path })
  }

  return crumbs
})

async function browseDirectory(path?: string) {
  isLoading.value = true
  validationMessage.value = ''
  try {
    const p = path && path.length ? path : undefined
    const r = await apiService.browseDirectory(p)
    // Expect: { currentPath, parentPath, items }
    currentPath.value = r.currentPath
    parentPath.value = r.parentPath ?? null
    const itemsRes = (r.items || []) as FileSystemItem[]
    items.value = itemsRes.filter((it) => (props.showFiles ? true : it.isDirectory))
    localPath.value = currentPath.value || localPath.value
    // Validate path after browsing
    await validatePath()
  } catch {
    validationMessage.value = 'Failed to browse directory'
    console.error('browseDirectory error')
  } finally {
    isLoading.value = false
  }
}

async function validatePath() {
  if (!localPath.value) {
    isValid.value = false
    validationMessage.value = ''
    return
  }
  try {
    const res = await apiService.validatePath(localPath.value)
    isValid.value = !!(res && res.isValid)
    validationMessage.value = res?.message ?? (isValid.value ? 'Valid' : 'Invalid')
  } catch {
    isValid.value = false
    validationMessage.value = 'Failed to validate path'
  }
}

function formatSize(size?: number | null) {
  if (size == null) return ''
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let v = size
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v = v / 1024
    i++
  }
  return `${Math.round(v * 10) / 10} ${units[i]}`
}

// navigateToParent was removed; use selectParentPath() which handles selection/navigation

function openBrowser() {
  // When embedded inline, request the parent to show a modal instance instead of expanding inline
  if (props.inline) {
    emit('open-modal')
    return
  }

  // Non-inline usage: open internal content
  if (props.open !== undefined) emit('update:open', true)
  isOpen.value = true
  browseDirectory(localPath.value || undefined)
}

function selectParentPath() {
  if (!parentPath.value) return
  localPath.value = parentPath.value
  if (props.inline && props.autoSelect) {
    // Inline usage selects immediately when autoSelect is enabled
    emit('update:modelValue', parentPath.value)
    isOpen.value = false
    emit('browser-closed')
    return
  }
  // In modal usage, or inline-but-not-autoSelect: update draft and navigate but don't finalize selection
  emit('path-draft', parentPath.value)
  browseDirectory(parentPath.value)
}

function handleItemClick(item: FileSystemItem) {
  if (!item.isDirectory) return
  localPath.value = item.path
  if (props.inline && props.autoSelect) {
    // Inline usage selects immediately when autoSelect is enabled
    emit('update:modelValue', item.path)
  } else {
    // Modal usage or non-autoSelect inline usage: draft the path and navigate, do not finalize selection
    emit('path-draft', item.path)
  }
  browseDirectory(item.path)
}

function handleKeydown(event: KeyboardEvent) {
  if (!filteredItems.value.length) return
  const maxIndex = filteredItems.value.length - 1
  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault()
      selectedIndex.value = Math.min(selectedIndex.value + 1, maxIndex)
      break
    case 'ArrowUp':
      event.preventDefault()
      selectedIndex.value = Math.max(selectedIndex.value - 1, 0)
      break
    case 'Enter':
      event.preventDefault()
      if (selectedIndex.value >= 0 && selectedIndex.value <= maxIndex) {
        const it = filteredItems.value[selectedIndex.value]
        if (it) handleItemClick(it)
      }
      break
    case 'Home':
      event.preventDefault()
      selectedIndex.value = 0
      break
    case 'End':
      event.preventDefault()
      selectedIndex.value = maxIndex
      break
  }
}

function filterItems() {
  // Reactive, but can add debouncing if needed
  selectedIndex.value = -1
}

// Controlled or uncontrolled open state
const isOpen = ref(props.open !== undefined ? !!props.open : !props.inline)

// Keep controlled prop in sync if provided
if (props.open !== undefined) {
  watch(
    () => props.open,
    (v) => {
      isOpen.value = !!v
    },
  )
}

function closeBrowser() {
  isOpen.value = false
  emit('browser-closed')
  if (props.open !== undefined) emit('update:open', false)
}

// Emit browser-opened/closed when isOpen changes
watch(
  () => isOpen.value,
  (v) => {
    if (v) emit('browser-opened')
    else emit('browser-closed')
    if (props.open !== undefined) emit('update:open', v)
  },
)


onMounted(() => {
  if (props.modelValue) localPath.value = props.modelValue
  // initial browse for convenience (only when open)
  if (isOpen.value && props.autoBrowse) {
    browseDirectory(localPath.value || undefined)
  }
})

// Keep in sync when the parent updates the modelValue (e.g., recent folder selection)
watch(() => props.modelValue, (v) => {
  localPath.value = v ?? ''
  // Validate the incoming path so the parent sees validation feedback immediately
  if (localPath.value) validatePath()
})
</script>

<style scoped>
/* Align with centralized modal palette */
.folder-browser { display:flex; flex-direction:column; gap:0.75rem; width:100% }
.browser-input-group { display:flex; gap:0.5rem }
.browser-input {
  flex:1;
  padding:0.6rem 0.75rem;
  background: #1a1a1a; /* match modal form inputs */
  border: 1px solid #444;
  border-radius:6px;
  color:#eef2f8;
  font-size:0.95rem;
} 

/* Compact browse button to sit next to the input */



.browser-content {
  background:#2a2a2a; /* modal-content background */
  border:1px solid #444; /* modal border */
  border-radius:8px;
  width:100%;
  overflow: hidden;
  display:flex;
  flex-direction:column; /* contain header/search and list */
  max-height: 70vh; /* ensure a definite constraint for flex children */
}

.browser-header {
  display:flex; justify-content:space-between; align-items:center; padding:0.8rem 1rem; border-bottom:1px solid #444;
}
.browser-header .title { display:flex; align-items:center; gap:0.5rem; color:#fff; font-weight: 500 }
.header-actions { display:flex; gap:0.5rem }


.browser-body { padding:1rem; display:flex; flex-direction:column; gap:0.75rem; flex:1 1 auto; min-height:0 }
.breadcrumbs { display:flex; align-items:center; gap:0.5rem; margin-bottom:0; flex-wrap:wrap; padding:0; background:transparent; border-radius:6px; }
.breadcrumb-item { background: transparent; border: 1px solid rgba(255,255,255,0.03); color:#aaa; cursor:pointer; padding:0.2rem 0.45rem; border-radius:6px; font-size:0.9rem; transition: all 0.12s ease; font-weight:500 }
.breadcrumb-item:hover { background: rgba(255,255,255,0.02); color:#fff }
.breadcrumb-item.current { color:var(--brand-600); font-weight: 500; cursor:default; background: rgba(var(--brand-rgb),0.08); border-color: rgba(var(--brand-rgb),0.12) }
.breadcrumb-home { padding:0.25rem; display:flex; align-items:center; justify-content:center; background:transparent; border-radius:6px; border: 1px solid rgba(255,255,255,0.02) }
.breadcrumb-home:hover { background: rgba(255,255,255,0.02) }
.breadcrumb-icon { width:16px; height:16px; color:#ccc }
.breadcrumb-separator { color:#666; font-weight:400; margin:0 0.25rem }
.loading-state { display:flex; flex-direction:column; gap:0.75rem; align-items:center; justify-content:center; color:#fff; padding:3rem 0; min-height:200px }
.spinner-container { position:relative; display:flex; align-items:center; justify-content:center; width:64px; height:64px }
.spinner-icon { width:24px; height:24px; color:#2196f3; animation: pulse 2s ease-in-out infinite }
.spinner-ring {
  position:absolute;
  width:100%;
  height:100%;
  border:3px solid #333;
  border-top:3px solid #2196f3;
  border-radius:50%;
  animation: spin 1.5s linear infinite;
}
@keyframes spin { 0% { transform:rotate(0deg) } 100% { transform:rotate(360deg) } }
@keyframes pulse { 0%, 100% { opacity:1 } 50% { opacity:0.5 } }
@keyframes fadeInOut { 0%, 100% { opacity:0.7 } 50% { opacity:1 } }
/* @keyframes modalSlideIn and backdropFadeIn are centralized in src/assets/animations.css */

.list-enter-active, .list-leave-active { transition: all 0.3s ease }
.list-enter-from { opacity:0; transform:translateY(10px) }
.list-leave-to { opacity:0; transform:translateY(-10px) }
.list-move { transition: transform 0.3s ease }
.loading-text { font-size:1rem; font-weight:500; color:#ccc; animation: fadeInOut 2s ease-in-out infinite }

.fade-enter-active, .fade-leave-active { transition: opacity 0.3s ease }
.fade-enter-from, .fade-leave-to { opacity:0 }
.empty-state { display:flex; flex-direction:column; gap:0.75rem; align-items:center; justify-content:center; color:#999; padding:2rem 0 }
.empty-icon { color:#868e96; width:48px; height:48px }
.empty-state div { font-size:1.1rem; font-weight:500 }

.search-group { margin-bottom:1rem }
.search-input-wrapper { position:relative; display:flex; align-items:center }
.search-icon { position:absolute; left:0.65rem; width:18px; height:18px; color:#7a7a7a; pointer-events:none; z-index:1 }
.search-input {
  width:100%;
  padding:0.6rem 0.75rem 0.6rem 2.2rem;
  background:var(--modal-input-bg, #161616);
  border:1px solid rgba(255,255,255,0.04);
  border-radius:6px;
  color:#e6eef8;
  font-size:0.92rem;
  transition: box-shadow 0.12s ease, border-color 0.12s ease;
}
.search-input:focus { outline:none; border-color:var(--brand-focus); box-shadow:0 0 0 3px rgba(var(--brand-rgb), 0.08); background:#1b1b1b }
/* Directory list scrolling - use flex so list fills remaining body space and scrolls */
.directory-list {
  display:flex;
  flex-direction:column;
  gap:0.5rem;
  flex: 1 1 auto;
  min-height: 0; /* allow flex children to shrink and enable overflow */
  overflow: auto;
  padding-right:0.25rem;
}
.directory-list::-webkit-scrollbar { width:10px }
.directory-list::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.04); border-radius:6px }

/* Inline usage: cap height when not in a modal so it doesn't grow too tall */
.folder-browser.browser-inline .directory-list {
  max-height: calc(70vh - 355px);
  overflow-y: auto;
}

/* When used as a modal ensure the list fills the available body space */
.folder-browser.browser-modal .directory-list {
  max-height: none; /* let flex sizing take over */
}

.directory-item { display:flex; gap:0.5rem; align-items:center; padding:0.45rem 0.6rem; background:transparent; border:1px solid rgba(255,255,255,0.02); border-radius:6px; color:#fff; cursor:pointer; transition: box-shadow 0.12s ease, transform 0.12s ease; transform: translateY(0); }
.directory-item:hover { transform:translateY(-1px); box-shadow:0 4px 10px rgba(0,0,0,0.32); background:rgba(255,255,255,0.01); border-color:rgba(255,255,255,0.03) }
.directory-item:focus { outline: none; box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.06); border-color: rgba(var(--brand-rgb), 0.1) }
.directory-item:active { transform:translateY(0); transition-duration: 0.08s }
.directory-item .item-icon { width:32px; height:32px; display:inline-flex; align-items:center; justify-content:center; border-radius:6px; background:rgba(255,255,255,0.03); color:#fff; font-size:16px; flex-shrink:0 }
.directory-item .item-name { font-weight: 500; font-size:0.95rem }
.directory-item .item-meta { color:#9aa0a6; display:block; margin-top:2px; font-size:0.8rem; font-weight:400 }
.directory-item.selected { background: rgba(var(--brand-rgb), 0.06); border-color: rgba(var(--brand-rgb), 0.12); box-shadow: none; border-left: 4px solid var(--brand-600); padding-left: calc(0.75rem - 2px) }

.validation-message { padding:0.5rem; border-radius:6px; font-size:0.9rem }
.validation-message.error { background:rgba(231,76,60,0.1); color:#e74c3c }
.validation-message.success { background:rgba(46,204,113,0.08); color:#2ecc71 }

@media (max-width:720px) {
  .browser-content { max-width:100%; }
  .directory-list { max-height:220px }
}

/* When used as a modal (not inline), center and float above other overlays */
.folder-browser.browser-modal {
  position: fixed;
  left: 50%;
  top: 80px;
  transform: translateX(-50%);
  width: 94%;
  max-width: 920px;
  z-index: 2001; /* above modal overlay (1000) */
  pointer-events: auto;
  display: block;
  animation: modalSlideIn 0.3s cubic-bezier(0.34, 1.56, 0.64, 1);
}

/* Backdrop behind the modal */
.folder-browser-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0,0,0,0.6);
  z-index: 2000;
  animation: backdropFadeIn 0.3s ease;
}

/* Slight shadow and border to stand out above the underlying modal */
.folder-browser.browser-modal {
  border-radius: 8px;
  overflow: visible;
}
.folder-browser.browser-modal .browser-content {
  box-shadow: 0 24px 60px rgba(0,0,0,0.6);
  max-height: 70vh;
  overflow: hidden;
  border-radius: 8px;
  margin: 12px; /* create breathing room around content */
  background: #242424;
  border: 1px solid rgba(255,255,255,0.04);
}

/* inner content scroll area */
.folder-browser.browser-modal .browser-body {
  overflow: auto;
  padding: 1.5rem;
}

/* Modal input wrapper spacing */
.folder-browser.browser-modal .browser-input-group {
  padding: 1rem 1.5rem 0 1.5rem;
}
.folder-browser.browser-modal .browser-input { width: calc(100% - 120px); }


</style>
