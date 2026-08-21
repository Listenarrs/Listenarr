using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfLibraryDirectoryOwnershipStore
{
    public Task EnsureAdditiveHierarchyAsync(
        string destinationDirectory,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedBoundary);
        EnsureResolved(semantics);

        var destination = FileSystemPathIdentity.Canonicalize(
            destinationDirectory,
            semantics.Syntax);
        var boundary = FileSystemPathIdentity.Canonicalize(
            managedBoundary,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                destination,
                boundary,
                semantics))
        {
            throw new InvalidOperationException(
                "The additive directory destination is outside its managed boundary.");
        }

        var hierarchy = new List<string>();
        var currentPath = destination;
        while (!FileSystemPathIdentity.AreEquivalent(
            currentPath,
            boundary,
            semantics))
        {
            hierarchy.Add(currentPath);
            currentPath = Path.GetDirectoryName(currentPath)
                ?? throw new InvalidOperationException(
                    "The additive directory hierarchy escaped its managed boundary.");
        }
        hierarchy.Reverse();

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(boundary);
        try
        {
            foreach (var directory in hierarchy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childName = Path.GetFileName(directory);
                PinnedDirectoryCreation.PinnedDirectoryAnchor next;
                try
                {
                    next = current.OpenExistingChild(childName);
                }
                catch (Win32Exception exception) when (
                    exception.NativeErrorCode is 2 or 3)
                {
                    using var creation = current.TryCreateChild(childName);
                    next = creation.Created
                        ? creation.OpenCreatedDirectoryAnchor()
                        : current.OpenExistingChild(childName);
                }

                if (!next.VisiblePathMatches())
                {
                    next.Dispose();
                    throw new IOException(
                        "An additive directory component changed while it was being pinned.");
                }

                current.Dispose();
                current = next;
            }
        }
        finally
        {
            current.Dispose();
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
        string destinationDirectory,
        string managedBoundary,
        FileSystemPathSemantics semantics,
        string creationWorkflow,
        Guid? creationOperationId = null,
        int? audiobookId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedBoundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(creationWorkflow);
        EnsureResolved(semantics);

        var destination = FileSystemPathIdentity.Canonicalize(
            destinationDirectory,
            semantics.Syntax);
        var boundary = FileSystemPathIdentity.Canonicalize(
            managedBoundary,
            semantics.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(destination, boundary, semantics))
        {
            throw new InvalidOperationException(
                "The directory creation destination is outside its managed boundary.");
        }
        using var authorization = await _boundaryAuthorizer.AuthorizeAsync(
            boundary,
            semantics,
            cancellationToken);

        var hierarchy = new List<string>();
        var current = destination;
        while (!FileSystemPathIdentity.AreEquivalent(current, boundary, semantics))
        {
            hierarchy.Add(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    "The directory creation destination has no parent inside its managed boundary.");
            if (!FileSystemPathIdentity.IsSameOrInside(current, boundary, semantics))
            {
                throw new InvalidOperationException(
                    "The directory creation hierarchy escaped its managed boundary.");
            }
        }
        hierarchy.Reverse();

        var createdOwnerships = new List<LibraryDirectoryOwnership>();
        var currentAnchor = authorization.BoundaryAnchor.Duplicate();
        try
        {
            foreach (var directory in hierarchy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!currentAnchor.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The visible directory hierarchy changed after its boundary was pinned.");
                }

                var childName = Path.GetFileName(directory);
                using var creation = currentAnchor.TryCreateChild(childName);
                PinnedDirectoryCreation.PinnedDirectoryAnchor nextAnchor;
                if (!creation.Created || !creation.CreationGenerationIsProvable)
                {
                    using var existingPublication =
                        currentAnchor.OpenExistingChildForPublication(childName);
                    nextAnchor = existingPublication.OpenCreatedDirectoryAnchor();
                    try
                    {
                        EnsureVisibleAnchor(nextAnchor);
                        var existingResolution = await ResolveOwnedCoreAsync(
                            directory,
                            semantics,
                            validateProof: false,
                            cancellationToken);
                        EnsureVisibleAnchor(nextAnchor);
                        if (existingResolution.State == LibraryDirectoryOwnershipResolutionState.Owned)
                        {
                            await RepairPinnedExistingAsync(
                                new LibraryDirectoryOwnershipClaim(
                                    directory,
                                    semantics,
                                    creationWorkflow,
                                    creationOperationId,
                                    audiobookId),
                                existingPublication,
                                authorization.RootFolderId,
                                cancellationToken);
                            EnsureVisibleAnchor(nextAnchor);
                        }
                        else if (existingResolution.State is
                            LibraryDirectoryOwnershipResolutionState.Conflict
                            or LibraryDirectoryOwnershipResolutionState.Unavailable)
                        {
                            throw new InvalidOperationException(
                                existingResolution.Reason
                                    ?? "Existing directory ownership is conflicting or unavailable.");
                        }
                    }
                    catch
                    {
                        nextAnchor.Dispose();
                        throw;
                    }
                }
                else
                {
                    try
                    {
                        createdOwnerships.Add(await RecordPinnedCreatedAsync(
                            new LibraryDirectoryOwnershipClaim(
                                directory,
                                semantics,
                                creationWorkflow,
                                creationOperationId,
                                audiobookId),
                            creation,
                            authorization.RootFolderId,
                            CancellationToken.None));
                        if (!creation.VisiblePathMatches())
                        {
                            throw new InvalidOperationException(
                                "The visible created directory changed before hierarchy continuation.");
                        }

                        var createdAnchor = creation.OpenCreatedDirectoryAnchor();
                        try
                        {
                            EnsureVisibleAnchor(createdAnchor);
                            nextAnchor = createdAnchor;
                        }
                        catch
                        {
                            createdAnchor.Dispose();
                            throw;
                        }
                    }
                    catch (Exception exception) when (exception is not (
                        OutOfMemoryException or StackOverflowException))
                    {
                        // Path-based compensation is allowed only while the visible path still
                        // identifies the pinned generation. A replaced pathname is preserved.
                        if (creation.VisiblePathMatches())
                        {
                            await TryCompensateFailedExclusiveCreationAsync(
                                creation,
                                semantics);
                        }
                        throw;
                    }
                }

                currentAnchor.Dispose();
                currentAnchor = nextAnchor;
            }

            return createdOwnerships;
        }
        finally
        {
            currentAnchor.Dispose();
        }
    }

    private static void EnsureVisibleAnchor(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor)
    {
        if (!anchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The visible directory hierarchy changed while a component was pinned.");
        }
    }

    private async Task TryCompensateFailedExclusiveCreationAsync(
        PinnedDirectoryCreation creation,
        FileSystemPathSemantics semantics)
    {
        try
        {
            var directory = creation.FullPath;
            var resolution = await ResolveOwnedAsync(
                directory,
                semantics,
                CancellationToken.None);
            if (resolution.State != LibraryDirectoryOwnershipResolutionState.Unowned
                || !creation.VisiblePathMatches())
            {
                return;
            }

            using var createdAnchor = creation.OpenCreatedDirectoryAnchor();
            if (Directory.EnumerateFileSystemEntries(createdAnchor.FullPath).Any()
                || !createdAnchor.VisiblePathMatches()
                || !creation.VisiblePathMatches())
            {
                return;
            }

            creation.DeletePinnedEmptyDirectoryImmediately(
                Path.GetFileName(directory));
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            // Compensation is deliberately best effort and fail closed. The original
            // ownership failure remains authoritative, and any uncertain or changed path
            // is preserved rather than recursively cleaned or adopted on retry.
        }
    }

}
