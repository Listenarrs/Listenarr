/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Downloads.Submission;

internal sealed class DirectDownloadSubmissionResolver(
    IIndexerRepository indexerRepository,
    IEnumerable<IDirectDownloadSourcePolicy> sourcePolicies) : IDownloadSourceResolver
{
    private const int MaxArtifactCount = 500;

    public int Priority => 0;

    public bool CanResolve(TrustedDownloadCandidate candidate)
        => candidate.SourceDescriptor.Protocol == DownloadProtocol.DirectDownload;

    public async Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken)
    {
        var locators = candidate.SourceDescriptor.Locators
            .Where(value => value.Kind == DownloadSourceLocatorKind.DirectUrl)
            .ToList();
        if (locators.Count == 0 || locators.Count > MaxArtifactCount)
        {
            throw new DownloadClientSubmissionException(
                $"The direct-download artifact plan must contain between 1 and {MaxArtifactCount} files.");
        }

        var artifacts = new List<(DownloadSourceLocator Locator, Uri Uri)>();
        foreach (var locator in locators)
        {
            if (string.IsNullOrWhiteSpace(locator.Value) ||
                !Uri.TryCreate(locator.Value, UriKind.Absolute, out var uri) ||
                !OutboundRequestSecurity.TryValidateExternalHttpUri(uri, out _, allowPrivateTargets: true))
            {
                throw new DownloadClientSubmissionException("The direct-download URL is invalid.");
            }

            artifacts.Add((locator, uri));
        }

        if (candidate.SourceDescriptor.IndexerId is not int indexerId)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not associated with a configured indexer.");
        }

        var indexer = await indexerRepository.GetByIdAsync(indexerId, cancellationToken);
        if (indexer == null || !indexer.IsEnabled)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not trusted.");
        }

        var policy = sourcePolicies
            .OrderBy(policy => policy.Priority)
            .FirstOrDefault(policy => policy.CanPrepare(indexer, candidate, artifacts.Select(artifact => artifact.Uri).ToList()));
        if (policy == null)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not trusted.");
        }

        var preparedArtifacts = PrepareArtifacts(artifacts, policy, candidate.Title);

        // Store the policy selected at submission time. The DDL worker re-resolves
        // the same policy before fetching so adding a new source only requires a
        // new allow-list policy, not changes to the transfer processor.
        return new PreparedDirectDownloadSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            locators[0].Value,
            preparedArtifacts,
            policy.Key);
    }

    private static IReadOnlyList<PreparedDirectDownloadArtifact> PrepareArtifacts(
        IReadOnlyList<(DownloadSourceLocator Locator, Uri Uri)> artifacts,
        IDirectDownloadSourcePolicy policy,
        string title)
    {
        var preparedArtifacts = new List<PreparedDirectDownloadArtifact>(artifacts.Count);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            var locator = artifact.Locator;
            if (locator.ExpectedSize < 0 || !Enum.IsDefined(locator.Packaging))
            {
                throw new DownloadClientSubmissionException(
                    "The direct-download artifact metadata is invalid.");
            }

            var sourceFileName = string.IsNullOrWhiteSpace(locator.FileName)
                ? policy.GetFileName(artifact.Uri, new Download { Title = title })
                : locator.FileName;
            if (!DirectDownloadArtifactFileNames.TryNormalizeArtifactFileName(
                    sourceFileName,
                    out var fileName,
                    out _) ||
                !fileNames.Add(fileName))
            {
                throw new DownloadClientSubmissionException(
                    "The direct-download artifact filename is invalid or duplicated.");
            }

            preparedArtifacts.Add(new PreparedDirectDownloadArtifact(
                artifact.Uri,
                fileName,
                locator.ExpectedSize,
                locator.Packaging));
        }

        return preparedArtifacts;
    }
}
