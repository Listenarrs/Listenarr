<template>
  <div class="form-section">
    <h3>
      <PhMagnifyingGlass /> Search Settings
    </h3>
    <div class="form-body">
      <CheckboxCard :modelValue="settings.enableOpenLibrarySearch" @update:modelValue="v => updateField('enableOpenLibrarySearch', v)" title="Enable OpenLibrary Searching" description="Include OpenLibrary title augmentation and lookups when performing intelligent searches." />

    </div>
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { PhMagnifyingGlass } from '@phosphor-icons/vue'
// Checkbox usage handled via CheckboxCard; no direct Checkbox import needed here
import CheckboxCard from '@/components/settings/CheckboxCard.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

/* Modal-like search settings */
.form-body { padding: 1.25rem; border-radius: 6px; border: 1px solid #333; box-shadow: 0 4px 14px rgba(0,0,0,0.6); background-color: #232323; }

.form-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 1.5rem; }

.form-group label { margin-bottom: 0.5rem; font-weight: 500; color: #fff }

.form-group input[type='number'] { width: 100%; padding: 0.9rem 0.85rem; border: 1px solid #444; border-radius: 6px; background-color: #1a1a1a; color: #fff; font-size: 0.95rem }

.form-group input:focus { outline:none; border-color:var(--brand-500); box-shadow:0 0 0 3px rgba(77,171,247,0.08); }
.form-group input::placeholder { color: #6c757d }

.form-help { display:block; margin-top:0.5rem; font-size:0.85rem; color:#adb5bd; line-height:1.5 }
</style>