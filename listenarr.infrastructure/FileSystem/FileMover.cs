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
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    internal enum FileMutationOutcome
    {
        Success,
        Skipped,
        Blocked,
        Failed
    }

    internal sealed record FileMutationResult(
        FileMutationOutcome Outcome,
        FileAction Action,
        string SourcePath,
        string? DestinationPath,
        string? Reason = null);

    public partial class FileMover : IFileMover
    {
        private readonly ILogger<FileMover> _logger;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IFileMutationJournalStore? _fileMutationJournalStore;
        private readonly IApplicationPathService _applicationPathService;
        private readonly Func<string, bool?> _readOnlyFileSystemProbe;
        private readonly IRootFolderRepository? _rootFolderRepository;
        private readonly IRootFolderStorageHealthResolver? _rootFolderStorageHealthResolver;

        public FileMover(
            ILogger<FileMover> logger,
            IProcessRunner? processRunner = null,
            IOptions<FileMoverOptions>? options = null,
            IFileSystemSemanticsResolver? semanticsResolver = null,
            IDbContextFactory<ListenArrDbContext>? dbContextFactory = null,
            TimeProvider? timeProvider = null,
            IApplicationPathService? applicationPathService = null,
            Func<string, bool?>? readOnlyFileSystemProbe = null,
            IRootFolderRepository? rootFolderRepository = null,
            IRootFolderStorageHealthResolver? rootFolderStorageHealthResolver = null)
        {
            _logger = logger;
            _ = processRunner;
            _ = options;
            _semanticsResolver = semanticsResolver ?? new FileSystemSemanticsResolver();
            _applicationPathService = applicationPathService
                ?? new ApplicationPathService(AppContext.BaseDirectory);
            _readOnlyFileSystemProbe = readOnlyFileSystemProbe
                ?? FileSystemMutationCapabilityProbe.ProbeReadOnlyDirectory;
            _rootFolderRepository = rootFolderRepository;
            _rootFolderStorageHealthResolver = rootFolderStorageHealthResolver;
            _fileMutationJournalStore = dbContextFactory == null
                ? null
                : new EfFileMutationJournalStore(
                    dbContextFactory,
                    timeProvider ?? TimeProvider.System,
                    _semanticsResolver);
        }

    }
}
