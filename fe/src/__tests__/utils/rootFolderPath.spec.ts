import { describe, expect, it } from 'vitest'
import type { RootFolder } from '@/types'
import { rootFolderPathChanged } from '@/utils/rootFolderPath'

function createRoot(path: string, pathSyntax: RootFolder['pathSyntax'] = null): RootFolder {
  return {
    id: 1,
    name: 'Library',
    path,
    pathSyntax,
    isDefault: true,
    caseSensitivityMode: 'Auto',
    resolvedCaseSensitivity: 'Unknown',
    pathIdentityState: 'Unavailable',
  }
}

describe('rootFolderPathChanged', () => {
  it('treats an unambiguous Windows repair of an ambiguous persisted root as a path change', () => {
    const root = createRoot('//server/share/library')

    expect(rootFolderPathChanged(root, '\\\\server\\share\\library')).toBe(true)
  })

  it('does not manufacture a path change when an ambiguous persisted root is unchanged', () => {
    const root = createRoot('//server/share/library')

    expect(rootFolderPathChanged(root, '//server/share/library')).toBe(false)
  })
})
