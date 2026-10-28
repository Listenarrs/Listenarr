import { defineStore } from 'pinia'
import { ref } from 'vue'

export interface ScanNotificationUpdate {
  jobId: string
  audiobookId?: number | null
  status: string
  found?: number
  created?: number
  error?: string
}

export interface TrackedScanNotification extends ScanNotificationUpdate {
  timestamp: string
  visible: boolean
  dismissed?: boolean
}

function statusRank(status: string): number {
  const normalized = status.toLowerCase()
  if (normalized === 'queued') return 0
  if (normalized === 'processing') return 1
  return 2
}

function isActive(status: string): boolean {
  const normalized = status.toLowerCase()
  return normalized === 'queued' || normalized === 'processing'
}

export const useScanNotificationsStore = defineStore('scanNotifications', () => {
  const jobs = ref<TrackedScanNotification[]>([])

  function find(jobId: string): TrackedScanNotification | undefined {
    return jobs.value.find((job) => job.jobId === jobId)
  }

  function trim(): void {
    if (jobs.value.length <= 50) return

    const next = [...jobs.value]
    for (let index = next.length - 1; index >= 0 && next.length > 50; index -= 1) {
      const candidate = next[index]
      if (candidate && (!candidate.visible || candidate.dismissed || !isActive(candidate.status))) {
        next.splice(index, 1)
      }
    }

    jobs.value = next
  }

  function applyUpdate(update: ScanNotificationUpdate): void {
    const existing = find(update.jobId)
    if (!existing) {
      jobs.value = [
        {
          ...update,
          timestamp: new Date().toISOString(),
          visible: update.status.toLowerCase() === 'queued',
          dismissed: false,
        },
        ...jobs.value,
      ]
      trim()
      return
    }

    const existingStatus = existing.status.toLowerCase()
    const updateStatus = update.status.toLowerCase()
    const conflictingTerminalStates =
      !isActive(existing.status) && !isActive(update.status) && existingStatus !== updateStatus

    existing.audiobookId = update.audiobookId ?? existing.audiobookId
    if (conflictingTerminalStates) {
      return
    }

    const preserveExistingStatus = statusRank(existing.status) > statusRank(update.status)
    existing.status = preserveExistingStatus ? existing.status : update.status
    existing.found = update.found ?? existing.found
    existing.created = update.created ?? existing.created
    existing.error = update.error ?? existing.error
    existing.visible = existing.visible || updateStatus === 'queued'
  }

  function registerManualScan(jobId: string, audiobookId: number): void {
    const existing = find(jobId)
    if (existing) {
      existing.audiobookId = audiobookId
      existing.visible = true
      return
    }

    jobs.value = [
      {
        jobId,
        audiobookId,
        status: 'Queued',
        timestamp: new Date().toISOString(),
        visible: true,
        dismissed: false,
      },
      ...jobs.value,
    ]
    trim()
  }

  function dismiss(jobId: string): void {
    const job = find(jobId)
    if (!job || isActive(job.status)) return
    job.dismissed = true
  }

  function clearFinished(): void {
    jobs.value = jobs.value.filter((job) => isActive(job.status))
  }

  return {
    jobs,
    applyUpdate,
    registerManualScan,
    dismiss,
    clearFinished,
  }
})
