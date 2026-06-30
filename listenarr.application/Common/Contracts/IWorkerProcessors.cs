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


namespace Listenarr.Application.Common.Contracts
{
    public interface IDownloadMonitorProcessor
    {
        void ScheduleNextClientPoll(DownloadClientConfiguration client, double intervalSeconds);
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IDownloadImportProcessor
    {
        Task ProcessQueueAsync(CancellationToken cancellationToken);
        Task ProcessJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken);
    }

    public interface IDirectDownloadProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
        Task ProcessDownloadAsync(Download download, CancellationToken cancellationToken);
    }

    public interface IMovedDownloadCleanupProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IScanJobProcessor
    {
        Task ProcessJobAsync(ScanJob job, CancellationToken cancellationToken);
    }

    public interface IMoveJobProcessor
    {
        Task ProcessJobAsync(MoveJob job, CancellationToken cancellationToken);
    }

    public interface IAutomaticSearchProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IAuthorMonitoringProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface ISeriesMonitoringProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IMetadataRescanProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IImageCacheCleanupProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);
    }

    public interface IFfmpegInstallProcessor
    {
        Task EnsureInstalledAsync(CancellationToken cancellationToken);
    }

    public interface IUnmatchedScanProcessor
    {
        Task ProcessJobAsync(UnmatchedScanJob job, CancellationToken cancellationToken);
    }

    public interface IQueueMonitorProcessor
    {
        Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken);
    }
}
