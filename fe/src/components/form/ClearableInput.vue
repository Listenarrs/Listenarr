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
<!-- A text field with an erase button inside it, on the right, while it holds something. Its room is
     always kept, so showing the button doesn't change the field's size. Emptying it emits cleared;
     the attributes and listeners given go to the input (v-model.trim trims what is typed). -->
<template>
  <span class="clearable-input">
    <input ref="inputEl" v-model="model" class="form-input" v-bind="$attrs" />
    <button
      v-if="model"
      type="button"
      class="clearable-input-clear"
      :title="clearLabel"
      :aria-label="clearLabel"
      :data-testid="clearTestId"
      @click.prevent="clear"
    >
      <PhBackspace :size="18" />
    </button>
  </span>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhBackspace } from '@phosphor-icons/vue'

defineOptions({ inheritAttrs: false })

defineProps<{
  // The erase button's tooltip and accessible name
  clearLabel: string
  clearTestId?: string
}>()

const [model, modifiers] = defineModel<string>({
  required: true,
  set: (value) => (modifiers.trim ? value.trim() : value),
})

const emit = defineEmits<{ cleared: [] }>()

const inputEl = ref<HTMLInputElement | null>(null)

function clear() {
  model.value = ''
  emit('cleared')
}

// Focused with its text selected, ready to be typed over
function focusAndSelect() {
  inputEl.value?.focus()
  inputEl.value?.select()
}

defineExpose({ focusAndSelect })
</script>

<style scoped>
.clearable-input {
  position: relative;
  display: block;
}

.clearable-input .form-input {
  width: 100%;
  box-sizing: border-box;
  padding-right: 2.2rem;
}

.clearable-input-clear {
  position: absolute;
  top: 50%;
  right: 0.4rem;
  transform: translateY(-50%);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0.25rem;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: #aaa;
  cursor: pointer;
}

.clearable-input-clear:hover {
  color: var(--status-error);
  background: rgba(255, 255, 255, 0.06);
}
</style>
