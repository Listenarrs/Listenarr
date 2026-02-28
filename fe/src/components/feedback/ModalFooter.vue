<template>
  <div data-modal-footer="true" class="modal-footer">
    <div class="modal-footer-left">
      <slot name="left">
        <button v-if="showCancel" type="button" class="cancel-button btn" @click="$emit('cancel')">
          <PhX />
          {{ cancelLabel }}
        </button>
      </slot>
    </div>

    <div class="modal-footer-actions">
      <slot>
        <button
          v-if="showTest"
          type="button"
          class="btn btn-info"
          @click="$emit('test')"
          :disabled="testing"
        >
          <PhSpinner v-if="testing" class="ph-spin" />
          <PhGear v-else />
          {{ testing ? testLabelLoading : testLabel }}
        </button>

        <button
          v-if="showSave"
          type="button"
          class="btn btn-primary"
          @click="$emit('save')"
          :disabled="saving"
        >
          <PhSpinner v-if="saving" class="ph-spin" />
          <PhCheck v-else />
          {{ saving ? saveLabelLoading : saveLabel }}
        </button>
      </slot>
    </div>
  </div>
</template>

<script setup lang="ts">
import { PhX, PhSpinner, PhGear, PhCheck } from '@phosphor-icons/vue'

const props = defineProps({
  showCancel: { type: Boolean, default: false },
  showTest: { type: Boolean, default: false },
  showSave: { type: Boolean, default: false },
  testing: { type: Boolean, default: false },
  saving: { type: Boolean, default: false },
  cancelLabel: { type: String, default: 'Cancel' },
  testLabel: { type: String, default: 'Test' },
  testLabelLoading: { type: String, default: 'Testing...' },
  saveLabel: { type: String, default: 'Save' },
  saveLabelLoading: { type: String, default: 'Saving...' },
})

const emit = defineEmits(['cancel', 'test', 'save'])
</script>

<style scoped>
.modal-footer {
  display: flex;
  align-items: center;
  justify-content: space-between; /* cancel on far left, actions on far right */
  gap: 1rem;
  padding: 1rem 2rem;
  border-top: 1px solid #444;
}

.modal-footer-left {
  display: flex;
  gap: 0.75rem;
  align-items: center;
  flex: 0 0 auto;
}

.modal-footer-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
  margin-left: 0; /* actions sit at the right because of space-between */
}

@media (max-width: 480px) {
  .modal-footer {
    flex-direction: column;
    align-items: stretch;
  }
  .modal-footer-left {
    order: 0;
    margin-bottom: 0.5rem;
    align-items: flex-start;
  }
  .modal-footer-actions {
    order: 1;
    display: flex;
    gap: 0.5rem;
    justify-content: stretch;
  }
  .modal-footer-actions .btn { width: 100%; }
}
</style>