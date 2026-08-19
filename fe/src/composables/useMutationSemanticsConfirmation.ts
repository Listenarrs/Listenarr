/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { showConfirm } from '@/composables/useConfirm'
import { apiService } from '@/services/api'
import { getApiValidationError } from '@/services/apiErrors'
import { useToast } from '@/services/toastService'
import { useRootFoldersStore } from '@/stores/rootFolders'
import type { RootFolder } from '@/types'
import { detectPathKind, pathIsInside, pathsEqual, type PathKind } from '@/utils/path'

export type ConfirmedCaseSensitivity = 'Sensitive' | 'Insensitive'
export type MutationSemanticsConfirmationOutcome = 'retry' | 'cancelled' | 'not-applicable'

const MUTATION_UNAVAILABLE_CODES = new Set([
  'destination_filesystem_mutation_unavailable',
  'source_filesystem_mutation_unavailable',
  'filesystem_mutation_unavailable',
])

export function detectedMutationSemantics(root: RootFolder): ConfirmedCaseSensitivity | null {
  return root.resolvedCaseSensitivity === 'Sensitive' ||
    root.resolvedCaseSensitivity === 'Insensitive'
    ? root.resolvedCaseSensitivity
    : null
}

export function needsMutationSemanticsConfirmation(root: RootFolder): boolean {
  return (
    (root.caseSensitivityMode ?? 'Auto') === 'Auto' &&
    root.storageReason === 'MutationSemanticsUnproven' &&
    detectedMutationSemantics(root) !== null
  )
}

export function caseSensitivityLabel(mode: ConfirmedCaseSensitivity): string {
  return mode === 'Sensitive' ? 'case-sensitive' : 'case-insensitive'
}

function rootPathKind(root: RootFolder): PathKind {
  if (root.pathSyntax === 'Windows') return 'windows'
  if (root.pathSyntax === 'Unix') return 'unix'
  return detectPathKind(root.path)
}

function rootMatchesPath(root: RootFolder, path: string): boolean {
  const kind = rootPathKind(root)
  const sensitivity = root.resolvedCaseSensitivity ?? 'Unknown'
  return (
    pathsEqual(path, root.path, kind, sensitivity) ||
    pathIsInside(path, root.path, kind, sensitivity)
  )
}

export function findMutationSemanticsRoot(
  roots: RootFolder[],
  path?: string | null,
): RootFolder | null {
  const candidates = roots.filter(needsMutationSemanticsConfirmation)
  if (candidates.length === 0) return null

  if (!path) return null

  const matching = candidates.filter((root) => rootMatchesPath(root, path))
  if (matching.length === 0) return null
  return matching.sort((left, right) => right.path.length - left.path.length)[0] ?? null
}

export async function applyDetectedMutationSemantics(root: RootFolder): Promise<boolean> {
  if (!root.id || root.activeRelocation) return false

  const store = useRootFoldersStore()
  const toast = useToast()

  try {
    let current = store.folders.find((candidate) => candidate.id === root.id)
    if (!current) {
      await store.load()
      current = store.folders.find((candidate) => candidate.id === root.id)
    }
    if (!current) {
      toast.error('Storage setting not saved', 'The root folder changed or was removed.')
      return false
    }
    if (current.activeRelocation) {
      toast.warning('Storage setting not saved', 'Finish the active root folder change first.')
      return false
    }
    if (
      current.caseSensitivityMode !== 'Auto' &&
      current.storageReason !== 'MutationSemanticsUnproven' &&
      current.canMutateFilesystem !== false
    ) {
      return true
    }

    const detected = detectedMutationSemantics(current)
    if (!detected || !needsMutationSemanticsConfirmation(current)) {
      toast.warning(
        'Storage setting not saved',
        'Listenarr can no longer confirm a detected case setting for this root. Refresh and review its storage status.',
      )
      return false
    }

    const updated = await store.update(
      current.id,
      {
        id: current.id,
        name: current.name,
        path: current.path,
        isDefault: current.isDefault,
        caseSensitivityMode: detected,
      },
      { expectedCurrentPath: current.path },
    )

    if (
      updated.storageReason === 'MutationSemanticsUnproven' ||
      updated.canMutateFilesystem === false
    ) {
      toast.warning(
        'Storage setting saved',
        'The setting was saved, but this storage still does not authorize filesystem mutations.',
      )
      return false
    }

    toast.success(
      'Storage behavior confirmed',
      `File operations are enabled using ${caseSensitivityLabel(detected)} behavior.`,
    )
    return true
  } catch (error: unknown) {
    toast.error(
      'Storage setting not saved',
      error instanceof Error ? error.message : 'Failed to save the detected storage behavior.',
    )
    return false
  }
}

