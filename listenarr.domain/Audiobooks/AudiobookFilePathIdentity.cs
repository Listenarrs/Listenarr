using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public sealed record AudiobookFilePathState(
    string? StoredPath,
    string? CanonicalPath,
    FileSystemPathSyntax? Syntax,
    FileSystemCaseSensitivity CaseSensitivity,
    FileSystemCaseSensitivityMode RequestedMode,
    string? BoundaryPath,
    string? LookupKey,
    string? OwnershipKey,
    int Version,
    PathIdentityState State,
    string? Reason);

public sealed record AudiobookFilePathIdentity(
    string CanonicalPath,
    FileSystemPathSyntax Syntax,
    FileSystemCaseSensitivity CaseSensitivity,
    FileSystemCaseSensitivityMode RequestedMode,
    string BoundaryPath,
    string LookupKey,
    string? OwnershipKey,
    int Version,
    PathIdentityState State,
    string? Reason = null)
{
    public const int CurrentVersion = 1;
    public const string IdentityScope = "audiobook-file";

    public static AudiobookFilePathIdentity CreateValid(
        string absolutePath,
        FileSystemPathSemantics semantics,
        FileSystemCaseSensitivityMode requestedMode,
        string boundaryPath)
    {
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            absolutePath,
            semantics.Syntax);
        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
            boundaryPath,
            semantics.Syntax);
        var lookupKey = FileSystemPathIdentity.CreateLookupKey(
            IdentityScope,
            canonicalPath,
            semantics.Syntax,
            CurrentVersion);
        var identity = new AudiobookFilePathIdentity(
            canonicalPath,
            semantics.Syntax,
            semantics.CaseSensitivity,
            requestedMode,
            canonicalBoundary,
            lookupKey,
            FileSystemPathIdentity.CreateKey(
                IdentityScope,
                canonicalPath,
                semantics,
                CurrentVersion),
            CurrentVersion,
            PathIdentityState.Valid);
        identity.Validate();
        return identity;
    }

    public static AudiobookFilePathIdentity CreateUnavailable(
        string absolutePath,
        FileSystemPathSyntax syntax,
        FileSystemCaseSensitivityMode requestedMode,
        string boundaryPath,
        string reason)
    {
        var canonicalPath = FileSystemPathIdentity.Canonicalize(absolutePath, syntax);
        return new AudiobookFilePathIdentity(
            canonicalPath,
            syntax,
            FileSystemCaseSensitivity.Unknown,
            requestedMode,
            FileSystemPathIdentity.Canonicalize(boundaryPath, syntax),
            FileSystemPathIdentity.CreateLookupKey(
                IdentityScope,
                canonicalPath,
                syntax,
                CurrentVersion),
            null,
            CurrentVersion,
            PathIdentityState.Unavailable,
            reason);
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CanonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(BoundaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(LookupKey);
        if (Version < 1)
        {
            throw new InvalidOperationException("Audiobook file path identity version must be positive.");
        }

        var snapshot = new PathIdentitySnapshot(
            Syntax,
            CaseSensitivity,
            RequestedMode,
            BoundaryPath);

        if (State == PathIdentityState.Valid)
        {
            if (CaseSensitivity == FileSystemCaseSensitivity.Unknown)
            {
                throw new InvalidOperationException(
                    "A valid audiobook file identity requires resolved filesystem case sensitivity.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(OwnershipKey);
            snapshot.ValidateForPath(CanonicalPath);
            return;
        }

        if (OwnershipKey != null)
        {
            throw new InvalidOperationException(
                "Only valid audiobook file identities may carry a database ownership key.");
        }
    }
}
