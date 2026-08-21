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
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Persistence.Configurations;

namespace Listenarr.Infrastructure.Persistence
{
    public class ListenArrDbContext : DbContext
    {
        public DbSet<Audiobook> Audiobooks { get; set; } = null!;
        public DbSet<AudiobookSeriesMembership> AudiobookSeriesMemberships { get; set; } = null!;
        public DbSet<AudiobookExternalIdentifier> AudiobookExternalIdentifiers { get; set; } = null!;
        public DbSet<AudiobookFile> AudiobookFiles { get; set; } = null!;
        public DbSet<AudiobookDeletionIntent> AudiobookDeletionIntents { get; set; } = null!;
        public DbSet<MoveJob> MoveJobs { get; set; } = null!;
        public DbSet<MoveJobEntry> MoveJobEntries { get; set; } = null!;
        public DbSet<MoveJobCreatedDirectory> MoveJobCreatedDirectories { get; set; } = null!;
        public DbSet<LibraryDirectoryOwnership> LibraryDirectoryOwnerships { get; set; } = null!;
        public DbSet<LibraryDirectoryOwnershipPathMigration> LibraryDirectoryOwnershipPathMigrations { get; set; } = null!;
        public DbSet<MoveScanHandoff> MoveScanHandoffs { get; set; } = null!;
        public DbSet<ApplicationSettings> ApplicationSettings { get; set; } = null!;
        public DbSet<History> History { get; set; } = null!;
        public DbSet<Indexer> Indexers { get; set; } = null!;
        public DbSet<ApiConfiguration> ApiConfigurations { get; set; } = null!;
        public DbSet<DownloadClientConfiguration> DownloadClientConfigurations { get; set; } = null!;
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Download> Downloads { get; set; } = null!;
        public DbSet<DownloadProcessingJob> DownloadProcessingJobs { get; set; } = null!;
        public DbSet<FileMutationJournal> FileMutationJournals { get; set; } = null!;
        public DbSet<CompatibilityFilePublicationJournal> CompatibilityFilePublicationJournals { get; set; } = null!;
        public DbSet<DownloadHistory> DownloadHistories { get; set; } = null!;
        public DbSet<QualityProfile> QualityProfiles { get; set; } = null!;
        public DbSet<RemotePathMapping> RemotePathMappings { get; set; } = null!;
        public DbSet<ProcessExecutionLog> ProcessExecutionLogs { get; set; } = null!;
        public DbSet<RootFolder> RootFolders { get; set; } = null!;
        public DbSet<RootFolderRelocation> RootFolderRelocations { get; set; } = null!;
        public DbSet<RootFolderRelocationSkippedItem> RootFolderRelocationSkippedItems { get; set; } = null!;
        public DbSet<RootFolderRelocationCreatedDirectory> RootFolderRelocationCreatedDirectories { get; set; } = null!;
        public DbSet<MonitoredAuthor> MonitoredAuthors { get; set; } = null!;
        public DbSet<MonitoredSeries> MonitoredSeries { get; set; } = null!;
        public DbSet<AuthorCacheEntry> AuthorCacheEntries { get; set; } = null!;
        public DbSet<SeriesCacheEntry> SeriesCacheEntries { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;

        public ListenArrDbContext(DbContextOptions<ListenArrDbContext> options)
            : base(options)
        {
        }

        public override int SaveChanges()
        {
            try
            {
                return base.SaveChanges();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                throw TranslatePersistenceException(ex);
            }
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await base.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                throw TranslatePersistenceException(ex);
            }
        }

