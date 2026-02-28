import { ref, computed } from 'vue'
import { useToast } from '@/services/toastService'

export function useFormState<T>(initialData: T) {
  const formData = ref<T>(initialData)
  const isSaving = ref(false)
  const saveError = ref<string | null>(null)
  const toast = useToast()

  const hasChanges = computed(() => {
    return JSON.stringify(formData.value) !== JSON.stringify(initialData)
  })

  const resetForm = () => {
    formData.value = JSON.parse(JSON.stringify(initialData))
    saveError.value = null
  }

  const handleSaveError = (error: any) => {
    const message = error?.message || 'Failed to save changes'
    saveError.value = message
    toast.error('Error', message)
  }

  const clearError = () => {
    saveError.value = null
  }

  return {
    formData,
    isSaving,
    saveError,
    hasChanges,
    resetForm,
    handleSaveError,
    clearError,
  }
}
