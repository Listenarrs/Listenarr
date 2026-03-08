import { computed, type Ref, type ComputedRef } from 'vue'

/** Windows MAX_PATH limit (260 chars including null terminator, so 259 usable). */
const WINDOWS_MAX_PATH = 259

/**
 * Composable that monitors a reactive path string and returns a warning
 * when it approaches or exceeds the Windows MAX_PATH limit.
 *
 * @param path - A ref or computed that evaluates to the full destination path
 * @returns `{ pathLength, pathLengthWarning }` — reactive length and a warning string (or null)
 */
export function usePathLengthCheck(path: Ref<string> | ComputedRef<string>) {
  const pathLength = computed(() => (path.value || '').length)

  const pathLengthWarning = computed<string | null>(() => {
    const len = pathLength.value
    if (len === 0) return null
    if (len <= WINDOWS_MAX_PATH) return null

    const excess = len - WINDOWS_MAX_PATH
    return (
      `This path is ${len} characters — ${excess} over the Windows limit of 260 characters. ` +
      'The server will automatically truncate long paths, but the result may differ from what you expect.'
    )
  })

  return { pathLength, pathLengthWarning, WINDOWS_MAX_PATH }
}
