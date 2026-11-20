using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

/// <summary>Writes fixtures to the 1.3.4 schema before applying newer migrations.</summary>
internal sealed class Released134SeedDbContext(DbContextOptions<ListenArrDbContext> options)
    : ListenArrDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Audiobook>().Ignore(audiobook => audiobook.Added);
    }
}
