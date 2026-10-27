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
export function repairStepTitle(repairStep: boolean): string {
  return repairStep
    ? 'Repair Books - Repair process - Step 2/3'
    : 'Repair Books - Matching - Step 1/3'
}

export interface RepairRow {
  // The audiobook's
  id: number
  title: string
  subtitle: string
  authors: string[]
  identifiers: AudiobookExternalIdentifier[]
  // The record the book should point at, edited in its card
  match: BookMatch
  // Ticked: repaired once it has a new record (finding one ticks it)
  selected: boolean
  // Its repair, once started: waiting, running, then how it ended (shown by its card)
  task: BookTask | null
  // Its record when the repair started, the one it is to get (repaired books only), and its record
  // after the repair
  before: BookRecord | null
  planned: BookRecord | null
  after: BookRecord | null
}

// One book to repair on step 2, from the moment the repair starts
export interface RepairOutcome {
  id: number
  title: string
  // Waiting, running, then how its repair ended: done, warning (in part), failed, or skipped
  // (canceled before its turn)
  state: BookTaskState
  // The line saying where its repair is or how it ended
  message: string
  // Its record before the repair; the record it is to get; and its record after the repair (null
  // until it is, or when it failed)
  before: BookRecord
  planned: BookRecord
  after: BookRecord | null
}

function primaryAsin(identifiers: AudiobookExternalIdentifier[]) {
  const asins = identifiers.filter((i) => i.type === 'Asin')
  return asins.find((i) => i.isPrimary) ?? asins[0]
}

// A book as step 1 shows it: its primary ASIN and region (the configured one when it has none),
// its title and first author to search with
export function repairRowFrom(
  audiobookId: number,
  book: Audiobook,
  identifiers: AudiobookExternalIdentifier[],
  configuredRegion: string,
): RepairRow {
  const asin = primaryAsin(identifiers)
  const match = createBookMatch(
    {
      asin: asin?.value ?? book.asin ?? '',
      region: asin?.region || configuredRegion,
      query: book.title ?? '',
      author: book.authors?.[0] ?? '',
    },
    { asin: asin?.value ?? book.asin ?? '', region: asin?.region ?? '' },
  )
  return {
    id: audiobookId,
    title: book.title ?? '',
    subtitle: book.subtitle ?? '',
    authors: book.authors ?? [],
    identifiers,
    match,
    selected: false,
    task: null,
    before: null,
    planned: null,
    after: null,
  }
}

export function isMatched(row: RepairRow): boolean {
  return matchState(row.match) === 'matched'
}

// Only a book with a new record, looked up in its region, is repaired
export function isIncluded(row: RepairRow): boolean {
  return row.selected && isMatched(row)
}

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again. Manual ones stay.
export function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  // Only a matched book is repaired: its new record is the picked one, whatever the fields hold
  const asin = (row.match.picked?.asin ?? '').toUpperCase()
  const region = row.match.picked?.region || row.match.region
  const manual = row.identifiers
    .filter((i) => i.source === 'Manual')
    .filter((i) => !(i.type === 'Asin' && i.value.toUpperCase() === asin))
    .map((i) => ({
      type: i.type,
      value: i.value,
      region: i.type === 'Asin' ? i.region || null : null,
      isPrimary: i.type === 'Asin' ? false : Boolean(i.isPrimary),
      source: 'Manual' as const,
    }))
  return [{ type: 'Asin', value: asin, region, isPrimary: true, source: 'Manual' }, ...manual]
}

// Step 2: the books to repair only, the unticked or unmatched ones stay behind on step 1
export function repairOutcomes(rows: RepairRow[]): RepairOutcome[] {
  return rows
    .filter((row): row is RepairRow & { planned: BookRecord } => !!row.planned)
    .map((row) => ({
      id: row.id,
      title: row.title,
      state: row.task?.state ?? 'skipped',
      message: row.task?.text ?? '',
      before: row.before ?? {},
      planned: row.planned,
      after: row.after,
    }))
}

// What went wrong on step 2, counted by kind: the footer's warning, and its bar orange
export function repairProblemText(rows: RepairRow[], canceledCount: number): string | null {
  const count = (state: BookTaskState) => rows.filter((row) => row.task?.state === state).length
  const parts = [
    [count('failed'), 'failed'],
    [count('warning'), 'repaired in part'],
    [canceledCount, 'canceled'],
  ]
    .filter(([n]) => n)
    .map(([n, what]) => `${n} ${what}`)
  return parts.length ? `Not all repaired: ${parts.join(', ')}` : null
}

// The longest warning the books on step 2 can bring: every kind, each counting them all
export function longestRepairProblemText(total: number): string {
  return `Not all repaired: ${total} failed, ${total} repaired in part, ${total} canceled`
}

// The record a book gets: the one it got, or until its repair ends the one it is to get. None once
// its repair failed or was canceled
export function newRecord(outcome: RepairOutcome): BookRecord | null {
  if (outcome.after) return outcome.after
  return outcome.state === 'waiting' || outcome.state === 'running' ? outcome.planned : null
}

export function booksRepaired(count: number): string {
  return `${count} ${count === 1 ? 'book' : 'books'} repaired`
}

export function organizeQuestion(count: number): string {
  const one = count === 1
  return (
    `, ${one ? 'its' : 'their'} metadata refreshed from the new Audible record. ` +
    `Organize ${one ? 'it' : 'them'} now? ${one ? 'Its files were' : 'Their files were'} last ` +
    'organized with the metadata from before, which may have been wrong.'
  )
}
