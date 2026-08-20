using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

internal static class ListenarrDatabaseMigrationPreflight
{
    internal const string DurableFilesystemRecoveryMigrationId =
        "20260810160602_AddDurableFilesystemRecovery";
    internal const string RootFoldersMigrationId =
        "20260101172733_AddRootFolders";
    internal const string AudiobookAddedDateMigrationId =
        "20260820015101_AddAudiobookAddedDate";

    public static ListenarrDatabaseMigrationPreflightResult RepairLegacyData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var normalizeDefaultRoots =
            applied.Contains(RootFoldersMigrationId)
            && !applied.Contains(DurableFilesystemRecoveryMigrationId);
        if (!normalizeDefaultRoots)
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        var defaultRootsNormalized = context.Database.ExecuteSqlRaw(
            """
            UPDATE "RootFolders"
            SET "IsDefault" = 0
            WHERE "IsDefault" = 1
              AND "Id" <> (
                  SELECT MIN("Id")
                  FROM "RootFolders"
                  WHERE "IsDefault" = 1
              );
            """);
        transaction.Commit();

        return new ListenarrDatabaseMigrationPreflightResult(defaultRootsNormalized);
    }

    public static ListenarrDatabasePostMigrationRepairResult RepairPostMigrationData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        if (!applied.Contains(DurableFilesystemRecoveryMigrationId))
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        var moveJobsRepaired = context.Database.ExecuteSqlRaw(
            """
            UPDATE "MoveJobs"
            SET
                "Status" = 'NeedsAttention',
                "Error" = 'This move job was created by a pre-durable released version and cannot be resumed safely after upgrade.',
                "FailureKind" = 'Verification',
                "ActiveDeduplicationKey" = NULL,
                "UpdatedAt" = CURRENT_TIMESTAMP
            WHERE "ExecutionProtocolVersion" = 0
              AND "Status" NOT IN ('Completed', 'Failed')
              AND (
                  "Status" <> 'NeedsAttention'
                  OR "ActiveDeduplicationKey" IS NOT NULL
                  OR "FailureKind" <> 'Verification'
                  OR "Error" IS NULL
              );
            """);

        var audiobookAddedDatesBackfilled = 0;
        if (applied.Contains(AudiobookAddedDateMigrationId))
        {
            audiobookAddedDatesBackfilled = context.Database.ExecuteSqlRaw(
                """
                WITH "Evidence" AS (
                    SELECT
                        "AudiobookId",
                        MIN("Timestamp") AS "ObservedAt",
                        0 AS "Priority"
                    FROM "History"
                    WHERE "EventType" = 'Added'
                      AND "AudiobookId" IS NOT NULL
                    GROUP BY "AudiobookId"

                    UNION ALL

                    SELECT
                        "AudiobookId",
                        MIN("CreatedAt") AS "ObservedAt",
                        1 AS "Priority"
                    FROM "AudiobookFiles"
                    GROUP BY "AudiobookId"
                ),
                "BestEvidence" AS (
                    SELECT
                        "AudiobookId",
                        COALESCE(
                            MIN(CASE WHEN "Priority" = 0 THEN "ObservedAt" END),
                            MIN(CASE WHEN "Priority" = 1 THEN "ObservedAt" END)
                        ) AS "ObservedAt"
                    FROM "Evidence"
                    GROUP BY "AudiobookId"
                )
                UPDATE "Audiobooks"
                SET "Added" = (
                    SELECT "BestEvidence"."ObservedAt"
                    FROM "BestEvidence"
                    WHERE "BestEvidence"."AudiobookId" = "Audiobooks"."Id"
                )
                WHERE "Added" IS NULL
                  AND "Id" IN (SELECT "AudiobookId" FROM "BestEvidence");
                """);
        }
        transaction.Commit();

        return new ListenarrDatabasePostMigrationRepairResult(
            moveJobsRepaired,
            audiobookAddedDatesBackfilled);
    }
}

internal readonly record struct ListenarrDatabaseMigrationPreflightResult(
    int DefaultRootsNormalized);

internal readonly record struct ListenarrDatabasePostMigrationRepairResult(
    int MoveJobsRepaired,
    int AudiobookAddedDatesBackfilled);
