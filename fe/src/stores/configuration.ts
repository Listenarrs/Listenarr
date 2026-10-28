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
import { defineStore } from 'pinia'
import { ref } from 'vue'
import type {
  ApiConfiguration,
  DownloadClientConfiguration,
  ApplicationSettings,
  QualityProfile,
} from '@/types'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'

export const useConfigurationStore = defineStore('configuration', () => {
  const apiConfigurations = ref<ApiConfiguration[]>([])
  const downloadClientConfigurations = ref<DownloadClientConfiguration[]>([])
  const applicationSettings = ref<ApplicationSettings | null>(null)
  const qualityProfiles = ref<QualityProfile[]>([])
  const isLoading = ref(false)

  const loadApiConfigurations = async () => {
    isLoading.value = true
    try {
      const configs = await apiService.getApiConfigurations()
      apiConfigurations.value = configs
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'loadApiConfigurations',
      })
    } finally {
      isLoading.value = false
    }
  }

  const saveApiConfiguration = async (config: ApiConfiguration) => {
    try {
      await apiService.saveApiConfiguration(config)
      await loadApiConfigurations() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'saveApiConfiguration',
        metadata: { configId: config.id },
      })
      throw error
    }
  }

  const deleteApiConfiguration = async (id: string) => {
    try {
      await apiService.deleteApiConfiguration(id)
      await loadApiConfigurations() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'deleteApiConfiguration',
        metadata: { configId: id },
      })
      throw error
    }
  }

  const loadDownloadClientConfigurations = async () => {
    isLoading.value = true
    try {
      const configs = await apiService.getDownloadClientConfigurations()
      downloadClientConfigurations.value = configs
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'loadDownloadClientConfigurations',
      })
    } finally {
      isLoading.value = false
    }
  }

  const saveDownloadClientConfiguration = async (config: DownloadClientConfiguration) => {
    try {
      await apiService.saveDownloadClientConfiguration(config)
      await loadDownloadClientConfigurations() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'saveDownloadClientConfiguration',
        metadata: { configId: config.id },
      })
      throw error
    }
  }

  const deleteDownloadClientConfiguration = async (id: string) => {
    try {
      await apiService.deleteDownloadClientConfiguration(id)
      await loadDownloadClientConfigurations() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'deleteDownloadClientConfiguration',
        metadata: { configId: id },
      })
      throw error
    }
  }

  const loadApplicationSettings = async () => {
    isLoading.value = true
    try {
      const settings = await apiService.getApplicationSettings()
      applicationSettings.value = settings
      return settings
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'loadApplicationSettings',
      })
      return null
    } finally {
      isLoading.value = false
    }
  }

  const saveApplicationSettings = async (settings: ApplicationSettings) => {
    try {
      const savedSettings = await apiService.saveApplicationSettings(settings)
      applicationSettings.value = savedSettings
      return savedSettings
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'saveApplicationSettings',
      })
      throw error
    }
  }

  const loadQualityProfiles = async () => {
    isLoading.value = true
    try {
      const profiles = await apiService.getQualityProfiles()
      qualityProfiles.value = profiles
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'loadQualityProfiles',
      })
    } finally {
      isLoading.value = false
    }
  }

  const saveQualityProfile = async (profile: QualityProfile) => {
    try {
      if (profile.id) {
        await apiService.updateQualityProfile(profile.id, profile)
      } else {
        await apiService.createQualityProfile(profile)
      }
      await loadQualityProfiles() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'saveQualityProfile',
        metadata: { profileId: profile.id },
      })
      throw error
    }
  }

  const deleteQualityProfile = async (id: number) => {
    try {
      await apiService.deleteQualityProfile(id)
      await loadQualityProfiles() // Refresh the list
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'ConfigurationStore',
        operation: 'deleteQualityProfile',
        metadata: { profileId: id },
      })
      throw error
    }
  }

  return {
    apiConfigurations,
    downloadClientConfigurations,
    applicationSettings,
    qualityProfiles,
    isLoading,
    loadApiConfigurations,
    saveApiConfiguration,
    deleteApiConfiguration,
    loadDownloadClientConfigurations,
    saveDownloadClientConfiguration,
    deleteDownloadClientConfiguration,
    loadApplicationSettings,
    saveApplicationSettings,
    loadQualityProfiles,
    saveQualityProfile,
    deleteQualityProfile,
  }
})
