<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  Licensed under the GNU Affero General Public License v3 or later.
-->
<template>
  <div class="tab-content">
    <div class="plugins-tab">
      <div class="section-header">
        <h3>Plugins</h3>
        <button class="btn btn-secondary" :disabled="loading" @click="load">
          <PhArrowClockwise :class="{ spin: loading }" />
          Refresh
        </button>
      </div>

      <p class="warn">
        Plugins run with full server access. Only add repositories you trust — installing from a URL
        downloads and runs code, the same trust model as *arr custom repositories.
      </p>

      <!-- Repositories -->
      <div class="card">
        <div class="card-title">Repositories</div>
        <div v-if="repositories.length === 0" class="muted">No repositories added yet.</div>
        <div v-for="url in repositories" :key="url" class="row">
          <span class="repo-url" :title="url">{{ url }}</span>
          <button class="btn-icon danger" title="Remove repository" @click="removeRepo(url)">
            <PhTrash />
          </button>
        </div>
        <div class="add-repo">
          <input
            v-model="newRepoUrl"
            type="url"
            placeholder="https://example.com/listenarr-plugins.json"
            @keydown.enter="addRepo"
          />
          <button class="btn btn-primary" :disabled="!newRepoUrl.trim() || busy" @click="addRepo">
            <PhPlus />
            Add
          </button>
        </div>
        <div v-if="repoError" class="error">{{ repoError }}</div>
      </div>

      <!-- Available / updatable -->
      <div class="card">
        <div class="card-title">Available</div>
        <div v-if="available.length === 0" class="muted">
          Nothing to install. Add a repository above.
        </div>
        <div v-for="p in available" :key="p.id" class="row plugin">
          <div class="plugin-meta">
            <div class="plugin-name">
              {{ p.name }} <span class="ver">v{{ p.availableVersion ?? p.version }}</span>
              <span v-if="p.status === 'update'" class="badge update">update</span>
            </div>
            <div v-if="p.description" class="plugin-desc">{{ p.description }}</div>
          </div>
          <button class="btn btn-primary" :disabled="busy" @click="install(p.id)">
            {{ p.status === 'update' ? 'Update' : 'Install' }}
          </button>
        </div>
      </div>

      <!-- Installed -->
      <div class="card">
        <div class="card-title">Installed</div>
        <div v-if="installed.length === 0" class="muted">No plugins installed.</div>
        <div v-for="p in installed" :key="p.id" class="row plugin">
          <div class="plugin-meta">
            <div class="plugin-name">{{ p.name }} <span class="ver">v{{ p.version }}</span></div>
            <div v-if="p.description" class="plugin-desc">{{ p.description }}</div>
          </div>
          <button class="btn btn-danger" :disabled="busy" @click="uninstall(p.id)">Remove</button>
        </div>
      </div>

      <div v-if="error" class="error">{{ error }}</div>
    </div>

    <!-- Restart overlay -->
    <div v-if="restarting" class="restart-overlay">
      <div class="restart-box">
        <PhArrowClockwise class="spin big" />
        <div>Restarting to apply…</div>
        <div class="muted small">Reconnecting</div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { PhArrowClockwise, PhPlus, PhTrash } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'

interface PluginInfo {
  id: string
  name: string
  version: string
  description?: string | null
  author?: string | null
  status: 'installed' | 'available' | 'update'
  availableVersion?: string | null
  repository?: string | null
}

const plugins = ref<PluginInfo[]>([])
const repositories = ref<string[]>([])
const newRepoUrl = ref('')
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const repoError = ref('')
const restarting = ref(false)

const available = computed(() => plugins.value.filter((p) => p.status !== 'installed'))
const installed = computed(() => plugins.value.filter((p) => p.status !== 'available'))

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    repositories.value = await apiService.pluginRequest<string[]>('/plugins/repositories')
    plugins.value = await apiService.pluginRequest<PluginInfo[]>('/plugins')
  } catch (e) {
    error.value = (e as Error).message || 'Failed to load plugins.'
  } finally {
    loading.value = false
  }
}

async function addRepo(): Promise<void> {
  const url = newRepoUrl.value.trim()
  if (!url) return
  busy.value = true
  repoError.value = ''
  try {
    await apiService.ensureAntiforgeryForCurrentAuth()
    repositories.value = await apiService.pluginRequest<string[]>('/plugins/repositories', {
      method: 'POST',
      body: JSON.stringify({ url }),
    })
    newRepoUrl.value = ''
    await load()
  } catch (e) {
    repoError.value = (e as Error).message || 'Could not add repository.'
  } finally {
    busy.value = false
  }
}

