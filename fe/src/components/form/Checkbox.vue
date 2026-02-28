<template>
  <label class="input-checkbox">
    <input type="checkbox" :checked="modelValue" @change="$emit('update:modelValue', ($event.target as HTMLInputElement)?.checked ?? false)" />
    <span class="checkbox-box" aria-hidden="true"></span>
    <span class="checkbox-label"><slot /></span>
  </label> 

</template>

<script setup lang="ts">
const props = defineProps({ modelValue: { type: Boolean, default: false } })
const emit = defineEmits(['update:modelValue'])
</script>

<style scoped>
.input-checkbox { display:flex; align-items:center; gap:0.75rem; cursor:pointer; }
.input-checkbox input { position:absolute; opacity:0; width:0; height:0 }
.checkbox-box {
  width:18px;
  height:18px;
  border:1px solid rgba(255,255,255,0.12);
  border-radius:3px;
  background:transparent;
  display:inline-flex;
  align-items:center;
  justify-content:center;
  flex-shrink:0;
  margin-top:0;
  align-self: center;
  position: relative;
  transition: background 0.15s ease, border-color 0.15s ease;
}
.input-checkbox input:checked + .checkbox-box {
  background: var(--brand-500);
  border-color: var(--brand-500);
}
/* Checkmark drawn with a CSS pseudo-element so we don't rely on extra markup */
.checkbox-box::after {
  content: '';
  position: absolute;
  left: 5px;
  top: 50%;
  width: 5px;
  height: 9px;
  border: solid transparent;
  border-width: 0 2px 2px 0;
  border-color: #fff;
  transform: translateY(-50%) rotate(45deg) scale(0.8);
  opacity: 0;
  transition: opacity 0.12s ease, transform 0.12s ease;
}
.input-checkbox input:checked + .checkbox-box::after {
  opacity: 1;
  transform: translateY(-50%) rotate(45deg) scale(1);
}
.input-checkbox input:focus-visible + .checkbox-box {
  box-shadow: 0 0 0 3px rgba(30, 136, 229, 0.12);
  outline: none;
}

.checkbox-label { color:var(--text, #e6eef8); display:flex; flex-direction:column; text-align: left; }
/* Make the first label line a fixed-height row (the same as the checkbox) and center its text */
.checkbox-label > * { display:block }
.checkbox-label > :first-child {
  display:flex; justify-content: flex-start; }
.checkbox-label strong { line-height:1; font-weight: 500 }
.checkbox-label small { color:var(--color-text-secondary); font-size:0.85rem; font-weight:400; margin-top:0.25rem }
</style>