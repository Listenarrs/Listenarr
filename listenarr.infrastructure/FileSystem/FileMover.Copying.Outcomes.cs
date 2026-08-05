namespace Listenarr.Infrastructure.FileSystem;

internal enum IdempotentFileMoveOutcome
{
    NotApplicable,
    Completed,
    SourcePathRecreated
}

internal enum SameContentShortcutOutcome
{
    NotApplicable,
    Completed,
    Blocked
}
