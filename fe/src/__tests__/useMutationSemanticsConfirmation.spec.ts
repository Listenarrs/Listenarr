import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { apiService } from '@/services/api'
import type { RootFolder } from '@/types'
import {
  confirmMutationSemanticsForBlockedOperation,
  findMutationSemanticsRoot,
  preparePhysicalDeleteRetry,
} from '@/composables/useMutationSemanticsConfirmation'

const confirmMock = vi.hoisted(() => vi.fn())
const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  warning: vi.fn(),
  error: vi.fn(),
}))

vi.mock('@/composables/useConfirm', () => ({
  showConfirm: confirmMock,
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => toastMocks,
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getRootFolders: vi.fn(),
    changeRootFolderPath: vi.fn(),
    updateRootFolder: vi.fn(),
    scanAudiobook: vi.fn(),
    getScanJobStatus: vi.fn(),
  },
}))

function root(id: number, path: string, name = `Root ${id}`): RootFolder {
  return {
    id,
    name,
    path,
    pathSyntax: 'Unix',
    isDefault: id === 1,
    caseSensitivityMode: 'Auto',
    resolvedCaseSensitivity: 'Sensitive',
    pathIdentityState: 'Valid',
    storageState: 'Limited',
    storageReason: 'MutationSemanticsUnproven',
    canChangePath: true,
    canReadFilesystem: true,
    canScanFilesystem: true,
    canMutateFilesystem: false,
  }
}

