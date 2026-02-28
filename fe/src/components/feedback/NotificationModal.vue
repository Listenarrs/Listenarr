<template>
  <transition name="modal">
    <div v-if="visible" class="modal-overlay" @click.self="onClose">
      <div :class="['notification-modal', `notification-${type}`]">
        <div class="notification-icon">
          <slot name="icon">
            <i v-if="type === 'success'">✓</i>
            <i v-else-if="type === 'error'">!</i>
            <i v-else-if="type === 'warning'">!</i>
            <i v-else>i</i>
          </slot>
        </div>

        <div class="notification-content">
          <h3 v-if="title">{{ title }}</h3>
          <p v-if="message">{{ stripHtmlAndNormalize(message) }}</p>
          <slot />
        </div>

        <button class="notification-close" type="button" aria-label="Close" @click="onClose">✕</button>
      </div>
    </div>
  </transition>
</template>

<script setup lang="ts">
import { stripHtmlAndNormalize } from '@/utils/textUtils'

const props = defineProps({
  visible: { type: Boolean, default: false },
  title: { type: String, default: '' },
  message: { type: String, default: '' },
  type: { type: String as () => 'info' | 'success' | 'warning' | 'error', default: 'info' },
})
const emit = defineEmits(['close'])

function onClose() {
  emit('close')
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  background-color: rgba(0, 0, 0, 0.5);
  display: flex;
  justify-content: center;
  align-items: center;
  z-index: 9999;
  padding: 1rem;
}

.notification-modal {
  background: #2a2a2a;
  border-radius: 6px;
  padding: 1.5rem;
  max-width: 500px;
  width: 100%;
  display: flex;
  align-items: flex-start;
  gap: 1rem;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
  position: relative;
  animation: slideIn 0.3s ease-out;
}

.notification-icon {
  flex-shrink: 0;
}

.notification-icon i {
  font-size: 2.5rem;
}

.notification-success .notification-icon i {
  color: #4caf50;
}

.notification-error .notification-icon i {
  color: #f44336;
}

.notification-warning .notification-icon i {
  color: #ff9800;
}

.notification-info .notification-icon i {
  color: var(--brand-500);
}

.notification-content {
  flex: 1;
  color: #ffffff;
}

.notification-content h3 {
  margin: 0 0 0.5rem 0;
  font-size: 1.25rem;
  font-weight: 500;
}

.notification-content p {
  margin: 0;
  line-height: 1.5;
  color: #cccccc;
  white-space: pre-wrap;
}

.notification-close {
  position: absolute;
  top: 1rem;
  right: 1rem;
  background: none;
  border: none;
  color: #999;
  cursor: pointer;
  padding: 0.25rem;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
  font-size: 1.25rem;
}

.notification-close:hover {
  color: #fff;
}

/* Animations */
.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.3s ease;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-active .notification-modal {
  animation: slideIn 0.3s ease-out;
}

.modal-leave-active .notification-modal {
  animation: slideOut 0.3s ease-in;
}

@keyframes slideIn {
  from {
    transform: translateY(-20px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

@keyframes slideOut {
  from {
    transform: translateY(0);
    opacity: 1;
  }
  to {
    transform: translateY(-20px);
    opacity: 0;
  }
}

/* Responsive */
@media (max-width: 768px) {
  .notification-modal {
    max-width: calc(100vw - 2rem);
  }

  .notification-icon i {
    font-size: 2rem;
  }

  .notification-content h3 {
    font-size: 1.1rem;
  }
}
</style>
