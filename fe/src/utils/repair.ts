/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import type {
  Audiobook,
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  AudiobookUpdateRequest,
} from '@/types'
import {
  createBookMatch,
  matchState,
  type BookMatch,
  type BookRecord,
  type BookTask,
  type BookTaskState,
} from '@/utils/bookMatch'

// Repair Books: each book's new record picked (step 1), the repairs and their results (step 2),
// then the repaired books organized (step 3, in the Organize dialog)

// Repair takes three steps, the last in the Organize dialog it opens: its title there
export const REPAIR_ORGANIZE_TITLE = 'Repair Books - Organize - Step 3/3'

// Steps 1 and 2 of three: pick each book's new record, then the repair process and its results,
// to choose what to organize
export function repairStepTitle(isRepairStep: boolean): string {
  return isRepairStep
    ? 'Repair Books - Repair process - Step 2/3'
    : 'Repair Books - Matching - Step 1/3'
}

export interface RepairRow {
  audiobookId: number
  title: string
  subtitle: string
  authors: string[]
  identifiers: AudiobookExternalIdentifier[]
  // The record the book should point at, edited in its card
  bookMatch: BookMatch
  // Ticked: repaired once it has a new record (finding one ticks it)
  isSelected: boolean
  // Its repair, once started: waiting, running, then how it ended (shown by its card)
  repairTask: BookTask | null
  // Its record when the repair started, the one it is to get (repaired books only), and its record
  // after the repair
  recordBefore: BookRecord | null
  plannedRecord: BookRecord | null
  recordAfter: BookRecord | null
  // Why the book couldn't be loaded when the dialog opened (its card then shows only that), and
  // whether loading it again can help (not for a book deleted since it was selected)
  loadError: RepairLoadError | null
}

export interface RepairLoadError {
  message: string
  canLoadAgain: boolean
}

// One book to repair on step 2, from the moment the repair starts
export interface RepairOutcome {
  audiobookId: number
  title: string
  // Waiting, running, then how its repair ended: done, warning (in part), failed, or skipped
  // (canceled before its turn)
  state: BookTaskState
  // The line saying where its repair is or how it ended
  message: string
  // Its record before the repair; the record it is to get; and its record after the repair (null
  // until it is, or when it failed)
  recordBefore: BookRecord
  plannedRecord: BookRecord
  recordAfter: BookRecord | null
}

function primaryAsin(identifiers: AudiobookExternalIdentifier[]) {
  const asinIdentifiers = identifiers.filter((identifier) => identifier.type === 'Asin')
  return asinIdentifiers.find((identifier) => identifier.isPrimary) ?? asinIdentifiers[0]
}

// A book as step 1 shows it: its primary ASIN and region (the configured one when it has none),
// its title and first author to search with
export function repairRowFrom(
  audiobookId: number,
  book: Audiobook,
  identifiers: AudiobookExternalIdentifier[],
  configuredRegion: string,
): RepairRow {
  const primaryAsinIdentifier = primaryAsin(identifiers)
  const bookMatch = createBookMatch(
    {
      asin: primaryAsinIdentifier?.value ?? book.asin ?? '',
      region: primaryAsinIdentifier?.region || configuredRegion,
      query: book.title ?? '',
      author: book.authors?.[0] ?? '',
    },
    {
      asin: primaryAsinIdentifier?.value ?? book.asin ?? '',
      region: primaryAsinIdentifier?.region ?? '',
    },
  )
  return {
    audiobookId,
    title: book.title ?? '',
    subtitle: book.subtitle ?? '',
    authors: book.authors ?? [],
    identifiers,
    bookMatch,
    isSelected: false,
    repairTask: null,
    recordBefore: null,
    plannedRecord: null,
    recordAfter: null,
    loadError: null,
  }
}

// A book that couldn't be loaded: its card says why and can't be matched. Its title when the book
// itself was read (its identifiers failed), else its number
export function failedRepairRow(audiobookId: number, error: unknown, title = ''): RepairRow {
  const isNotFound = (error as { status?: number })?.status === 404
  return {
    ...repairRowFrom(audiobookId, { id: audiobookId, title } as Audiobook, [], ''),
    loadError: {
      message: isNotFound
        ? 'Not found: deleted since it was selected'
        : `Couldn't be loaded: ${error instanceof Error ? error.message : 'unknown error'}`,
      canLoadAgain: !isNotFound,
    },
  }
}

export function isMatched(repairRow: RepairRow): boolean {
  return matchState(repairRow.bookMatch) === 'matched'
}

// Only a book with a new record, looked up in its region, is repaired
export function isIncluded(repairRow: RepairRow): boolean {
  return repairRow.isSelected && isMatched(repairRow)
}

