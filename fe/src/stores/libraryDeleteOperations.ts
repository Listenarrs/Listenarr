import { defineStore } from 'pinia'
import { ref } from 'vue'

export type LibraryDeleteOperationStatus = 'deleting' | 'completed' | 'failed'
export type LibraryDeleteOperationKind = 'single' | 'bulk'

export interface LibraryDeleteOperation {
  id: string
  kind: LibraryDeleteOperationKind
  title: string
  audiobookId?: number
  status: LibraryDeleteOperationStatus
  progress: number
  total: number
  processed: number
  deleted: number
  failed: number
  currentTitle?: string
  startedAt: string
  error?: string
  dismissed?: boolean
}

let sequence = 0

function nextOperationId(): string {
  sequence += 1
  return `library-delete-${Date.now()}-${sequence}`
}

function clampProgress(value: number): number {
  return Math.min(100, Math.max(0, value))
}

export const useLibraryDeleteOperationsStore = defineStore('libraryDeleteOperations', () => {
  const operations = ref<LibraryDeleteOperation[]>([])

  function trim(): void {
    if (operations.value.length <= 50) return

    const next = [...operations.value]
    for (let index = next.length - 1; index >= 0 && next.length > 50; index -= 1) {
      const candidate = next[index]
      if (candidate && (candidate.dismissed || candidate.status !== 'deleting')) {
        next.splice(index, 1)
      }
    }

    operations.value = next
  }

  function prepend(operation: LibraryDeleteOperation): string {
    operations.value = [operation, ...operations.value]
    trim()
    return operation.id
  }

  function beginSingle(audiobookId: number, title: string): string {
    return prepend({
      id: nextOperationId(),
      kind: 'single',
      title: title || 'Audiobook',
      audiobookId,
      status: 'deleting',
      // A single synchronous delete has no trustworthy byte/item denominator.
      // Keep a visible animated bar without presenting a fake percentage.
      progress: 35,
      total: 1,
      processed: 0,
      deleted: 0,
      failed: 0,
      startedAt: new Date().toISOString(),
      dismissed: false,
    })
  }

  function beginBulk(total: number): string {
    return prepend({
      id: nextOperationId(),
      kind: 'bulk',
      title: `Deleting ${total} audiobook${total === 1 ? '' : 's'}`,
      status: 'deleting',
      progress: 0,
      total,
      processed: 0,
      deleted: 0,
      failed: 0,
      startedAt: new Date().toISOString(),
      dismissed: false,
    })
  }

  function find(operationId: string): LibraryDeleteOperation | undefined {
    return operations.value.find((operation) => operation.id === operationId)
  }

  function completeSingle(operationId: string): void {
    const operation = find(operationId)
    if (!operation) return

    operation.processed = 1
    operation.deleted = 1
    operation.progress = 100
    operation.status = 'completed'
  }

  function failSingle(operationId: string, error?: string): void {
    const operation = find(operationId)
    if (!operation) return

    operation.processed = 1
    operation.failed = 1
    operation.status = 'failed'
    operation.error = error
  }

  function setBulkCurrentItem(operationId: string, currentTitle: string): void {
    const operation = find(operationId)
    if (!operation || operation.kind !== 'bulk' || operation.status !== 'deleting') return

    operation.currentTitle = currentTitle
  }

  function updateBulkItem(
    operationId: string,
    currentTitle: string,
    deleted: boolean,
    error?: string,
  ): void {
    const operation = find(operationId)
    if (!operation || operation.kind !== 'bulk' || operation.status !== 'deleting') return

    operation.currentTitle = currentTitle
    operation.processed += 1
    if (deleted) {
      operation.deleted += 1
    } else {
      operation.failed += 1
      if (error) operation.error = error
    }
    operation.progress = clampProgress(
      operation.total > 0 ? (operation.processed / operation.total) * 100 : 100,
    )
  }

  function finishBulk(operationId: string): void {
    const operation = find(operationId)
    if (!operation || operation.kind !== 'bulk') return

    operation.progress = operation.processed >= operation.total ? 100 : operation.progress
    operation.status =
      operation.failed > 0 || operation.processed < operation.total ? 'failed' : 'completed'
  }

  function dismiss(operationId: string): void {
    const operation = find(operationId)
    if (!operation || operation.status === 'deleting') return
    operation.dismissed = true
  }

  function clearFinished(): void {
    operations.value = operations.value.filter((operation) => operation.status === 'deleting')
  }

  return {
    operations,
    beginSingle,
    beginBulk,
    completeSingle,
    failSingle,
    setBulkCurrentItem,
    updateBulkItem,
    finishBulk,
    dismiss,
    clearFinished,
  }
})
