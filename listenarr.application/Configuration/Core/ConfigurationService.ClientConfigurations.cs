/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Configuration.Core
{
    public partial class ConfigurationService
    {
        public async Task<List<ApiConfiguration>> GetApiConfigurationsAsync()
        {
            try
            {
                return await apiConfigRepository.GetAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error loading API configurations from database");
                return new List<ApiConfiguration>();
            }
        }

        public async Task<ApiConfiguration?> GetApiConfigurationAsync(string id)
        {
            try
            {
                return await apiConfigRepository.GetByIdAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error loading API configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveApiConfigurationAsync(ApiConfiguration config)
        {
            try
            {
                var saved = await apiConfigRepository.SaveAsync(config);
                return saved.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving API configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteApiConfigurationAsync(string id)
        {
            try
            {
                return await apiConfigRepository.DeleteAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error deleting API configuration from database");
                return false;
            }
        }

        public async Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync()
        {
            try
            {
                return await downloadClientRepository.GetAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error loading download client configurations from database");
                return new List<DownloadClientConfiguration>();
            }
        }

        public async Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id)
        {
            try
            {
                return await downloadClientRepository.GetByIdAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error loading download client configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config)
        {
            try
            {
                var saved = await downloadClientRepository.SaveAsync(config);
                return saved.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving download client configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteDownloadClientConfigurationAsync(string id)
        {
            try
            {
                return await downloadClientRepository.DeleteAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error deleting download client configuration from database");
                return false;
            }
        }
    }
}
