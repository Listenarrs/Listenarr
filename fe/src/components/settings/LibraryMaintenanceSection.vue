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
  <div class="settings-section">
    <h3><PhWrench /> Library Maintenance</h3>

    <!-- Live move-queue status: in-flight moves, queue depth, recent failures. -->
    <MoveQueueStatusBanner />

    <div class="maintenance-action">
      <div class="maintenance-action-text">
        <strong>Organize library</strong>
        <small>
          Preview every audiobook against the folder your naming pattern computes for it, then queue
          background moves for the ones you confirm. Read-only until you apply.
        </small>
      </div>
      <button type="button" class="action-button" @click="showOrganizeModal = true">
        <PhFolderOpen />
        Organize library…
      </button>
    </div>

    <OrganizeLibraryModal :visible="showOrganizeModal" @close="showOrganizeModal = false" />
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhWrench, PhFolderOpen } from '@phosphor-icons/vue'
import OrganizeLibraryModal from '@/components/domain/organize/OrganizeLibraryModal.vue'
import MoveQueueStatusBanner from '@/components/domain/organize/MoveQueueStatusBanner.vue'

const showOrganizeModal = ref(false)
</script>

<style scoped>
.settings-section {
  margin-top: 2rem;
}

.settings-section h3 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
  font-size: 1.15rem;
  font-weight: 500;
  margin: 0 0 1rem;
}

.maintenance-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
  padding: 1rem 1.25rem;
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  margin-top: 1rem;
}

.maintenance-action-text {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.maintenance-action-text strong {
  color: #fff;
}

.maintenance-action-text small {
  color: #8a93a0;
  max-width: 46rem;
  line-height: 1.5;
}

.action-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.1rem;
  background: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 6px;
  color: var(--brand-500);
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
  white-space: nowrap;
}

.action-button:hover {
  background: rgba(var(--brand-rgb), 0.2);
}
</style>
