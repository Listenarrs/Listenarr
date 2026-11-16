import { afterEach, vi } from 'vitest'

vi.mock('@/services/signalr', async () => {
  const { signalRServiceMock } = await import('@/test/mocks/signalr')

  return {
    signalRService: signalRServiceMock.signalRService,
  }
})

afterEach(async () => {
  const { resetSignalRServiceMock } = await import('@/test/mocks/signalr')

  resetSignalRServiceMock()
})
