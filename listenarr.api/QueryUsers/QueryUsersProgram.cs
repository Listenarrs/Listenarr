// See https://aka.ms/new-console-template for more information
using Microsoft.Data.Sqlite;
using Serilog;

static class QueryUsersProgram
{
    // Utility method for querying users. Renamed from Main to avoid emitting a second
    // program entry point when the web project uses top-level statements.
    public static void Run()
    {
        EnsureSerilog();

        static void EnsureSerilog()
        {
            try
            {
                var loggerType = Log.Logger?.GetType().Name;
                if (string.Equals(loggerType, "SilentLogger", StringComparison.OrdinalIgnoreCase) || Log.Logger == null)
                {
                    Log.Logger = new LoggerConfiguration()
                        .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                        .CreateLogger();
                }
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }
        try
        {
            // Path to the application's SQLite database (same as used by the API)
            // The API config uses: Data Source=config/database/listenarr.db
            string dbPath = Path.Join(Directory.GetCurrentDirectory(), "config", "database", "listenarr.db");
            Log.Information("Database path: {DbPath}", dbPath);

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Username, Email, IsAdmin, CreatedAt FROM Users";

            using var reader = command.ExecuteReader();
            Log.Information("\nUsers in database:");
            Log.Information("ID | Username | Email | IsAdmin | CreatedAt");
            Log.Information("---|----------|-------|---------|-----------");

            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var username = reader.GetString(1);
                var email = reader.IsDBNull(2) ? "NULL" : reader.GetString(2);
                var isAdmin = reader.GetBoolean(3);
                var createdAt = reader.GetDateTime(4);

                Log.Information("{Id} | {Username} | {Email} | {IsAdmin} | {CreatedAt}", id, username, email, isAdmin, createdAt);
            }

            if (!reader.HasRows)
            {
                Log.Information("No users found in database.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            Log.Error(ex, "Error querying users database");
        }
    }
}


