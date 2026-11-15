import { expect, test } from '@playwright/test'
import type { GetLibraryGetAllApiV1LibraryResponses } from '../../../cypress/support/generated-api/types.gen.js'

test('reads the seeded library using the generated API contract', async ({ request }) => {
  const response = await request.get('/api/v1/library')
  expect(response.status()).toBe(200)

  const books = (await response.json()) as GetLibraryGetAllApiV1LibraryResponses['200']
  expect(books).toHaveLength(4)
  expect(books.map((book) => book.title ?? '').sort()).toEqual([
    'Pride and Prejudice',
    'Robinson Crusoe',
    'The Strange Case of Dr Jekyll and Mr Hyde',
    'Twenty Thousand Leagues Under the Sea',
  ])
  const verneBook = books.find((book) => book.title === 'Twenty Thousand Leagues Under the Sea')
  expect(verneBook?.authors).toEqual(['Jules Verne'])
})