// What a book takes from its record and the record may not have: emptied before every repair's
// rescan, so the book takes its record as a whole. The rescan only writes what the record has, so
// an old record's series, subtitle, narrators… would stay (and Organize would file the book under
// the old series). The title, authors, cover and runtime are always read again: they stay until
// then, so a failed rescan never leaves a book without a title or author.
export const EMPTIED_RECORD_FIELDS: AudiobookUpdateRequest = {
  subtitle: '',
  series: '',
  seriesNumber: '',
  seriesMemberships: [],
  description: '',
  publisher: '',
  publishYear: '',
  publishedDate: '',
  language: '',
  version: '',
  edition: '',
  narrators: [],
  genres: [],
  isbn: [],
}

// Asked before the repairs start: what a repair does to each book, which can't be undone
export function repairConfirmTitle(count: number): string {
  return `Repair ${count} book${count === 1 ? '' : 's'}?`
}
export const REPAIR_CONFIRM_MESSAGE =
  "Each book takes its new record as a whole: its title, authors, series, subtitle, narrators, description, genres and the rest are replaced by the record's, and what the record doesn't have is emptied. Changes made by hand are lost. The files, the folder and the book's settings stay.\n\nThis can't be undone."

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again, and so is the
// ASIN it is repaired away from. Other Manual ones stay.
export function identifiersWithPrimaryAsin(
  repairRow: RepairRow,
): AudiobookExternalIdentifierInput[] {
  // Only a matched book is repaired: its new record is the picked one, whatever the fields hold
  const asin = (repairRow.bookMatch.pickedSearchResult?.asin ?? '').toUpperCase()
  const region = repairRow.bookMatch.pickedSearchResult?.region || repairRow.bookMatch.region
  // The record it is repaired away from, whatever its source: a Manual one too (an earlier
  // repair's, saved as Manual by the server, or a wrong one typed by hand) would stay a fallback
  // the rescan can read
  const replacedAsin = repairRow.bookMatch.currentRecordKey.asin.toUpperCase()
  const manualIdentifiers = repairRow.identifiers
    .filter((identifier) => identifier.source === 'Manual')
    .filter(
      (identifier) =>
        !(
          identifier.type === 'Asin' &&
          [asin, replacedAsin].includes(identifier.value.toUpperCase())
        ),
    )
    .map((identifier) => ({
      type: identifier.type,
      value: identifier.value,
      region: identifier.type === 'Asin' ? identifier.region || null : null,
      isPrimary: identifier.type === 'Asin' ? false : Boolean(identifier.isPrimary),
      source: 'Manual' as const,
    }))
  return [
    { type: 'Asin', value: asin, region, isPrimary: true, source: 'Manual' },
    ...manualIdentifiers,
  ]
}

// Step 2: the books to repair only, the unticked or unmatched ones stay behind on step 1
export function toRepairOutcomes(repairRows: RepairRow[]): RepairOutcome[] {
  return repairRows
    .filter(
      (repairRow): repairRow is RepairRow & { plannedRecord: BookRecord } =>
        !!repairRow.plannedRecord,
    )
    .map((repairRow) => ({
      audiobookId: repairRow.audiobookId,
      title: repairRow.title,
      state: repairRow.repairTask?.state ?? 'skipped',
      message: repairRow.repairTask?.text ?? '',
      recordBefore: repairRow.recordBefore ?? {},
      plannedRecord: repairRow.plannedRecord,
      recordAfter: repairRow.recordAfter,
    }))
}

// What went wrong on step 2, counted by kind: the footer's warning, and its bar orange
export function repairProblemText(repairRows: RepairRow[], canceledCount: number): string | null {
  const countInState = (state: BookTaskState) =>
    repairRows.filter((repairRow) => repairRow.repairTask?.state === state).length
  const problemParts = [
    [countInState('failed'), 'failed'],
    [countInState('warning'), 'repaired in part'],
    [canceledCount, 'canceled'],
  ]
    .filter(([problemCount]) => problemCount)
    .map(([problemCount, problemLabel]) => `${problemCount} ${problemLabel}`)
  return problemParts.length ? `Not all repaired: ${problemParts.join(', ')}` : null
}

// The longest warning the books on step 2 can bring: every kind, each counting them all
export function longestRepairProblemText(bookCount: number): string {
  return `Not all repaired: ${bookCount} failed, ${bookCount} repaired in part, ${bookCount} canceled`
}

// The record a book gets: the one it got, or until its repair ends the one it is to get. None once
// its repair failed or was canceled
export function newRecord(repairOutcome: RepairOutcome): BookRecord | null {
  if (repairOutcome.recordAfter) return repairOutcome.recordAfter
  return repairOutcome.state === 'waiting' || repairOutcome.state === 'running'
    ? repairOutcome.plannedRecord
    : null
}

export function booksRepairedText(bookCount: number): string {
  return `${bookCount} ${bookCount === 1 ? 'book' : 'books'} repaired`
}

export function organizeQuestion(bookCount: number): string {
  const isSingleBook = bookCount === 1
  return (
    `, ${isSingleBook ? 'its' : 'their'} metadata refreshed from the new Audible record. ` +
    `Organize ${isSingleBook ? 'it' : 'them'} now? ${isSingleBook ? 'Its files were' : 'Their files were'} last ` +
    'organized with the metadata from before, which may have been wrong.'
  )
}
