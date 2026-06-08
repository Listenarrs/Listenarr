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
  <div class="system-view">
    <div class="page-header-with-actions">
      <div class="settings-header">
        <h1>
          <PhCpu />
          System
        </h1>
        <p>Monitor system health, resources, and service status</p>
      </div>
      <div class="page-actions">
        <button class="add-button btn btn-primary" @click="refreshStatus" :disabled="loading">
          <component :is="loading ? PhSpinner : PhArrowClockwise" />
          {{ loading ? 'Refreshing...' : 'Refresh' }}
        </button>
      </div>
    </div>

    <div class="system-status">
      <div v-if="error" class="error-message">
        <PhWarning />
        {{ error }}
      </div>

      <LoadingState v-if="loading && !systemInfo" message="Loading system information..." />

      <div v-else class="status-grid">
        <StatusCard title="API Status" :icon="PhCheckCircle">
          <template #header-badge>
            <span :class="['status-badge', apiStatus.status]">
              {{ apiStatus.status }}
            </span>
          </template>
          <div class="detail-row">
            <PhCode />
            <span class="label">Version:</span>
            <span class="value">{{ apiStatus.version }}</span>
          </div>
          <div class="detail-row">
            <PhClock />
            <span class="label">Uptime:</span>
            <span class="value">{{ apiStatus.uptime }}</span>
          </div>
        </StatusCard>

        <StatusCard title="Download Clients" :icon="PhDownload">
          <template #header-badge>
            <span :class="['status-badge', downloadClientsStatus.status]">
              {{ downloadClientsStatus.connected }}/{{ downloadClientsStatus.total }}
            </span>
          </template>
          <div v-if="downloadClients.length === 0" class="empty-message">
            <PhInfo />
            <span>No download clients configured</span>
          </div>
          <div v-else>
            <div v-for="client in downloadClients" :key="client.name" class="client-status">
              <component
                :is="client.status === 'connected' ? PhCheckCircle : PhXCircle"
                :class="client.status === 'connected' ? 'success' : 'error'"
              />
              <span class="client-name">{{ client.name }}</span>
              <span :class="['client-indicator', client.status]">{{ client.status }}</span>
            </div>
          </div>
        </StatusCard>

        <StatusCard title="Storage" :icon="PhHardDrives">
          <StorageDisksList v-if="storageInfo" :disks="storageInfo.disks" />
        </StatusCard>

        <StatusCard title="External APIs" :icon="PhCloud">
          <template #header-badge>
            <span :class="['status-badge', externalApis.status]">
              {{ externalApis.connected }}/{{ externalApis.total }}
            </span>
          </template>
          <div v-if="externalApis.apis.length === 0" class="empty-message">
            <PhInfo />
            <span>No API sources configured</span>
          </div>
          <div v-else>
            <div v-for="api in externalApis.apis" :key="api.name" class="client-status">
              <component
                :is="api.status === 'connected' ? PhCheckCircle : PhXCircle"
                :class="api.status === 'connected' ? 'success' : 'error'"
              />
              <span class="client-name">{{ api.name }}</span>
              <span :class="['client-indicator', api.status]">{{ api.status }}</span>
            </div>
          </div>
        </StatusCard>

        <StatusCard title="WebSockets" :icon="PhWifiX">
          <template #header-badge>
            <span :class="['status-badge', isConnected ? 'healthy' : 'error']">
              {{ isConnected ? 'Connected' : 'Disconnected' }}
            </span>
          </template>
          <div class="detail-row">
            <PhBroadcast />
            <span class="label">Real-time:</span>
            <span class="value">{{ isConnected ? 'Active' : 'Inactive' }}</span>
          </div>
          <div class="detail-row">
            <PhActivity />
            <span class="label">Status:</span>
            <span class="value">{{ isConnected ? 'Receiving updates' : 'Not connected' }}</span>
          </div>
        </StatusCard>
      </div>
    </div>

    <div class="system-sections">
      <div class="section">
        <div class="section-header">
          <h2>
            <PhInfo />
            System Information
          </h2>
        </div>
        <div v-if="systemInfo" class="info-grid">
          <InfoCard :icon="PhDesktopTower">
            <template #label>Operating System</template>
            <span>{{ systemInfo.operatingSystem }}</span>
          </InfoCard>
          <InfoCard :icon="PhCode">
            <template #label>Runtime</template>
            <span>{{ systemInfo.runtime }}</span>
          </InfoCard>
          <InfoCard :icon="PhMemory">
            <template #label>Memory Usage</template>
            <div class="info-content">
              <span
                >{{ systemInfo.memory.usedFormatted }} /
                {{ systemInfo.memory.totalFormatted }}</span
              >
              <span class="percentage">({{ systemInfo.memory.usedPercentage.toFixed(1) }}%)</span>
            </div>
          </InfoCard>
          <InfoCard :icon="PhCpu">
            <template #label>CPU Usage</template>
            <div class="info-content">
              <span>{{ systemInfo.cpu.usagePercentage.toFixed(1) }}%</span>
              <span class="percentage">({{ systemInfo.cpu.processorCount }} cores)</span>
            </div>
          </InfoCard>
        </div>
        <LoadingState v-else message="Loading system information..." />
      </div>

      <div class="section">
        <div class="section-header">
          <div class="section-title-with-status">
            <h2>
              <PhFileText />
              Recent Logs
            </h2>
            <div
              class="connection-status"
              :class="{ connected: logsConnected, disconnected: !logsConnected }"
            >
              <span class="status-dot"></span>
              {{ logsConnected ? 'Live' : 'Disconnected' }}
            </div>
          </div>
          <div class="section-actions">
            <button class="icon-button" @click="clearSystemLogs" title="Clear Logs">
              <PhTrash />
            </button>
            <button class="icon-button" @click="viewFullLogs" title="View Full Logs">
              <PhEye />
            </button>
            <button class="icon-button" @click="downloadLogs" title="Download Logs">
              <PhDownloadSimple />
            </button>
          </div>
        </div>
        <div class="logs-container">
          <div v-for="log in recentLogs" :key="log.id" :class="['log-entry', log.level]">
            <component :is="getLogIconComponent(log.level)" class="log-icon" />
            <span class="log-time">{{ formatLogTime(log.timestamp) }}</span>
            <span class="log-level">{{ log.level.toUpperCase() }}</span>
            <span class="log-message">{{ log.message }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import {
  PhCpu,
  PhSpinner,
  PhArrowClockwise,
  PhWarning,
  PhCheckCircle,
  PhXCircle,
  PhCode,
  PhClock,
  PhDownload,
  PhInfo,
  PhHardDrives,
  PhCloud,
  PhWifiX,
  PhBroadcast,
  PhActivity,
  PhDesktopTower,
  PhMemory,
  PhFileText,
  PhEye,
  PhDownloadSimple,
  PhTrash,
} from '@phosphor-icons/vue'
import { useSignalR } from '@/composables/useSignalR'
import { useSystemLogs } from '@/composables/useSystemLogs'
import { useRouter } from 'vue-router'
import { LoadingState, StatusCard, InfoCard } from '@/components/base'
import StorageDisksList from '@/components/system/StorageDisksList.vue'
import {
  getSystemInfo,
  getStorageInfo,
  getServiceHealth,
  downloadLogs as downloadLogsApi,
} from '@/services/api'
import type { SystemInfo, StorageInfo, ServiceHealth } from '@/types'
import { logger } from '@/utils/logger'

const router = useRouter()

// Real data from API
const systemInfo = ref<SystemInfo | null>(null)
const storageInfo = ref<StorageInfo | null>(null)
const serviceHealth = ref<ServiceHealth | null>(null)
const loading = ref(true)
const error = ref<string | null>(null)

// Computed values for easier template access
const apiStatus = ref({
  status: 'unknown' as string,
  version: '',
  uptime: '',
})

const downloadClientsStatus = ref({
  status: 'unknown' as string,
  connected: 0,
  total: 0,
})

const downloadClients = ref<Array<{ name: string; status: string }>>([])

const externalApis = ref({
  status: 'unknown' as string,
  connected: 0,
  total: 0,
  apis: [] as Array<{ name: string; status: string }>,
})

// Real-time logs using SignalR
const {
  logs: recentLogs,
  isConnected: logsConnected,
  clearLogs: clearSystemLogs,
} = useSystemLogs(50)

// Load all system data
const loadSystemData = async () => {
  loading.value = true
  error.value = null

  try {
    // Load all data in parallel
    const [sysInfo, storage, health] = await Promise.all([
      getSystemInfo(),
      getStorageInfo(),
      getServiceHealth(),
    ])

    systemInfo.value = sysInfo
    storageInfo.value = storage
    serviceHealth.value = health

    // Update API status
    apiStatus.value = {
      status: health.status,
      version: health.version,
      uptime: health.uptime,
    }

    // Update download clients status
    downloadClientsStatus.value = {
      status: health.downloadClients.status,
      connected: health.downloadClients.connected,
      total: health.downloadClients.total,
    }

    downloadClients.value = health.downloadClients.clients.map((client) => ({
      name: client.name,
      status: client.status,
    }))

    // Update external APIs status
    externalApis.value = {
      status: health.externalApis.status,
      connected: health.externalApis.connected,
      total: health.externalApis.total,
      apis: health.externalApis.apis.map((api) => ({
        name: api.name,
        status: api.status,
      })),
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load system data'
    logger.error('Error loading system data:', err)
  } finally {
    loading.value = false
  }
}

const formatLogTime = (timestamp: string): string => {
  const date = new Date(timestamp)
  return date.toLocaleTimeString()
}

const getLogIconComponent = (level: string) => {
  switch (level.toLowerCase()) {
    case 'info':
      return PhInfo
    case 'warning':
      return PhWarning
    case 'error':
      return PhXCircle
    case 'success':
      return PhCheckCircle
    default:
      return PhInfo
  }
}

const refreshStatus = async () => {
  logger.debug('Refreshing system status')
  await loadSystemData()
}

const viewFullLogs = () => {
  router.push('/logs')
}

const downloadLogs = () => {
  downloadLogsApi()
}

// SignalR state for backend connectivity indicator
const { isConnected } = useSignalR()

// Load data on mount
onMounted(() => {
  loadSystemData()
})
</script>

<style scoped>
.system-view {
  padding: 1rem;
  margin: 0 auto;
}

/* Page Header with Actions - For Right-Aligned Button */
.page-header-with-actions {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 2rem;
  margin-bottom: 2rem;
}

/* Header Styles - Matching Settings Page */
.settings-header {
  margin-bottom: 0;
}

.settings-header h1 {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 2rem;
  font-weight: 500;
}

.settings-header h1 i {
  font-size: 2rem;
  color: var(--brand-focus);
}

.settings-header p {
  color: #999;
  font-size: 1rem;
  margin: 0;
}

/* Page Actions */
.page-actions {
  margin-bottom: 2rem;
}

.add-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.2rem;
  background: var(--brand-focus);
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.add-button:hover:not(:disabled) {
  background: var(--brand-700);
  transform: translateY(-1px);
}

.add-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.add-button i {
  font-size: 1.1rem;
}

/* Error Message */
.error-message {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem;
  background: rgba(231, 76, 60, 0.1);
  border: 1px solid rgba(231, 76, 60, 0.3);
  border-radius: 6px;
  color: #e74c3c;
  margin-bottom: 2rem;
}

.error-message i {
  font-size: 1.5rem;
}

/* Loading State */
.loading-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1rem;
  padding: 3rem;
  color: #999;
}

.loading-state i {
  font-size: 2.5rem;
  color: var(--brand-focus);
}

/* Status Grid */
.system-status {
  margin-bottom: 2rem;
}

.status-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;
}