async function removeRepo(url: string): Promise<void> {
  busy.value = true
  try {
    await apiService.ensureAntiforgeryForCurrentAuth()
    repositories.value = await apiService.pluginRequest<string[]>(
      `/plugins/repositories?url=${encodeURIComponent(url)}`,
      { method: 'DELETE' },
    )
    await load()
  } catch (e) {
    repoError.value = (e as Error).message || 'Could not remove repository.'
  } finally {
    busy.value = false
  }
}

async function install(id: string): Promise<void> {
  busy.value = true
  error.value = ''
  try {
    await apiService.ensureAntiforgeryForCurrentAuth()
    await apiService.pluginRequest('/plugins/install', {
      method: 'POST',
      body: JSON.stringify({ id }),
    })
    waitForRestart()
  } catch (e) {
    error.value = (e as Error).message || 'Install failed.'
    busy.value = false
  }
}

async function uninstall(id: string): Promise<void> {
  busy.value = true
  error.value = ''
  try {
    await apiService.ensureAntiforgeryForCurrentAuth()
    await apiService.pluginRequest(`/plugins/${encodeURIComponent(id)}`, { method: 'DELETE' })
    waitForRestart()
  } catch (e) {
    error.value = (e as Error).message || 'Uninstall failed.'
    busy.value = false
  }
}

// The server restarts after install/uninstall. Poll the (anonymous) plugin manifest until it
// answers again, then hard-reload so the new plugin set is picked up everywhere.
function waitForRestart(): void {
  restarting.value = true
  let tries = 0
  const tick = async (): Promise<void> => {
    tries++
    try {
      // A small delay first so we don't catch the still-up old process.
      const resp = await fetch('/plugins/manifest', { cache: 'no-store' })
      if (resp.ok && tries > 2) {
        window.location.reload()
        return
      }
    } catch {
      // server still down — keep waiting
    }
    if (tries < 60) {
      setTimeout(tick, 1000)
    } else {
      restarting.value = false
      error.value = 'Server did not come back automatically — reload the page manually.'
    }
  }
  setTimeout(tick, 1500)
}

onMounted(load)
</script>

<style scoped>
.plugins-tab {
  max-width: 820px;
}
/* Core .btn sets align-items but not justify-content, so text left-aligns when a
   button is wider than its label. Center it. */
.btn {
  justify-content: center;
}
.section-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 0.5rem;
}
.warn {
  color: #f0ad4e;
  font-size: 13px;
  margin: 0 0 1rem;
}
.card {
  background: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 8px;
  padding: 16px;
  margin-bottom: 16px;
}
.card-title {
  font-weight: 600;
  margin-bottom: 12px;
  color: #fff;
}
.muted {
  color: #9aa4b2;
  font-size: 13px;
}
.small {
  font-size: 12px;
}
.row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 0;
  border-top: 1px solid #333;
}
.row:first-of-type {
  border-top: none;
}
.repo-url {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  color: #ddd;
}
.add-repo {
  display: flex;
  gap: 8px;
  margin-top: 12px;
}
.add-repo input {
  flex: 1;
  padding: 8px 10px;
  background: #1f1f1f;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
}
.plugin-meta {
  flex: 1;
}
.plugin-name {
  color: #fff;
  font-weight: 500;
}
.plugin-desc {
  color: #9aa4b2;
  font-size: 12px;
  margin-top: 2px;
}
.ver {
  color: #9aa4b2;
  font-weight: 400;
  font-size: 12px;
}
.badge.update {
  margin-left: 6px;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 11px;
  background: rgba(243, 156, 18, 0.2);
  color: #f39c12;
  border: 1px solid rgba(243, 156, 18, 0.4);
}
.btn-icon.danger {
  background: none;
  border: none;
  color: #e74c3c;
  cursor: pointer;
  display: inline-flex;
  padding: 4px;
}
.btn-icon.danger:hover {
  color: #c0392b;
}
.error {
  color: #e74c3c;
  font-size: 13px;
  margin-top: 8px;
}
.spin {
  animation: lp-spin 1s linear infinite;
}
.spin.big {
  font-size: 32px;
}
@keyframes lp-spin {
  to {
    transform: rotate(360deg);
  }
}
.restart-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 9999;
}
.restart-box {
  background: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 10px;
  padding: 28px 40px;
  text-align: center;
  color: #fff;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
}
</style>
