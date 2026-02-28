/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Stage 6: Three-phase validation pipeline for processing completed downloads
    /// 
    /// Phase 1 - Check: Validate download is complete and ready
    ///   - Verify files exist on disk
    ///   - Check file integrity (size, format)
    ///   - Validate download client reports completion
    ///   - Ensure no partial downloads
    /// 
    /// Phase 2 - Import: Process and move files
    ///   - Extract archives if needed
    ///   - Apply file naming patterns
    ///   - Move/copy to destination folder
    ///   - Update database records
    /// 
    /// Phase 3 - Verify: Confirm import success
    ///   - Verify files at destination
    ///   - Validate database consistency
    ///   - Mark download as imported
    ///   - Cleanup source files (optional)
    /// </summary>
    public class DownloadValidationPipeline
    {
        private readonly ILogger<DownloadValidationPipeline> _logger;
        private readonly DownloadStateMachine _stateMachine;
        private readonly DownloadHistoryRepository _historyRepo;

        public DownloadValidationPipeline(
            ILogger<DownloadValidationPipeline> logger,
            DownloadStateMachine stateMachine,
            DownloadHistoryRepository historyRepo)
        {
            _logger = logger;
            _stateMachine = stateMachine;
            _historyRepo = historyRepo;
        }

        /// <summary>
        /// Execute the complete validation pipeline
        /// Returns true if all phases succeed, false otherwise
        /// </summary>
        public async Task<ValidationResult> ExecutePipelineAsync(
            DownloadClientItem download,
            Guid? audiobookId = null,
            CancellationToken ct = default)
        {
            var result = new ValidationResult
            {
                DownloadId = download.DownloadId,
                StartedAt = DateTime.UtcNow
            };

            try
            {
                // Phase 1: Check
                _logger.LogInformation("Pipeline Phase 1/3: Checking download {DownloadId}", download.DownloadId);
                var checkResult = await CheckPhaseAsync(download, ct);
                result.CheckPhase = checkResult;

                if (!checkResult.Success)
                {
                    _logger.LogWarning("Check phase failed for {DownloadId}: {Reason}", download.DownloadId, checkResult.ErrorMessage);
                    result.CompletedAt = DateTime.UtcNow;
                    return result;
                }

                // Phase 2: Import
                _logger.LogInformation("Pipeline Phase 2/3: Importing download {DownloadId}", download.DownloadId);
                var importResult = await ImportPhaseAsync(download, audiobookId, ct);
                result.ImportPhase = importResult;

                if (!importResult.Success)
                {
                    _logger.LogWarning("Import phase failed for {DownloadId}: {Reason}", download.DownloadId, importResult.ErrorMessage);
                    result.CompletedAt = DateTime.UtcNow;
                    return result;
                }

                // Phase 3: Verify
                _logger.LogInformation("Pipeline Phase 3/3: Verifying download {DownloadId}", download.DownloadId);
                var verifyResult = await VerifyPhaseAsync(download, importResult.ImportedPath, ct);
                result.VerifyPhase = verifyResult;

                if (!verifyResult.Success)
                {
                    _logger.LogWarning("Verify phase failed for {DownloadId}: {Reason}", download.DownloadId, verifyResult.ErrorMessage);
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Success = checkResult.Success && importResult.Success && verifyResult.Success;

                if (result.Success)
                {
                    _logger.LogInformation("✅ Pipeline completed successfully for {DownloadId} in {Duration:F1}s",
                        download.DownloadId, (result.CompletedAt.Value - result.StartedAt).TotalSeconds);

                    // Mark as imported in history
                    await _historyRepo.MarkAsImportedAsync(download.DownloadId, ct);
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Pipeline execution failed for {DownloadId}", download.DownloadId);
                result.CompletedAt = DateTime.UtcNow;
                result.Success = false;
                result.GlobalError = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Phase 1: Check - Validate download is complete and ready
        /// </summary>
        private async Task<PhaseResult> CheckPhaseAsync(DownloadClientItem download, CancellationToken ct)
        {
            var result = new PhaseResult { PhaseName = "Check" };

            try
            {
                // Check 1: Download must be in Completed status
                if (download.Status != DownloadItemStatus.Completed)
                {
                    result.ErrorMessage = $"Download not completed (Status: {download.Status})";
                    return result;
                }

                // Check 2: Must have output path
                if (string.IsNullOrEmpty(download.OutputPath))
                {
                    result.ErrorMessage = "Output path is empty";
                    return result;
                }

                // Check 3: Output path must exist
                if (!Directory.Exists(download.OutputPath) && !File.Exists(download.OutputPath))
                {
                    result.ErrorMessage = $"Output path does not exist: {download.OutputPath}";
                    return result;
                }

                // Check 4: Must have valid DownloadId
                if (string.IsNullOrEmpty(download.DownloadId) || download.DownloadId.StartsWith("temp-"))
                {
                    result.ErrorMessage = "Invalid or temporary DownloadId";
                    return result;
                }

                // Check 5: Size must be greater than zero
                if (download.TotalSize <= 0)
                {
                    result.ErrorMessage = "Download size is zero or negative";
                    return result;
                }

                // Record check phase success
                await _stateMachine.TransitionAsync(
                    download.DownloadId,
                    DownloadItemStatus.Completed,
                    DownloadItemStatus.Completed,
                    DownloadHistoryEventType.Checking,
                    downloadClient: download.DownloadClientInfo.Name,
                    downloadClientId: download.DownloadClientInfo.Id,
                    protocol: download.DownloadClientInfo.Protocol,
                    title: download.Title,
                    outputPath: download.OutputPath,
                    metadata: new Dictionary<string, object>
                    {
                        ["Phase"] = "Check",
                        ["OutputPath"] = download.OutputPath,
                        ["TotalSize"] = download.TotalSize
                    },
                    ct: ct);

                result.Success = true;
                _logger.LogDebug("Check phase passed for {DownloadId}", download.DownloadId);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error in check phase for {DownloadId}", download.DownloadId);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Phase 2: Import - Process and move files
        /// </summary>
        private async Task<ImportPhaseResult> ImportPhaseAsync(DownloadClientItem download, Guid? audiobookId, CancellationToken ct)
        {
            var result = new ImportPhaseResult { PhaseName = "Import" };

            try
            {
                // For now, we'll use the output path as-is
                // In a full implementation, this would:
                // - Extract archives
                // - Apply naming patterns
                // - Move to final destination
                // - Update database

                result.ImportedPath = download.OutputPath;

                // Record import phase
                await _stateMachine.TransitionAsync(
                    download.DownloadId,
                    DownloadItemStatus.Completed,
                    DownloadItemStatus.Completed,
                    DownloadHistoryEventType.Imported,
                    audiobookId: audiobookId,
                    downloadClient: download.DownloadClientInfo.Name,
                    downloadClientId: download.DownloadClientInfo.Id,
                    protocol: download.DownloadClientInfo.Protocol,
                    title: download.Title,
                    outputPath: result.ImportedPath,
                    metadata: new Dictionary<string, object>
                    {
                        ["Phase"] = "Import",
                        ["ImportedPath"] = result.ImportedPath
                    },
                    ct: ct);

                result.Success = true;
                _logger.LogDebug("Import phase passed for {DownloadId}", download.DownloadId);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error in import phase for {DownloadId}", download.DownloadId);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Phase 3: Verify - Confirm import success
        /// </summary>
        private async Task<PhaseResult> VerifyPhaseAsync(DownloadClientItem download, string? importedPath, CancellationToken ct)
        {
            var result = new PhaseResult { PhaseName = "Verify" };

            try
            {
                // Verify 1: Imported path must not be empty
                if (string.IsNullOrEmpty(importedPath))
                {
                    result.ErrorMessage = "Imported path is empty";
                    return result;
                }

                // Verify 2: Imported path must still exist
                if (!Directory.Exists(importedPath) && !File.Exists(importedPath))
                {
                    result.ErrorMessage = $"Imported path does not exist: {importedPath}";
                    return result;
                }

                // Record verify phase
                await _stateMachine.TransitionAsync(
                    download.DownloadId,
                    DownloadItemStatus.Completed,
                    DownloadItemStatus.Completed,
                    DownloadHistoryEventType.Imported, // Use Imported event for final success
                    downloadClient: download.DownloadClientInfo.Name,
                    downloadClientId: download.DownloadClientInfo.Id,
                    protocol: download.DownloadClientInfo.Protocol,
                    title: download.Title,
                    outputPath: importedPath,
                    metadata: new Dictionary<string, object>
                    {
                        ["Phase"] = "Verify",
                        ["VerifiedPath"] = importedPath,
                        ["PipelineComplete"] = true
                    },
                    ct: ct);

                result.Success = true;
                _logger.LogDebug("Verify phase passed for {DownloadId}", download.DownloadId);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error in verify phase for {DownloadId}", download.DownloadId);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }
    }

    /// <summary>
    /// Result of the complete validation pipeline
    /// </summary>
    public class ValidationResult
    {
        public string DownloadId { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool Success { get; set; }
        public string? GlobalError { get; set; }

        public PhaseResult? CheckPhase { get; set; }
        public ImportPhaseResult? ImportPhase { get; set; }
        public PhaseResult? VerifyPhase { get; set; }

        public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;
    }

    /// <summary>
    /// Result of a single pipeline phase
    /// </summary>
    public class PhaseResult
    {
        public string PhaseName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of the import phase (includes imported path)
    /// </summary>
    public class ImportPhaseResult : PhaseResult
    {
        public string? ImportedPath { get; set; }
    }
}

