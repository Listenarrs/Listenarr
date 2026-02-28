<template>
  <div class="password-input">
    <!-- Explicitly bind attributes only to the input and prevent automatic inheritance -->
    <input v-bind="$attrs" class="password-field" :type="show ? 'text' : 'password'" :value="modelValue" @input="onInput" />
    <button
      type="button"
      class="password-toggle"
      @click="toggle"
      :aria-pressed="show"
      :aria-label="show ? 'Hide password' : 'Show password'"
      title="Toggle password visibility"
    >
      <PhEye v-if="!show" class="password-icon" />
      <PhEyeSlash v-else class="password-icon" />
      <span class="sr-only">{{ show ? 'Hide password' : 'Show password' }}</span>
    </button>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhEye, PhEyeSlash } from '@phosphor-icons/vue'

// Prevent attributes from being applied to the root element so consumer classes (e.g. `form-input`)
// are applied only to the inner <input> when bound via $attrs.
defineOptions({ inheritAttrs: false })

const props = withDefaults(defineProps<{ modelValue?: string }>(), { modelValue: '' })
const emit = defineEmits<{ (e: 'update:modelValue', v: string): void }>()

const show = ref(false)
function toggle() {
  show.value = !show.value
}

function onInput(e: Event) {
  const v = (e.target as HTMLInputElement).value
  emit('update:modelValue', v)
}
</script>

<style scoped>
.password-input {
  position: relative;
  display: inline-block;
  width: 100%;
}

/* Base input styles for the password field */
.password-field {
  padding: 0.75rem;
  padding-right: 3rem; /* Make room for the eye icon */
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: white;
  font-size: 1rem;
  width: 100%;
  box-sizing: border-box;
}

.password-field:focus {
  outline: none;
  border-color: var(--brand-500);
}

/* When consumers pass the shared .form-input class it will style the input. */
.password-field.form-input {
  padding-right: 3rem; /* Make room for the eye icon */
}

/* Toggle button positioned inside the input field */
.password-toggle {
  position: absolute;
  right: 0.75rem;
  top: 50%;
  transform: translateY(-50%);
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  padding: 0.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  transition: all 0.2s;
  font-size: 1.2rem;
}

.password-toggle:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.18);
  outline-offset: 2px;
}

.password-toggle:hover {
  background: rgba(77, 171, 247, 0.1);
  color: #4dabf7;
}

/* Remove default button appearance on some browsers */
.password-toggle {
  appearance: none;
}

.password-toggle .password-icon {
  width: 18px;
  height: 18px;
  display: block;
}

/* Accessible hidden text for screen readers */
.sr-only {
  position: absolute !important;
  width: 1px !important;
  height: 1px !important;
  padding: 0 !important;
  margin: -1px !important;
  overflow: hidden !important;
  clip: rect(0, 0, 0, 0) !important;
  white-space: nowrap !important;
  border: 0 !important;
}
</style>
