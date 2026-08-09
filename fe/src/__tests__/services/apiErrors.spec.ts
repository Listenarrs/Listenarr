import { describe, expect, it } from 'vitest'
import { getApiValidationError } from '@/services/apiErrors'

describe('getApiValidationError', () => {
  it('returns a matching structured field error and preserves the resolved destination', () => {
    const error = Object.assign(new Error('API error'), {
      status: 400,
      body: JSON.stringify({
        code: 'destination_path_outside_roots',
        field: 'destinationPath',
        message: 'DestinationPath must be inside a configured root folder or output path',
        resolvedDestination: '/outside/Author/Title',
      }),
    })

    expect(getApiValidationError(error, 'destinationPath')).toEqual({
      code: 'destination_path_outside_roots',
      field: 'destinationPath',
      message: 'DestinationPath must be inside a configured root folder or output path',
      resolvedDestination: '/outside/Author/Title',
    })
  })

  it('uses RFC problem-details detail for filesystem initialization failures', () => {
    const error = Object.assign(new Error('API error'), {
      status: 503,
      body: JSON.stringify({
        title: 'Service unavailable',
        status: 503,
        code: 'filesystem_initializing',
        detail: 'Library filesystem initialization is still in progress.',
      }),
    })

    expect(getApiValidationError(error)).toEqual({
      code: 'filesystem_initializing',
      field: undefined,
      message: 'Library filesystem initialization is still in progress.',
      resolvedDestination: undefined,
      jobId: undefined,
      status: undefined,
      requestedPath: undefined,
      recoveryDisposition: undefined,
      canRetry: undefined,
    })
  })

  it('does not return an error for another field', () => {
    const error = Object.assign(new Error('API error'), {
      body: JSON.stringify({
        field: 'title',
        message: 'Title is invalid',
      }),
    })

    expect(getApiValidationError(error, 'destinationPath')).toBeNull()
  })

  it.each(['not-json', '{}', '{"message":""}'])(
    'fails closed for an unusable response body: %s',
    (body) => {
      expect(getApiValidationError(Object.assign(new Error('API error'), { body }))).toBeNull()
    },
  )
})
