import { flushPromises } from '@vue/test-utils'
import { nextTick } from 'vue'

type WaitForOptions = {
  interval?: number
  timeout?: number
}

export const delay = (ms = 0) => new Promise((resolve) => setTimeout(resolve, ms))

export function createDeferred<T = unknown>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((promiseResolve, promiseReject) => {
    resolve = promiseResolve
    reject = promiseReject
  })

  return { promise, resolve, reject }
}

export async function flushAsync(ticks = 1) {
  await flushPromises()
  for (let i = 0; i < ticks; i++) {
    await nextTick()
  }
  await delay(0)
}

export async function waitFor(
  assertion: () => void | boolean | Promise<void | boolean>,
  options: WaitForOptions = {},
) {
  const timeout = options.timeout ?? 1000
  const interval = options.interval ?? 20
  const start = Date.now()
  let lastError: unknown

  while (Date.now() - start < timeout) {
    try {
      const result = await assertion()
      if (result !== false) return
    } catch (error) {
      lastError = error
    }

    await delay(interval)
  }

  if (lastError instanceof Error) throw lastError
  throw new Error(`Timed out after ${timeout}ms waiting for condition.`)
}
