/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.Common;

public sealed class IndexerTestWorkflow
{
    private const string GenericIndexerType = "Generic";

    private readonly IIndexerRepository _indexerRepository;
    private readonly IReadOnlyList<IIndexerConnectionTester> _connectionTesters;
    private readonly ILogger<IndexerTestWorkflow> _logger;

    public IndexerTestWorkflow(
        IIndexerRepository indexerRepository,
        IEnumerable<IIndexerConnectionTester> connectionTesters,
        ILogger<IndexerTestWorkflow> logger)
    {
        _indexerRepository = indexerRepository;
        _connectionTesters = connectionTesters.ToList();
        _logger = logger;
    }

    public async Task<IndexerTestWorkflowResult> TestPersistedAsync(
        int indexerId,
        CancellationToken cancellationToken = default)
    {
        var indexer = await _indexerRepository.GetByIdAsync(indexerId, cancellationToken);
        if (indexer == null)
        {
            return new IndexerTestWorkflowResult(
                IndexerTestWorkflowResultKind.NotFound,
                null,
                null,
                "Indexer not found");
        }

        return await ExecuteAsync(indexer, persist: true, cancellationToken);
    }

    public async Task<IndexerTestWorkflowResult> TestDraftAsync(
        Indexer? indexer,
        CancellationToken cancellationToken = default)
    {
        if (indexer == null)
        {
            return new IndexerTestWorkflowResult(
                IndexerTestWorkflowResultKind.BadRequest,
                null,
                null,
                "Index data is required");
        }

        return await ExecuteAsync(indexer, persist: false, cancellationToken);
    }

    private async Task<IndexerTestWorkflowResult> ExecuteAsync(
        Indexer indexer,
        bool persist,
        CancellationToken cancellationToken)
    {
        indexer.Url = IndexerUrlNormalizer.NormalizeIndexerUrl(indexer.Url);

        var tester = ResolveTester(indexer.Implementation);
        if (tester == null)
        {
            var failure = IndexerConnectionTestResult.Failure(
                "Indexer test failed.",
                "No connection tester is registered for this indexer implementation.");
            await SaveTestResultAsync(indexer, persist, failure, persistAdditionalSettings: false, cancellationToken);
            return new IndexerTestWorkflowResult(IndexerTestWorkflowResultKind.Failed, indexer, failure);
        }

        _logger.LogInformation(
            "Testing indexer {Name} with implementation {Implementation} using tester {TesterType}",
            LogRedaction.SanitizeText(indexer.Name),
            LogRedaction.SanitizeText(indexer.Implementation),
            tester.GetType().Name);

        var result = await tester.TestAsync(indexer, cancellationToken);

        var persistAdditionalSettings = false;
        if (result.Succeeded &&
            persist &&
            !string.IsNullOrWhiteSpace(result.MamId) &&
            IsMyAnonamouseImplementation(indexer.Implementation))
        {
            indexer.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(
                indexer.AdditionalSettings,
                result.MamId);
            persistAdditionalSettings = true;
        }

        await SaveTestResultAsync(indexer, persist, result, persistAdditionalSettings, cancellationToken);

        return new IndexerTestWorkflowResult(
            result.Succeeded ? IndexerTestWorkflowResultKind.Success : IndexerTestWorkflowResultKind.Failed,
            indexer,
            result);
    }

    private IIndexerConnectionTester? ResolveTester(string? implementation)
    {
        var normalized = NormalizeImplementation(implementation);
        var testerType = normalized switch
        {
            "InternetArchive" => "InternetArchive",
            "MyAnonamouse" => "MyAnonamouse",
            "Newznab" or "Torznab" => "TorznabNewznab",
            "Custom" or "" => GenericIndexerType,
            _ => normalized
        };

        return FindTester(testerType) ?? FindTester(GenericIndexerType);
    }

    private IIndexerConnectionTester? FindTester(string indexerType)
        => _connectionTesters.FirstOrDefault(t =>
            string.Equals(t.IndexerType, indexerType, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeImplementation(string? implementation)
    {
        var value = (implementation ?? string.Empty).Trim();
        return value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMyAnonamouseImplementation(string? implementation)
        => string.Equals(NormalizeImplementation(implementation), "MyAnonamouse", StringComparison.OrdinalIgnoreCase);

    private async Task SaveTestResultAsync(
        Indexer indexer,
        bool persist,
        IndexerConnectionTestResult result,
        bool persistAdditionalSettings,
        CancellationToken cancellationToken)
    {
        indexer.LastTestedAt = DateTime.UtcNow;
        indexer.LastTestSuccessful = result.Succeeded;
        indexer.LastTestError = result.Succeeded ? null : result.Error ?? result.Message;

        if (!persist || indexer.Id == 0)
        {
            return;
        }

        var existing = await _indexerRepository.GetByIdAsync(indexer.Id, cancellationToken);
        if (existing == null)
        {
            return;
        }

        existing.LastTestedAt = indexer.LastTestedAt;
        existing.LastTestSuccessful = result.Succeeded;
        existing.LastTestError = indexer.LastTestError;
        if (persistAdditionalSettings)
        {
            existing.AdditionalSettings = indexer.AdditionalSettings;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await _indexerRepository.UpdateAsync(existing, cancellationToken);
    }
}