/* StatusCard styling is now defined in StatusCard.vue component */

.status-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1.25rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid #333;
}

.card-title {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.card-title i {
  font-size: 1.5rem;
  color: var(--brand-focus);
}

.card-title h3 {
  margin: 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.status-badge {
  padding: 0.35rem 0.85rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
  text-transform: capitalize;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.status-badge.healthy {
  background: rgba(39, 174, 96, 0.15);
  color: #27ae60;
  border: 1px solid rgba(39, 174, 96, 0.3);
}

.status-badge.warning {
  background: rgba(243, 156, 18, 0.15);
  color: #f39c12;
  border: 1px solid rgba(243, 156, 18, 0.3);
}

.status-badge.error,
.status-badge.unknown {
  background: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

/* Status Details */
.status-details {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.detail-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem;
  background: #252525;
  border-radius: 6px;
}

.detail-row i {
  font-size: 1.1rem;
  color: var(--brand-focus);
  flex-shrink: 0;
}

.detail-row .label {
  color: #999;
  font-size: 0.9rem;
  min-width: 60px;
}

.detail-row .value {
  color: #fff;
  font-weight: 500;
  flex: 1;
}

/* Client/API Status */
.client-status {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem;
  background: #252525;
  border-radius: 6px;
  margin-bottom: 0.5rem;
}

.client-status:last-child {
  margin-bottom: 0;
}

.client-status i {
  font-size: 1.2rem;
}

.client-status i.success {
  color: #27ae60;
}

.client-status i.error {
  color: #e74c3c;
}

.client-name {
  flex: 1;
  color: #fff;
  font-weight: 500;
}

.client-indicator {
  padding: 0.25rem 0.65rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: capitalize;
}

.client-indicator.connected {
  background: rgba(39, 174, 96, 0.15);
  color: #27ae60;
  border: 1px solid rgba(39, 174, 96, 0.3);
}

.client-indicator.disconnected,
.client-indicator.unknown {
  background: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

/* Empty Message */
.empty-message {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem;
  background: #252525;
  border-radius: 6px;
  color: #999;
  font-style: italic;
}

.empty-message i {
  font-size: 1.2rem;
  color: #868e96;
}

/* Storage Bar styling is now in ProgressBar.vue component */

/* System Sections */
.system-sections {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.section {
  background: #232323;
  border: 1px solid #333;
  border-radius: 8px;
  padding: 1.5rem;
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.25);
}

.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1.5rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid #333;
}

.section-header h2 {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0;
  color: #fff;
  font-size: 1.3rem;
  font-weight: 500;
}

.section-header h2 i {
  font-size: 1.5rem;
  color: var(--brand-focus);
}

.section-title-with-status {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.connection-status {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.35rem 0.85rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.connection-status.connected {
  background: rgba(39, 174, 96, 0.15);
  color: #27ae60;
  border: 1px solid rgba(39, 174, 96, 0.3);
}

.connection-status.disconnected {
  background: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

.status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.5;
  }
}

.section-actions {
  display: flex;
  gap: 0.5rem;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */

/* Info Grid */
.info-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 1rem;
}

/* InfoCard styling is now defined in InfoCard.vue component */

/* Logs Container */
.logs-container {
  max-height: 400px;
  overflow-y: auto;
  background: #252525;
  border: 1px solid #333;
  border-radius: 6px;
}

.log-entry {
  display: grid;
  grid-template-columns: auto auto auto 1fr;
  gap: 1rem;
  padding: 0.85rem 1rem;
  border-bottom: 1px solid #2a2a2a;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 0.85rem;
  align-items: center;
  transition: background 0.2s;
  /* allow children to shrink so the last column (message) can wrap instead of forcing horizontal scroll */
  min-width: 0;
}

.log-entry:hover {
  background: #2a2a2a;
}

.log-entry:last-child {
  border-bottom: none;
}

.log-entry.info {
  border-left: 3px solid var(--brand-focus);
}

.log-entry.warning {
  border-left: 3px solid #f39c12;
}

.log-entry.error {
  border-left: 3px solid #e74c3c;
}

.log-entry.success {
  border-left: 3px solid #27ae60;
}

.log-entry > i {
  font-size: 1.1rem;
}

/* Phosphor icon sizing for logs */
.log-icon svg {
  width: 18px;
  height: 18px;
}

.log-entry.info > i {
  color: var(--brand-500);
}

.log-entry.info .log-icon svg {
  color: var(--brand-500);
}

.log-entry.warning > i {
  color: #f39c12;
}

.log-entry.warning .log-icon svg {
  color: #f39c12;
}

.log-entry.error > i {
  color: #e74c3c;
}

.log-entry.error .log-icon svg {
  color: #e74c3c;
}

.log-entry.success > i {
  color: #27ae60;
}

.log-entry.success .log-icon svg {
  color: #27ae60;
}

.log-time {
  color: #666;
  font-size: 0.8rem;
}

.log-level {
  font-weight: 500;
  font-size: 0.75rem;
  padding: 0.25rem 0.5rem;
  border-radius: 6px;
  letter-spacing: 0.5px;
}

.log-entry.info .log-level {
  background: rgba(var(--brand-rgb), 0.15);
  color: var(--brand-500);
}

.log-entry.warning .log-level {
  background: rgba(243, 156, 18, 0.15);
  color: #f39c12;
}

.log-entry.error .log-level {
  background: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
}

.log-entry.success .log-level {
  background: rgba(39, 174, 96, 0.15);
  color: #27ae60;
}

.log-message {
  color: #ccc;
  /* Prevent long unbroken strings from causing horizontal scroll */
  min-width: 0;
  overflow-wrap: anywhere;
  word-break: break-word;
  white-space: pre-wrap;
}

/* Scrollbar Styles */
.logs-container::-webkit-scrollbar {
  width: 8px;
}

.logs-container::-webkit-scrollbar-track {
  background: #1e1e1e;
  border-radius: 6px;
}

.logs-container::-webkit-scrollbar-thumb {
  background: #444;
  border-radius: 6px;
}

.logs-container::-webkit-scrollbar-thumb:hover {
  background: #555;
}

/* Animations */
.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */

/* Responsive */
@media (max-width: 768px) {
  .system-view {
    padding: 1rem;
  }

  .status-grid {
    grid-template-columns: 1fr;
  }

  .info-grid {
    grid-template-columns: 1fr;
  }

  .log-entry {
    grid-template-columns: 1fr;
    gap: 0.5rem;
  }

  /* Make header actions stack and buttons fill width on small screens */
  .page-header-with-actions {
    flex-direction: column;
    align-items: stretch;
    gap: 1rem;
  }

  .page-actions {
    width: 100%;
  }

  .add-button {
    width: 100%;
    justify-content: center;
  }
}
</style>