export async function refreshAudiobookFileIdentity(audiobookId: number): Promise<void> {
  const scan = await apiService.scanAudiobook(audiobookId)
  const jobId = scan.jobId?.trim()
  if (!jobId) return

  for (let attempt = 0; attempt < 120; attempt += 1) {
    const status = await apiService.getScanJobStatus(jobId)
    if (status.status === 'Completed') return
    if (status.status === 'Failed') {
      throw new Error(status.error || 'Listenarr could not refresh the audiobook file identity.')
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }

  throw new Error('Timed out while refreshing the audiobook file identity.')
}

export async function confirmDetectedMutationSemantics(
  root: RootFolder,
  operationLabel: string,
): Promise<MutationSemanticsConfirmationOutcome> {
  const detected = detectedMutationSemantics(root)
  if (!detected || !needsMutationSemanticsConfirmation(root)) return 'not-applicable'

  const label = caseSensitivityLabel(detected)
  const confirmed = await showConfirm(
    `Listenarr detected that “${root.name}” behaves as ${label}, but this storage cannot report that reliably enough for file operations while Automatic is selected. Confirm the detected setting to enable file moves and deletes for this root, then Listenarr will continue ${operationLabel}.`,
    'Confirm storage behavior',
    {
      confirmText: `Use ${label} & continue`,
      cancelText: 'Cancel',
    },
  )
  if (!confirmed) return 'cancelled'

  return (await applyDetectedMutationSemantics(root)) ? 'retry' : 'cancelled'
}

async function refreshDeleteIdentityOrDeferToRecovery(audiobookId: number): Promise<void> {
  try {
    await refreshAudiobookFileIdentity(audiobookId)
  } catch (error: unknown) {
    if (getApiValidationError(error)?.code === 'delete_recovery_pending') {
      return
    }
    throw error
  }
}

export async function preparePhysicalDeleteRetry(
  error: unknown,
  audiobookId: number,
  path?: string | null,
): Promise<boolean | 'cancel'> {
  const validationError = getApiValidationError(error)
  if (validationError?.code === 'delete_source_unverified') {
    await refreshDeleteIdentityOrDeferToRecovery(audiobookId)
    return true
  }

  const confirmation = await confirmMutationSemanticsForBlockedOperation(error, {
    path,
    operationLabel: 'the delete',
  })
  if (confirmation === 'cancelled') return 'cancel'
  if (confirmation !== 'retry') return false

  await refreshDeleteIdentityOrDeferToRecovery(audiobookId)
  return true
}

export async function confirmMutationSemanticsForBlockedOperation(
  error: unknown,
  options: {
    path?: string | null
    operationLabel: string
  },
): Promise<MutationSemanticsConfirmationOutcome> {
  const validationError = getApiValidationError(error)
  if (!validationError?.code || !MUTATION_UNAVAILABLE_CODES.has(validationError.code)) {
    return 'not-applicable'
  }

  const store = useRootFoldersStore()
  try {
    await store.load()
  } catch {
    return 'not-applicable'
  }
  const root = findMutationSemanticsRoot(store.folders, options.path)
  if (!root) return 'not-applicable'

  return confirmDetectedMutationSemantics(root, options.operationLabel)
}
