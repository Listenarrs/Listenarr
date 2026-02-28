import { ref } from 'vue'

export function useLoadingState() {
  const isLoading = ref(false)
  const error = ref<string | null>(null)

  const setLoading = (value: boolean) => {
    isLoading.value = value
  }

  const setError = (message: string | null) => {
    error.value = message
  }

  const reset = () => {
    isLoading.value = false
    error.value = null
  }

  const clearError = () => {
    error.value = null
  }

  return {
    isLoading,
    error,
    setLoading,
    setError,
    reset,
    clearError,
  }
}
