// This partial is compiled only for the test host. It applies small DI patches
// so the WebApplicationFactory used by integration tests has the same persistence
// registrations as the real app (including IDbContextFactory).
public partial class Program
{
    internal static partial void ApplyTestHostPatches(WebApplicationBuilder builder)
    {
        // In test environment, force an isolated temp SQLite DB by default to prevent
        // writes to the developer's real config/database/listenarr.db.
        var sqliteDbPathOverride = builder.Configuration["Listenarr:SqliteDbPath"];
        var sqliteDbPath = string.IsNullOrWhiteSpace(sqliteDbPathOverride)
            ? Path.Join(Path.GetTempPath(), "listenarr-tests", "program-testing", $"listenarr-{Guid.NewGuid():N}.db")
            : Path.GetFullPath(sqliteDbPathOverride, builder.Environment.ContentRootPath);
        var sqliteDbDir = Path.GetDirectoryName(sqliteDbPath);
        if (!string.IsNullOrEmpty(sqliteDbDir) && !Directory.Exists(sqliteDbDir))
        {
            Directory.CreateDirectory(sqliteDbDir);
        }

        // Disable hosted services and enforce the isolated sqlite path before Program.cs
        // computes persistence registrations.
        var inMemory = new Dictionary<string, string?>()
        {
            ["Listenarr:SqliteDbPath"] = sqliteDbPath,
            ["Listenarr:DisableHostedServices"] = "true"
        };
        builder.Configuration.AddInMemoryCollection(inMemory);
    }
}
