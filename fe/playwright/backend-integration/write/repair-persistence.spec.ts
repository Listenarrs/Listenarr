import type { APIRequestContext } from '@playwright/test'
import { antiforgeryHeaders } from '../support/stack.js'
import { expect, test } from '../support/write-test.js'

type LibraryBook = { id: number; title: string; authors: string[] }
type QualityProfile = { id: number }
type Identifier = { type: string; value: string; region?: string | null; isPrimary: boolean; source?: string }

// The record the backend integration host's Audible fixtures serve (tests/Listenarr.TestHost/Metadata/Fixtures).
const match = { asin: 'B005FRGT44', region: 'us', title: 'Ready Player One', author: 'Ernest Cline' }

// What a repair empties before the rescan, so the book keeps nothing of its old record.
const emptiedRecord = {
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

/** The three requests the Repair Books dialog sends for one book whose record changes. */
async function repair(request: APIRequestContext, bookId: number, headers: Record<string, string>) {
  const identifiers = await request.put(`/api/v1/library/${bookId}/identifiers`, {
    data: {
      identifiers: [{ type: 'Asin', value: match.asin, region: match.region, isPrimary: true, source: 'Manual' }],
    },
    headers,
  })
  if (!identifiers.ok()) return { stage: 'identifiers', status: identifiers.status() }

  const metadata = await request.put(`/api/v1/library/${bookId}`, { data: emptiedRecord, headers })
  if (!metadata.ok()) return { stage: 'metadata', status: metadata.status() }

  const rescan = await request.post(`/api/v1/library/${bookId}/rescan-metadata`, { headers })
  return { stage: 'rescan', status: rescan.status(), body: rescan.ok() ? await rescan.json() : await rescan.text() }
}

test.describe('Backend integration repair persistence', () => {
  test('fails a deleted book but continues and persists the next selected book', async ({ request }) => {
    const [deletedBook, repairedBook] = (await (await request.get('/api/v1/library')).json()) as LibraryBook[]
    if (!deletedBook || !repairedBook) throw new Error('The backend integration seed needs two library books.')
    const headers = await antiforgeryHeaders(request)

    const deleteResponse = await request.delete(`/api/v1/library/${deletedBook.id}`, {
      params: { deleteFiles: false, deleteFolder: false },
      headers,
    })
    expect(deleteResponse.status()).toBe(200)

    expect(await repair(request, deletedBook.id, headers)).toMatchObject({ stage: 'identifiers', status: 404 })

    const repaired = await repair(request, repairedBook.id, headers)
    expect(repaired).toMatchObject({
      stage: 'rescan',
      status: 200,
      body: { audiobookId: repairedBook.id, asin: match.asin, region: match.region },
    })

    const identifiersResponse = await request.get(`/api/v1/library/${repairedBook.id}/identifiers`)
    const { identifiers } = (await identifiersResponse.json()) as { identifiers: Identifier[] }
    expect(
      identifiers.filter(
        (identifier) =>
          identifier.value.toUpperCase() === match.asin &&
          identifier.region?.toLowerCase() === match.region &&
          identifier.isPrimary,
      ),
    ).toHaveLength(1)
    const savedBook = (await (await request.get(`/api/v1/library/${repairedBook.id}`)).json()) as LibraryBook
    expect(savedBook.title).toBe(match.title)
    expect(savedBook.authors).toEqual([match.author])

    expect(await (await request.get('/api/v1/library')).json()).toHaveLength(3)
    expect((await request.get(`/api/v1/library/${deletedBook.id}`)).status()).toBe(404)

    // Retrying the deleted book fails the same way.
    expect(await repair(request, deletedBook.id, headers)).toMatchObject({ stage: 'identifiers', status: 404 })
  })

  test('keeps monitoring and quality-profile settings after a record-change repair', async ({ request }) => {
    const book = ((await (await request.get('/api/v1/library')).json()) as LibraryBook[])[0]
    if (!book) throw new Error('The backend integration seed needs a library book.')
    const headers = await antiforgeryHeaders(request)

    const profileResponse = await request.post('/api/v1/qualityprofile', {
      data: { name: 'Backend Integration Repair Profile', isDefault: true },
      headers,
    })
    expect(profileResponse.ok()).toBe(true)
    const profile = (await profileResponse.json()) as QualityProfile
    const settingsResponse = await request.put(`/api/v1/library/${book.id}`, {
      data: { monitored: false, qualityProfileId: profile.id },
      headers,
    })
    expect(settingsResponse.status()).toBe(200)

    expect(await repair(request, book.id, headers)).toMatchObject({ stage: 'rescan', status: 200 })

    const savedBook = await (await request.get(`/api/v1/library/${book.id}`)).json()
    expect(savedBook).toMatchObject({ title: match.title, monitored: false, qualityProfileId: profile.id })
  })
})
