/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

export type BulkPathChangeMode = 'None' | 'MetadataOnly' | 'Physical'
export type BulkPathChangeOutcome =
  | 'none'
  | 'metadata-updated'
  | 'enqueued'
  | 'not-enqueued'
  | 'failed'

export interface BulkPathChangeRequest {
  mode: BulkPathChangeMode
  destinationRootOrPath?: string | null
  deleteEmptySource: boolean
}

export interface BulkEditItemResult {
  id: number
  success: boolean
  metadataUpdated?: boolean
  pathChangeOutcome: BulkPathChangeOutcome | null
  moveJobId?: string | null
  resolvedDestination?: string | null
  errors: string[]
}

export interface BulkEditApiItemResult extends Omit<
  BulkEditItemResult,
  'pathChangeOutcome' | 'errors'
> {
  pathChangeOutcome?: unknown
  errors?: string[]
}

export interface BulkEditOrchestrationRequest {
  ids: number[]
  updates: Record<string, boolean | number | string>
  destinationRoot?: string | null
  moveFiles: boolean
  deleteEmptySource: boolean
}

export interface BulkEditOrchestrationDependencies {
  bulkUpdateAudiobooks(
    ids: number[],
    updates: Record<string, boolean | number | string>,
    pathChange?: BulkPathChangeRequest,
  ): Promise<{ message: string; results: BulkEditApiItemResult[] }>
  trackQueuedJob(job: { jobId: string; audiobookId: number; target: string }): void
}

export async function executeBulkEdit(
  request: BulkEditOrchestrationRequest,
  dependencies: BulkEditOrchestrationDependencies,
): Promise<{ results: BulkEditItemResult[] }> {
  const metadataUpdates = { ...request.updates }
  delete metadataUpdates.moveFiles
  delete metadataUpdates.deleteEmptySource

  const requestedRoot = nonBlankString(request.destinationRoot)
  const rootChangeRequested = requestedRoot !== null || 'rootFolder' in metadataUpdates
  const destinationRoot = requestedRoot ?? stringValue(metadataUpdates.rootFolder)
  delete metadataUpdates.rootFolder

  let pathChange: BulkPathChangeRequest | undefined
  if (rootChangeRequested) {
    pathChange = {
      mode: request.moveFiles ? 'Physical' : 'MetadataOnly',
      destinationRootOrPath: destinationRoot,
      deleteEmptySource: request.deleteEmptySource,
    }
  }

  const response = await dependencies.bulkUpdateAudiobooks(request.ids, metadataUpdates, pathChange)
  const resultsById = new Map<number, BulkEditItemResult>(
    response.results.map((result) => {
      const errors = [...(result.errors ?? [])]
      const pathChangeOutcome = parsePathChangeOutcome(result.pathChangeOutcome)
      let success = result.success
      if (pathChangeOutcome == null) {
        success = false
        errors.push(
          result.pathChangeOutcome == null
            ? 'The server did not return a path-change outcome.'
            : 'The server returned an unrecognized path-change outcome.',
        )
      } else if (success && !isSuccessfulOutcomeForMode(pathChangeOutcome, pathChange?.mode)) {
        success = false
        errors.push('The server returned a path-change outcome that does not match the request.')
      }

      return [
        result.id,
        {
          ...result,
          success,
          pathChangeOutcome,
          errors,
        },
      ]
    }),
  )

  for (const id of request.ids) {
    const result = resultsById.get(id) ?? {
      id,
      success: false,
      pathChangeOutcome: null,
      errors: ['The bulk update returned no result for this audiobook.'],
    }
    resultsById.set(id, result)

    if (!request.moveFiles || !result.success) continue
    if (result.pathChangeOutcome !== 'enqueued') {
      result.success = false
      result.errors.push('The server did not confirm that a physical move was enqueued.')
      continue
    }

    const jobId = result.moveJobId?.trim()
    const target = nonBlankString(result.resolvedDestination)
    if (!jobId || !target) {
      result.success = false
      result.errors.push(
        !jobId
          ? 'The server did not return a durable move job ID.'
          : 'The server did not return the resolved move destination.',
      )
      continue
    }

    dependencies.trackQueuedJob({
      jobId,
      audiobookId: id,
      target,
    })
  }

  return { results: request.ids.map((id) => resultsById.get(id)!) }
}

function stringValue(value: boolean | number | string | undefined): string | null {
  return nonBlankString(typeof value === 'string' ? value : null)
}

function nonBlankString(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null
}

function parsePathChangeOutcome(value: unknown): BulkPathChangeOutcome | null {
  switch (value) {
    case 'none':
    case 'metadata-updated':
    case 'enqueued':
    case 'not-enqueued':
    case 'failed':
      return value
    default:
      return null
  }
}

function isSuccessfulOutcomeForMode(
  outcome: BulkPathChangeOutcome,
  mode: BulkPathChangeMode | undefined,
): boolean {
  switch (mode) {
    case 'Physical':
      return outcome === 'enqueued'
    case 'MetadataOnly':
      return outcome === 'metadata-updated'
    case 'None':
    case undefined:
      return outcome === 'none'
  }
}
