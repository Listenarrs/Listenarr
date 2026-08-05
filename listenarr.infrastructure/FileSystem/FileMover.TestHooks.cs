using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Action? BeforeDirectoryMoveAttemptForTest { get; init; }
    internal Func<FileAction, string, string, Task>? BeforeFileSameContentShortcutForTestAsync { get; init; }
    internal Func<FileAction, string, string, Task>? AfterFileEndpointsPinnedForTestAsync { get; init; }
    internal Func<string, string, Task>? AfterFileMoveEndpointsResolvedForTestAsync
    {
        get;
        init;
    }
    internal Func<FileAction, string, string, Task>? AfterFileEntriesPinnedForTestAsync { get; init; }
    internal Func<FileAction, string, string, Task>? AfterPinnedSourceContentCapturedForTestAsync { get; init; }
    internal Func<Task>? BeforePinnedHardlinkCreationForTestAsync { get; init; }
    internal Func<Task>? AfterRegistrationPublicationStatePreparedForTestAsync { get; init; }
    internal Func<Task>? AfterRegistrationPublicationClaimPreparedForTestAsync { get; init; }
    internal Func<Task>? AfterRegistrationDestinationPublishedForTestAsync { get; init; }
    internal Action? AfterRegistrationPublicationClaimRetiredForTest { get; init; }
    internal Action? AfterUncommittedRegistrationDestinationRetiredForTest
    {
        get;
        init;
    }
    internal Func<string, Task>? AfterPreparedMoveSourceDeletedForTestAsync { get; init; }
    internal bool DisableNativeFileRenameForTest { get; init; }
    internal Action<string>? BeforeFileMoveDurabilityBarrierForTest { get; init; }
    internal Action<string>? AfterDirectoryRenameJournalPublishedForTest { get; init; }
    internal Action<string>? BeforeDirectoryRenameJournalRetirementForTest { get; init; }
    internal Func<string, Task>? AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyPublicationForTestAsync { get; init; }
    internal Func<string, Task>? BeforeDirectoryCopyStagingCleanupForTestAsync { get; init; }
    internal Func<string, Task>? AfterCleanupDestinationPinnedForTestAsync { get; init; }
    internal Func<string, Task>? AfterCleanupSourceFileRetiredForTestAsync { get; init; }
    internal Func<string, Task>? BeforeCleanupSourceRecoveryDeleteForTestAsync { get; init; }
    internal Func<string, Task>? AfterCleanupSourceRecoveryDeleteForTestAsync { get; init; }
    internal Action<string>? AfterCleanupQuarantineRetiredForTest { get; init; }
    internal int DirectoryCleanupJournalVersionForTest { get; init; } = 2;
    internal string? FileMoveLockDirectoryForTest { get; init; }
}
