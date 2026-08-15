import type { RootFolder } from '@/types'
import { detectPathKind, pathsEqual, type PathCaseSensitivity, type PathKind } from '@/utils/path'

export function persistedRootPathKind(root: RootFolder): PathKind {
  if (root.pathSyntax === 'Windows') return 'windows'
  if (root.pathSyntax === 'Unix') return 'unix'
  return detectPathKind(root.path)
}

export function persistedRootCaseSensitivity(root: RootFolder): PathCaseSensitivity {
  if (root.caseSensitivityMode === 'Sensitive') return 'Sensitive'
  if (root.caseSensitivityMode === 'Insensitive') return 'Insensitive'
  if (
    root.caseSensitivityMode === 'Auto' &&
    root.pathIdentityState === 'Valid' &&
    root.resolvedCaseSensitivity &&
    root.resolvedCaseSensitivity !== 'Unknown'
  ) {
    return root.resolvedCaseSensitivity
  }

  // Unavailable, conflicting, or incomplete persisted identity must fail closed.
  return 'Sensitive'
}

export function rootFolderPathChanged(root: RootFolder, candidatePath: string): boolean {
  const sourceKind = persistedRootPathKind(root)
  const candidateKind = detectPathKind(candidatePath, sourceKind)
  if (sourceKind === 'unknown' && candidateKind !== 'unknown') return true
  if (sourceKind !== 'unknown' && candidateKind !== 'unknown' && sourceKind !== candidateKind) {
    return true
  }

  return !pathsEqual(candidatePath, root.path, sourceKind, persistedRootCaseSensitivity(root))
}
