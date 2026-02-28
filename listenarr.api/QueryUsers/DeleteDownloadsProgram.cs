using System;
using Serilog;
using Microsoft.Data.Sqlite;
using System.IO;

static class DeleteDownloadsProgram
{
    // Helper to delete all rows in the Downloads table. Use DeleteDownloadsProgram.Run()
    // from a separate tool if you need to execute this logic. This avoids emitting
    // a program entry point (Main) when the web project uses top-level statements.
    public static int Run()
    {
        try
        {
            string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "config", "database", "listenarr.db");
            if (!File.Exists(dbPath))
            {
                Log.Logger.Warning("Database file not found at: {DbPath}", dbPath);
                return 2;
            }

            Log.Logger.Information("Opening database: {DbPath}", dbPath);

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using var tx = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM \"Downloads\";";
                cmd.ExecuteNonQuery();
                Log.Logger.Information("Deleted rows from Downloads table.");
            }

            // Reset sqlite_sequence for Downloads if present
            using (var cmd2 = connection.CreateCommand())
            {
                cmd2.CommandText = "DELETE FROM sqlite_sequence WHERE name='Downloads';";
                try
                {
                    cmd2.ExecuteNonQuery();
                    Log.Logger.Information("Reset sqlite_sequence for Downloads (if it existed).");
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            tx.Commit();
            connection.Close();
            Log.Logger.Information("Done.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            Log.Logger.Error(ex, "Error while deleting downloads");
            return 1;
        }
    }
}