        private static PersistenceException TranslatePersistenceException(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            if (message.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new UniqueConstraintViolationException("A unique persistence constraint was violated.", ex);
            }

            return new PersistenceException("A persistence operation failed.", ex);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Only configure SQLite if no provider was configured externally (e.g. tests using InMemory)
            if (!optionsBuilder.IsConfigured)
            {
                // Apply PRAGMA settings when connection is configured
                optionsBuilder.UseSqlite(options =>
                {
                    options.CommandTimeout(60);
                });
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Apply configuration classes from this assembly to keep the DbContext small and focused.
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(ListenArrDbContext).Assembly);

            // Register commonly used indexes here for safety; prefer moving indexes into configuration classes.
            modelBuilder.Entity<Download>().HasIndex(d => d.Status);
            modelBuilder.Entity<Download>().HasIndex(d => d.DownloadClientId);
            modelBuilder.Entity<Download>().HasIndex(d => d.CompletedAt);

            modelBuilder.Entity<DownloadHistory>().HasIndex(dh => dh.DownloadId);
            modelBuilder.Entity<DownloadHistory>().HasIndex(dh => dh.EventDate);
            modelBuilder.Entity<DownloadHistory>().HasIndex(dh => dh.AudiobookId);
            modelBuilder.Entity<DownloadHistory>().HasIndex(dh => new { dh.DownloadId, dh.EventType });

            modelBuilder.Entity<DownloadProcessingJob>().HasIndex(j => new { j.DownloadId, j.Status });
            modelBuilder.Entity<DownloadProcessingJob>().HasIndex(j => j.Status);

            modelBuilder.Entity<Audiobook>().HasIndex(a => a.Monitored);
            modelBuilder.Entity<Audiobook>().HasIndex(a => a.LastSearchTime);
            modelBuilder.Entity<MonitoredAuthor>().HasIndex(a => new { a.AuthorNameNormalized, a.Region, a.Language }).IsUnique();
            modelBuilder.Entity<MonitoredAuthor>().HasIndex(a => a.LastCheckedAt);
            modelBuilder.Entity<MonitoredSeries>().HasIndex(s => new { s.SeriesNameNormalized, s.Region, s.Language }).IsUnique();
            modelBuilder.Entity<MonitoredSeries>().HasIndex(s => s.LastCheckedAt);
            modelBuilder.Entity<AuthorCacheEntry>().HasIndex(a => new { a.AuthorNameNormalized, a.Region }).IsUnique();
            modelBuilder.Entity<AuthorCacheEntry>().HasIndex(a => new { a.AuthorAsin, a.Region });
            modelBuilder.Entity<SeriesCacheEntry>().HasIndex(s => new { s.SeriesNameNormalized, s.Region }).IsUnique();
            modelBuilder.Entity<SeriesCacheEntry>().HasIndex(s => new { s.SeriesAsin, s.Region });

            modelBuilder.Entity<History>().HasIndex(h => h.Timestamp);
            modelBuilder.Entity<History>().HasIndex(h => h.EventType);
            modelBuilder.Entity<History>().HasIndex(h => h.Outcome);
            modelBuilder.Entity<History>().HasIndex(h => h.AudiobookExternalId);
            modelBuilder.Entity<History>().HasIndex(h => h.DownloadId);
            modelBuilder.Entity<History>().HasIndex(h => h.DownloadClientId);
            modelBuilder.Entity<History>().HasIndex(h => h.CorrelationId);
            modelBuilder.Entity<History>().HasIndex(h => h.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");

            modelBuilder.Entity<MoveJob>().HasIndex(m => new { m.AudiobookId, m.Status });

            modelBuilder.Entity<UserSession>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Username).IsRequired().HasMaxLength(256);
                entity.Property(s => s.TokenHash).IsRequired().HasMaxLength(128);
                entity.Property(s => s.CreatedAt).IsRequired();
                entity.Property(s => s.ExpiresAt).IsRequired();
                entity.Property(s => s.LastAccessed).IsRequired();

                entity.HasIndex(s => s.TokenHash).IsUnique();
                entity.HasIndex(s => s.Username);
                entity.HasIndex(s => s.ExpiresAt);
            });

            // RootFolders table configuration
            modelBuilder.ApplyConfiguration(new RootFolderConfiguration());
        }
    }
}
