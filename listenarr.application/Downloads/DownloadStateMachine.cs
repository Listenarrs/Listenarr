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

using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Stage 5: Explicit state machine for download status transitions
    /// 
    /// State Flow:
    /// 1. Grabbed (initial) → Queued (client accepts)
    /// 2. Queued → Downloading (client starts download)
    /// 3. Downloading → Completed (download finishes)
    /// 4. Completed → Check (validation phase - Stage 6)
    /// 5. Check → Import (processing phase - Stage 6)
    /// 6. Import → Verify (final verification - Stage 6)
    /// 7. Verify → Imported (terminal success state)
    /// 
    /// Error states:
    /// - Any state → Failed (download/import error)
    /// - Any state → Warning (recoverable issue)
    /// 
    /// Special states:
    /// - Paused (user action, can resume)
    /// - Checking (torrent checking)
    /// - Removed (cleanup, terminal state)
    /// </summary>
    public class DownloadStateMachine(
        ILogger<DownloadStateMachine> logger,
        IDownloadHistoryRepository historyRepository)
    {
        // Valid state transitions
        private static readonly Dictionary<DownloadItemStatus, HashSet<DownloadItemStatus>> ValidTransitions = new()
        {
            // From Queued
            [DownloadItemStatus.Queued] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Downloading,
                DownloadItemStatus.Paused,
                DownloadItemStatus.Failed,
                DownloadItemStatus.Warning
            },

            // From Downloading
            [DownloadItemStatus.Downloading] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Downloading, // Progress updates
                DownloadItemStatus.Completed,
                DownloadItemStatus.Paused,
                DownloadItemStatus.Failed,
                DownloadItemStatus.Warning
            },

            // From Paused
            [DownloadItemStatus.Paused] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Downloading, // Resume
                DownloadItemStatus.Queued,
                DownloadItemStatus.Failed
            },

            // From Completed
            [DownloadItemStatus.Completed] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Completed, // Can stay completed while importing
                DownloadItemStatus.Failed,
                DownloadItemStatus.Warning
            },

            // From Failed
            [DownloadItemStatus.Failed] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Queued, // Retry
                DownloadItemStatus.Failed  // Can stay failed
            },

            // From Warning
            [DownloadItemStatus.Warning] = new HashSet<DownloadItemStatus>
            {
                DownloadItemStatus.Downloading, // Resume after warning
                DownloadItemStatus.Queued,
                DownloadItemStatus.Failed
            }
        };

        /// <summary>
        /// Validate and execute a state transition
        /// Returns true if transition is valid and executed, false otherwise
        /// </summary>
        public async Task<bool> TransitionAsync(
            string downloadId,
            DownloadItemStatus currentState,
            DownloadItemStatus newState,
            DownloadHistoryEventType eventType,
            Guid? audiobookId = null,
            string? downloadClient = null,
            string? downloadClientId = null,
            DownloadProtocol protocol = DownloadProtocol.Torrent,
            string? title = null,
            string? outputPath = null,
            string? errorMessage = null,
            Dictionary<string, object>? metadata = null,
            CancellationToken ct = default)
        {
            // Validate transition
            if (!IsValidTransition(currentState, newState))
            {
                logger.LogWarning(
                    "Invalid state transition rejected: {DownloadId} cannot transition from {CurrentState} to {NewState}",
                    downloadId, currentState, newState);
                return false;
            }

            // Log the transition
            logger.LogInformation(
                "State transition: {DownloadId} {CurrentState} → {NewState} (Event: {EventType})",
                downloadId, currentState, newState, eventType);

            // Record in history
            try
            {
                await historyRepository.AddAsync(new DownloadHistory
                {
                    DownloadId = downloadId,
                    EventType = eventType,
                    Status = newState,
                    EventDate = DateTime.UtcNow,
                    AudiobookId = audiobookId,
                    DownloadClient = downloadClient ?? "Unknown",
                    DownloadClientId = downloadClientId ?? string.Empty,
                    Protocol = protocol,
                    Title = title ?? string.Empty,
                    OutputPath = outputPath,
                    ErrorMessage = errorMessage,
                    Data = metadata ?? new Dictionary<string, object>
                    {
                        ["PreviousState"] = currentState.ToString(),
                        ["NewState"] = newState.ToString()
                    }
                }, ct);

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex,
                    "Failed to record state transition for {DownloadId}: {CurrentState} → {NewState}",
                    downloadId, currentState, newState);
                return false;
            }
        }

        /// <summary>
        /// Check if a state transition is valid
        /// </summary>
        public bool IsValidTransition(DownloadItemStatus from, DownloadItemStatus to)
        {
            // Same state is always valid (progress updates)
            if (from == to)
                return true;

            // Check if transition is in the valid transitions map
            if (ValidTransitions.TryGetValue(from, out var allowedStates))
            {
                return allowedStates.Contains(to);
            }

            // If source state not in map, reject transition
            logger.LogWarning("Unknown source state in transition validation: {FromState}", from);
            return false;
        }

        /// <summary>
        /// Get the current state of a download from history
        /// </summary>
        public async Task<DownloadItemStatus?> GetCurrentStateAsync(string downloadId, CancellationToken ct = default)
        {
            var latestEvent = await historyRepository.GetLatestEventAsync(downloadId, ct);
            return latestEvent?.Status;
        }

        /// <summary>
        /// Get all valid next states from a given state
        /// </summary>
        public HashSet<DownloadItemStatus> GetValidNextStates(DownloadItemStatus currentState)
        {
            if (ValidTransitions.TryGetValue(currentState, out var nextStates))
            {
                return nextStates;
            }

            return new HashSet<DownloadItemStatus>();
        }

        /// <summary>
        /// Check if a download has reached a terminal state
        /// </summary>
        public bool IsTerminalState(DownloadItemStatus state)
        {
            // Terminal states: no valid transitions out (except to themselves)
            if (!ValidTransitions.TryGetValue(state, out var transitions))
            {
                return true;
            }

            return (transitions.Count == 1 && transitions.Contains(state));
        }

        /// <summary>
        /// Get state transition history for a download
        /// </summary>
        public async Task<List<(DateTime EventDate, DownloadItemStatus FromState, DownloadItemStatus ToState, DownloadHistoryEventType EventType)>>
            GetTransitionHistoryAsync(string downloadId, CancellationToken ct = default)
        {
            var events = await historyRepository.GetByDownloadIdAsync(downloadId, ct);
            var transitions = new List<(DateTime, DownloadItemStatus, DownloadItemStatus, DownloadHistoryEventType)>();

            DownloadItemStatus? previousState = null;

            foreach (var evt in events.OrderBy(e => e.EventDate))
            {
                if (previousState.HasValue)
                {
                    transitions.Add((evt.EventDate, previousState.Value, evt.Status, evt.EventType));
                }
                previousState = evt.Status;
            }

            return transitions;
        }

        /// <summary>
        /// Validate a complete state transition sequence
        /// Useful for testing and debugging
        /// </summary>
        public bool ValidateTransitionSequence(IEnumerable<DownloadItemStatus> states)
        {
            var stateList = states.ToList();
            if (stateList.Count < 2)
                return true; // Empty or single state is valid

            for (int i = 0; i < stateList.Count - 1; i++)
            {
                if (!IsValidTransition(stateList[i], stateList[i + 1]))
                {
                    logger.LogWarning(
                        "Invalid transition in sequence at position {Position}: {FromState} → {ToState}",
                        i, stateList[i], stateList[i + 1]);
                    return false;
                }
            }

            return true;
        }
    }
}

