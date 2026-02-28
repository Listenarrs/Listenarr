<template>
  <span :class="['status-badge', `status-${status}`]">
    {{ statusLabel }}
  </span>
</template>

<script setup lang="ts">
type StatusType = 'active' | 'completed' | 'failed' | 'pending' | 'success' | 'paused' | 'cancelled' | 'moved'

const props = defineProps({
  status: {
    type: String as () => StatusType,
    required: true,
  },
})

const statusLabelMap: Record<StatusType, string> = {
  active: 'Active',
  completed: 'Completed',
  failed: 'Failed',
  pending: 'Pending',
  success: 'Success',
  paused: 'Paused',
  cancelled: 'Cancelled',
  moved: 'Moved',
}

const statusLabel = statusLabelMap[props.status as StatusType] || props.status
</script>

<style scoped>
.status-badge {
  display: inline-block;
  padding: 0.375rem 0.75rem;
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  white-space: nowrap;
  transition: all 0.2s;
}

.status-active {
  background: var(--brand-500, #2196f3);
  color: #fff;
}

.status-completed {
  background: var(--success-500, #51cf66);
  color: #000;
}

.status-moved {
  background: var(--success-600, #40c057);
  color: #fff;
}

.status-success {
  background: var(--success-500, #51cf66);
  color: #000;
}

.status-failed {
  background: var(--danger-500, #ff6b6b);
  color: #fff;
}

.status-pending {
  background: var(--warning-500, #ffa500);
  color: #000;
}

.status-paused {
  background: #9ca3af;
  color: #fff;
}

.status-cancelled {
  background: #6b7280;
  color: #fff;
}
</style>
