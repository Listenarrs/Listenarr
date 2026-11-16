import type { CoverageReportOptions } from 'monocart-coverage-reports'

/** The Library Import code: its view, components, store and helpers. Their coverage is gated. */
export const LIBRARY_IMPORT_SOURCES = [
  'src/views/library/LibraryImportView.vue',
  'src/components/domain/audiobook/LibraryImportRow.vue',
  'src/components/domain/audiobook/LibraryImportFooter.vue',
  'src/components/domain/audiobook/LibraryImportSearchModal.vue',
  'src/stores/libraryImport.ts',
  'src/utils/libraryImportSearch.ts',
  'src/utils/libraryImportTable.ts',
]

/** Minimum percentages over LIBRARY_IMPORT_SOURCES, all files together. */
export const THRESHOLDS = { lines: 90, statements: 90 } as const

export const coverageOutputDirectory = './test-results/e2e-coverage'

export const coverageOptions: CoverageReportOptions = {
  name: 'Listenarr E2E coverage: Library Import',
  outputDir: coverageOutputDirectory,
  reports: ['v8', 'console-details', 'json-summary', 'lcovonly'],
  // The scripts of those files as the Vite dev server serves them: not their style blocks
  // (`?vue&type=style`), not the rest of the app or its dependencies.
  entryFilter: (entry) => {
    const url = new URL(entry.url)
    return (
      !url.searchParams.has('type') &&
      LIBRARY_IMPORT_SOURCES.some((source) => url.pathname === `/${source}`)
    )
  },
  // Each of those modules maps to its own source; name it by its path under src/, whatever form its
  // source map gives (a bare file name for some), and keep only those files in the report.
  sourcePath: (filePath, info) => {
    for (const candidate of [filePath, info.distFile ?? '']) {
      const index = candidate.lastIndexOf('src/')
      const path = index === -1 ? '' : candidate.slice(index).replace(/[?#].*$/, '')
      if (LIBRARY_IMPORT_SOURCES.includes(path)) return path
    }
    const name = filePath.split('/').pop()
    return LIBRARY_IMPORT_SOURCES.find((source) => source.endsWith(`/${name}`)) ?? filePath
  },
  outputFilter: (file) => LIBRARY_IMPORT_SOURCES.includes(file.sourcePath),
}
