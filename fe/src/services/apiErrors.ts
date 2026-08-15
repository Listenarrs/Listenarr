export interface ApiValidationErrorPayload {
  code?: string
  field?: string
  message: string
  resolvedDestination?: string | null
  jobId?: string
  status?: string
  requestedPath?: string
  recoveryDisposition?: string
  canRetry?: boolean
}

type ApiErrorWithBody = Error & {
  status?: number
  body?: string
}

export function getApiValidationError(
  error: unknown,
  expectedField?: string,
): ApiValidationErrorPayload | null {
  if (!(error instanceof Error)) return null

  const candidate = error as ApiErrorWithBody
  if (!candidate.body) return null

  try {
    const payload = JSON.parse(candidate.body) as Partial<ApiValidationErrorPayload> & {
      detail?: unknown
    }
    const message =
      typeof payload.message === 'string' && payload.message.trim().length > 0
        ? payload.message
        : typeof payload.detail === 'string' && payload.detail.trim().length > 0
          ? payload.detail
          : null
    if (message == null) {
      return null
    }
    if (expectedField && payload.field !== expectedField) return null

    return {
      code: typeof payload.code === 'string' ? payload.code : undefined,
      field: typeof payload.field === 'string' ? payload.field : undefined,
      message,
      resolvedDestination:
        typeof payload.resolvedDestination === 'string' || payload.resolvedDestination === null
          ? payload.resolvedDestination
          : undefined,
      jobId: typeof payload.jobId === 'string' ? payload.jobId : undefined,
      status: typeof payload.status === 'string' ? payload.status : undefined,
      requestedPath: typeof payload.requestedPath === 'string' ? payload.requestedPath : undefined,
      recoveryDisposition:
        typeof payload.recoveryDisposition === 'string' ? payload.recoveryDisposition : undefined,
      canRetry: typeof payload.canRetry === 'boolean' ? payload.canRetry : undefined,
    }
  } catch {
    return null
  }
}
