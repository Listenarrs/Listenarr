using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Listenarr.Domain.Models;

namespace Listenarr.Infrastructure.Models
{
    public class ListenArrDbContext : DbContext
    {
        public DbSet<Audiobook> Audiobooks { get; set; }
        public DbSet<AudiobookExternalIdentifier> AudiobookExternalIdentifiers { get; set; }
        public DbSet<AudiobookFile> AudiobookFiles { get; set; }
        public DbSet<MoveJob> MoveJobs { get; set; }
        public DbSet<ApplicationSettings> ApplicationSettings { get; set; }
        public DbSet<History> History { get; set; }
        public DbSet<Indexer> Indexers { get; set; }
        public DbSet<ApiConfiguration> ApiConfigurations { get; set; }
        public DbSet<DownloadClientConfiguration> DownloadClientConfigurations { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Download> Downloads { get; set; }
        public DbSet<DownloadProcessingJob> DownloadProcessingJobs { get; set; }
        public DbSet<DownloadHistory> DownloadHistories { get; set; }
        public DbSet<QualityProfile> QualityProfiles { get; set; }
        public DbSet<RemotePathMapping> RemotePathMappings { get; set; }
        public DbSet<ProcessExecutionLog> ProcessExecutionLogs { get; set; }
        public DbSet<RootFolder> RootFolders { get; set; }

        public ListenArrDbContext(DbContextOptions<ListenArrDbContext> options)
            : base(options)
        {
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

            modelBuilder.Entity<History>().HasIndex(h => h.Timestamp);

            modelBuilder.Entity<MoveJob>().HasIndex(m => new { m.AudiobookId, m.Status });

            // RootFolders table configuration
            modelBuilder.ApplyConfiguration(new Configurations.RootFolderConfiguration());
        }
    }
}
