import { antiforgeryHeaders } from '../../support/docker-stack.js'
import { expect, test } from '../support/write-test.js'

type LibraryBook = { id: number; title: string }
type QualityProfile = { id: number; name: string; isDefault: boolean }

test.describe('Backend integration library persistence', () => {
  test('removes a deleted book from the database while the other books stay readable', async ({ request }) => {
    const books = (await (await request.get('/api/v1/library')).json()) as LibraryBook[]
    expect(books).toHaveLength(4)
    const deletedBook = books[0]!
    const remainingBooks = books.slice(1)

    const deleteResponse = await request.delete(`/api/v1/library/${deletedBook.id}`, {
      params: { deleteFiles: false, deleteFolder: false },
      headers: await antiforgeryHeaders(request),
    })
    expect(deleteResponse.status()).toBe(200)

    expect((await request.get(`/api/v1/library/${deletedBook.id}`)).status()).toBe(404)
    for (const book of remainingBooks) {
      const response = await request.get(`/api/v1/library/${book.id}`)
      expect(response.status()).toBe(200)
      expect(((await response.json()) as LibraryBook).title).toBe(book.title)
    }
    expect(await (await request.get('/api/v1/library')).json()).toHaveLength(books.length - 1)
  })

  test('keeps per-book monitoring and quality profile settings after editing metadata', async ({ request }) => {
    const updatedSubtitle = 'Persisted backend integration subtitle'
    const book = ((await (await request.get('/api/v1/library')).json()) as LibraryBook[])[0]!
    const headers = await antiforgeryHeaders(request)

    const profileResponse = await request.post('/api/v1/qualityprofile', {
      data: { name: 'Backend Integration Persistence Profile', isDefault: true },
      headers,
    })
    expect(profileResponse.ok()).toBe(true)
    const profile = (await profileResponse.json()) as QualityProfile

    const settingsResponse = await request.put(`/api/v1/library/${book.id}`, {
      data: { monitored: false, qualityProfileId: profile.id },
      headers,
    })
    expect(settingsResponse.status()).toBe(200)

    const metadataResponse = await request.put(`/api/v1/library/${book.id}`, {
      data: { subtitle: updatedSubtitle },
      headers,
    })
    expect(metadataResponse.status()).toBe(200)

    const savedBook = await (await request.get(`/api/v1/library/${book.id}`)).json()
    expect(savedBook).toMatchObject({
      monitored: false,
      qualityProfileId: profile.id,
      subtitle: updatedSubtitle,
    })
  })
})
