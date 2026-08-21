namespace Listenarr.Application.Downloads.Contracts;

public enum FilePublicationExecutionMode
{
    Durable = 0,
    AdditiveCopyRetainSource = 1,
    Blocked = 2
}

public enum FilePublicationSourceDisposition
{
    NotApplicable = 0,
    Retained = 1,
    Retired = 2,
    Unchanged = 3
}

public sealed record FilePublicationPlan(
    FileAction RequestedAction,
    FileAction EffectiveAction,
    FilePublicationExecutionMode Mode,
    FilePublicationSourceDisposition SourceDisposition,
    string? ReasonCode = null,
    string? Message = null)
{
    public bool IsAllowed => Mode != FilePublicationExecutionMode.Blocked;

    public static FilePublicationPlan Durable(FileAction action) =>
        new(
            action,
            action,
            FilePublicationExecutionMode.Durable,
            action == FileAction.Move
                ? FilePublicationSourceDisposition.Retired
                : FilePublicationSourceDisposition.Unchanged);

    public static FilePublicationPlan Additive(FileAction requestedAction) =>
        new(
            requestedAction,
            FileAction.Copy,
            FilePublicationExecutionMode.AdditiveCopyRetainSource,
            FilePublicationSourceDisposition.Retained,
            "durable_identity_unavailable",
            requestedAction == FileAction.Move
                ? "The destination was copied successfully, but the source was retained because exact source retirement cannot be proven on this storage."
                : "The file was copied using compatibility publication because durable filesystem identity is unavailable.");

    public static FilePublicationPlan Blocked(
        FileAction requestedAction,
        string reasonCode,
        string message) =>
        new(
            requestedAction,
            requestedAction,
            FilePublicationExecutionMode.Blocked,
            FilePublicationSourceDisposition.Unchanged,
            reasonCode,
            message);
}

public interface IFilePublicationCapabilityResolver
{
    Task<FilePublicationPlan> ResolveAsync(
        FileAction requestedAction,
        string source,
        string destination,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken = default);
}