describe('mutation semantics confirmation', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
    vi.mocked(apiService.scanAudiobook).mockResolvedValue({
      message: 'Scan enqueued',
      found: 1,
      created: 0,
      jobId: 'scan-1',
    })
    vi.mocked(apiService.getScanJobStatus).mockResolvedValue({
      id: 'scan-1',
      audiobookId: 42,
      status: 'Completed',
      enqueuedAt: '2026-08-19T00:00:00Z',
      canRequeue: true,
    })
  })

  it('chooses the most-specific mutation-limited root for an affected path', () => {
    const roots = [root(1, '/library'), root(2, '/library/audiobooks'), root(3, '/other')]

    expect(findMutationSemanticsRoot(roots, '/library/audiobooks/Author/Book')?.id).toBe(2)
  })

  it('treats an omitted case mode as the Automatic default for remediation', () => {
    const legacyPayload = root(1, '/library')
    delete legacyPayload.caseSensitivityMode

    expect(findMutationSemanticsRoot([legacyPayload], '/library/Author/Book')?.id).toBe(1)
  })

  it('does not guess a root when the blocked operation has no affected path', () => {
    expect(findMutationSemanticsRoot([root(1, '/library')], null)).toBeNull()
  })

  it('confirms the detected setting, persists it, and allows the blocked operation to retry', async () => {
    const other = root(1, '/other', 'Other')
    const network = root(2, '/library', 'Network Library')
    const updatedNetwork: RootFolder = {
      ...network,
      caseSensitivityMode: 'Sensitive',
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    }
    vi.mocked(apiService.getRootFolders)
      .mockResolvedValueOnce([other, network])
      .mockResolvedValueOnce([other, updatedNetwork])
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValue({
      status: 'Completed',
    } as never)
    confirmMock.mockResolvedValue(true)

    const error = Object.assign(new Error('API error'), {
      status: 400,
      body: JSON.stringify({
        code: 'destination_file_publication_unavailable',
        field: 'destinationPath',
        message: 'Automatic case semantics need confirmation.',
      }),
    })

    await expect(
      confirmMutationSemanticsForBlockedOperation(error, {
        path: '/library/Author/Book',
        operationLabel: 'the move',
      }),
    ).resolves.toBe('retry')

    expect(confirmMock).toHaveBeenCalledWith(
      expect.stringContaining('Network Library'),
      'Confirm storage behavior',
      expect.objectContaining({ confirmText: 'Use case-sensitive & continue' }),
    )
    expect(apiService.changeRootFolderPath).toHaveBeenCalledWith(
      2,
      expect.objectContaining({
        targetPath: '/library',
        mode: 'metadataOnly',
        targetCaseSensitivityMode: 'Sensitive',
      }),
    )
    expect(toastMocks.success).toHaveBeenCalledWith(
      'Storage behavior confirmed',
      expect.stringContaining('case-sensitive'),
    )
  })

  it('rescans an unverified delete source before allowing the delete retry', async () => {
    const error = Object.assign(new Error('API error'), {
      status: 409,
      body: JSON.stringify({
        code: 'delete_source_unverified',
        message: 'Rescan the audiobook and try again.',
      }),
    })

    await expect(preparePhysicalDeleteRetry(error, 42, '/library/Author/Book')).resolves.toBe(true)

    expect(apiService.scanAudiobook).toHaveBeenCalledWith(42)
    expect(apiService.getScanJobStatus).toHaveBeenCalledWith('scan-1')
    expect(apiService.getRootFolders).not.toHaveBeenCalled()
    expect(confirmMock).not.toHaveBeenCalled()
  })

  it('confirms storage semantics and refreshes file identity before a delete retry', async () => {
    const network = root(2, '/library', 'Network Library')
    const updatedNetwork: RootFolder = {
      ...network,
      caseSensitivityMode: 'Sensitive',
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    }
    vi.mocked(apiService.getRootFolders)
      .mockResolvedValueOnce([network])
      .mockResolvedValueOnce([updatedNetwork])
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValue({ status: 'Completed' } as never)
    confirmMock.mockResolvedValue(true)
    const error = Object.assign(new Error('API error'), {
      status: 409,
      body: JSON.stringify({
        code: 'filesystem_mutation_unavailable',
        message: 'Automatic case semantics need confirmation.',
      }),
    })

    await expect(preparePhysicalDeleteRetry(error, 42, '/library/Author/Book')).resolves.toBe(true)

    expect(apiService.changeRootFolderPath).toHaveBeenCalled()
    expect(apiService.scanAudiobook).toHaveBeenCalledWith(42)
    expect(apiService.getScanJobStatus).toHaveBeenCalledWith('scan-1')
  })

  it('defers identity refresh to existing deletion recovery when the scan is already blocked by that intent', async () => {
    const network = root(2, '/library', 'Network Library')
    const updatedNetwork: RootFolder = {
      ...network,
      caseSensitivityMode: 'Sensitive',
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    }
    vi.mocked(apiService.getRootFolders)
      .mockResolvedValueOnce([network])
      .mockResolvedValueOnce([updatedNetwork])
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValue({ status: 'Completed' } as never)
    vi.mocked(apiService.scanAudiobook).mockRejectedValueOnce(
      Object.assign(new Error('Deletion recovery owns this audiobook'), {
        status: 409,
        body: JSON.stringify({
          code: 'delete_recovery_pending',
          message: 'An existing deletion recovery is still active.',
        }),
      }),
    )
    confirmMock.mockResolvedValue(true)
    const error = Object.assign(new Error('API error'), {
      status: 409,
      body: JSON.stringify({
        code: 'filesystem_mutation_unavailable',
        message: 'Automatic case semantics need confirmation.',
      }),
    })

    await expect(preparePhysicalDeleteRetry(error, 42, '/library/Author/Book')).resolves.toBe(true)

    expect(apiService.changeRootFolderPath).toHaveBeenCalled()
    expect(apiService.scanAudiobook).toHaveBeenCalledWith(42)
    expect(apiService.getScanJobStatus).not.toHaveBeenCalled()
  })

  it('does not prompt for unrelated mutation errors', async () => {
    const error = Object.assign(new Error('API error'), {
      status: 400,
      body: JSON.stringify({
        code: 'destination_path_invalid',
        field: 'destinationPath',
        message: 'Invalid destination.',
      }),
    })

    await expect(
      confirmMutationSemanticsForBlockedOperation(error, {
        path: '/library/Author/Book',
        operationLabel: 'the move',
      }),
    ).resolves.toBe('not-applicable')

    expect(apiService.getRootFolders).not.toHaveBeenCalled()
    expect(confirmMock).not.toHaveBeenCalled()
  })
})
