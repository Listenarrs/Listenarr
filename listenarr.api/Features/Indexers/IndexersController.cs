/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Listenarr.Api.Attributes;
using Listenarr.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Listenarr.Api.Features.Indexers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/indexers")]
    [Tags("Indexers")]
    public class IndexersController : ControllerBase
    {
        private readonly IIndexerRepository _indexerRepository;
        private readonly ILogger<IndexersController> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly IndexerTestWorkflow _indexerTestWorkflow;
        private readonly ProwlarrIndexerImportWorkflow _prowlarrImportWorkflow;
        private readonly IndexerDebugSearchWorkflow _debugSearchWorkflow;
        private readonly IndexerResponseRedactor _responseRedactor;

        public IndexersController(
            IIndexerRepository indexerRepository,
            ILogger<IndexersController> logger,
            HttpClient httpClient,
            IConfigurationService configurationService,
            IndexerTestWorkflow? indexerTestWorkflow = null,
            ProwlarrIndexerImportWorkflow? prowlarrImportWorkflow = null,
            IndexerDebugSearchWorkflow? debugSearchWorkflow = null)
        {
            _indexerRepository = indexerRepository;
            _logger = logger;
            _configurationService = configurationService;
            _indexerTestWorkflow = indexerTestWorkflow ?? throw new ArgumentNullException(nameof(indexerTestWorkflow));
            _prowlarrImportWorkflow = prowlarrImportWorkflow ?? new ProwlarrIndexerImportWorkflow(
                indexerRepository,
                configurationService,
                httpClient,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProwlarrIndexerImportWorkflow>.Instance);
            _debugSearchWorkflow = debugSearchWorkflow ?? new IndexerDebugSearchWorkflow(
                httpClient,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexerDebugSearchWorkflow>.Instance);
            _responseRedactor = new IndexerResponseRedactor();
        }

        private bool ShouldRedactIndexerSecretsForCaller()
            => _responseRedactor.ShouldRedact(HttpContext);

        private Indexer RedactIndexerForCaller(Indexer indexer)
            => _responseRedactor.RedactIndexerForCaller(indexer, HttpContext);

        private List<Indexer> RedactIndexersForCaller(IEnumerable<Indexer> indexers)
            => _responseRedactor.RedactIndexersForCaller(indexers, HttpContext);

        private string? RedactMamIdForCaller(string? mamId)
            => _responseRedactor.RedactMamIdForCaller(mamId, HttpContext);

        private IActionResult ToActionResult(IndexerTestWorkflowResult result)
        {
            return result.Kind switch
            {
                IndexerTestWorkflowResultKind.NotFound => NotFound(new { message = result.Message ?? "Indexer not found" }),
                IndexerTestWorkflowResultKind.BadRequest => BadRequest(new { message = result.Message ?? "Index data is required" }),
                IndexerTestWorkflowResultKind.Success => ToSuccessfulTestResult(result),
                _ => ToFailedTestResult(result)
            };
        }

        private IActionResult ToSuccessfulTestResult(IndexerTestWorkflowResult result)
        {
            var testResult = result.TestResult!;
            return Ok(new
            {
                success = true,
                message = testResult.Message,
                collection = testResult.Collection,
                mam_id = RedactMamIdForCaller(testResult.MamId),
                indexer = result.Indexer == null ? null : RedactIndexerForCaller(result.Indexer)
            });
        }

        private IActionResult ToFailedTestResult(IndexerTestWorkflowResult result)
        {
            var testResult = result.TestResult!;
            return BadRequest(new
            {
                success = false,
                message = testResult.Message,
                error = testResult.Error,
                status = testResult.StatusCode,
                indexer = result.Indexer == null ? null : RedactIndexerForCaller(result.Indexer)
            });
        }

        /// <summary>
        /// Get all configured indexers.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var indexers = (await _indexerRepository.GetAllAsync())
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .ToList();

            return Ok(RedactIndexersForCaller(indexers));
        }

        /// <summary>
        /// Get an indexer by its database ID.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            return Ok(RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Create a new indexer.
        /// </summary>
        /// <param name="indexer">Indexer configuration to create.</param>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Indexer indexer)
        {
            indexer.CreatedAt = DateTime.UtcNow;
            indexer.UpdatedAt = DateTime.UtcNow;

            indexer = await _indexerRepository.AddAsync(indexer);

            _logger.LogInformation("Created indexer '{Name}' (ID: {Id}, Type: {Type})",
                indexer.Name, indexer.Id, indexer.Type);

            return CreatedAtAction(nameof(GetById), new { id = indexer.Id }, RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Import indexers from a Prowlarr instance.
        /// By default this imports audiobook-related indexers (category 3000/3030),
        /// but a configured tag filter overrides that selection.
        /// </summary>
        /// <param name="request">Prowlarr server URL and API key.</param>
        [HttpPost("prowlarr/import")]
        public async Task<IActionResult> ImportFromProwlarr([FromBody] ProwlarrImportRequestDto request)
        {
            var result = await _prowlarrImportWorkflow.ImportAsync(request);
            if (result.Kind == ProwlarrIndexerImportWorkflowResultKind.BadRequest)
            {
                return BadRequest(new { message = result.Message });
            }

            if (result.Kind == ProwlarrIndexerImportWorkflowResultKind.UpstreamError)
            {
                if (result.UpstreamStatus.HasValue)
                {
                    return StatusCode(result.StatusCode ?? StatusCodes.Status502BadGateway, new { message = result.Message, status = result.UpstreamStatus.Value });
                }

                return StatusCode(result.StatusCode ?? StatusCodes.Status502BadGateway, new { message = result.Message });
            }

            return Ok(new
            {
                addedCount = result.AddedCount,
                skippedCount = result.SkippedCount,
                total = result.Total,
                indexers = result.CreatedIndexers.Select(i => new { id = i.Id, name = i.Name, url = i.Url, implementation = i.Implementation })
            });
        }

        /// <summary>
        /// Update an existing indexer.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        /// <param name="indexer">Updated indexer configuration.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] Indexer indexer)
        {
            var existing = await _indexerRepository.GetByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            // Update properties
            existing.Name = indexer.Name;
            existing.Type = indexer.Type;
            existing.Implementation = indexer.Implementation;
            existing.Url = indexer.Url;
            existing.ApiKey = indexer.ApiKey == ApiResponseRedactor.RedactedValue ? existing.ApiKey : indexer.ApiKey;
            existing.Categories = indexer.Categories;
            existing.AnimeCategories = indexer.AnimeCategories;
            existing.Tags = indexer.Tags;
            existing.EnableRss = indexer.EnableRss;
            existing.EnableAutomaticSearch = indexer.EnableAutomaticSearch;
            existing.EnableInteractiveSearch = indexer.EnableInteractiveSearch;
            existing.EnableAnimeStandardSearch = indexer.EnableAnimeStandardSearch;
            existing.IsEnabled = indexer.IsEnabled;
            existing.Priority = indexer.Priority;
            existing.MinimumAge = indexer.MinimumAge;
            existing.Retention = indexer.Retention;
            existing.MaximumSize = indexer.MaximumSize;
            existing.AdditionalSettings = ApiResponseRedactor.MergeAdditionalSettings(existing.AdditionalSettings, indexer.AdditionalSettings);
            existing.UpdatedAt = DateTime.UtcNow;

            await _indexerRepository.UpdateAsync(existing);

            _logger.LogInformation("Updated indexer '{Name}' (ID: {Id})", existing.Name, existing.Id);

            return Ok(RedactIndexerForCaller(existing));
        }

        /// <summary>
        /// Delete an indexer.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            await _indexerRepository.DeleteAsync(id);

            _logger.LogInformation("Deleted indexer '{Name}' (ID: {Id})", indexer.Name, indexer.Id);

            return Ok(new { message = "Indexer deleted successfully", id });
        }

        /// <summary>
        /// Test an indexer's connection to verify it is reachable and properly configured.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPost("{id}/test")]
        public async Task<IActionResult> Test(int id, CancellationToken cancellationToken = default)
        {
            var result = await _indexerTestWorkflow.TestPersistedAsync(id, cancellationToken);
            return ToActionResult(result);
        }

        /// <summary>
        /// Test an indexer configuration without saving it. Useful for validating settings before creating an indexer.
        /// </summary>
        /// <param name="indexer">Indexer configuration to test (not persisted).</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPost("test")]
        public async Task<IActionResult> TestDraft([FromBody] Indexer? indexer, CancellationToken cancellationToken = default)
        {
            var result = await _indexerTestWorkflow.TestDraftAsync(indexer, cancellationToken);
            return ToActionResult(result);
        }

        /// <summary>
        /// Debug search against a MyAnonamouse indexer: returns raw response plus parsed results
        /// </summary>
        [HttpPost("{id}/debug-search")]
        [LocalOrAdmin]
        public async Task<IActionResult> DebugMyAnonamouseSearch(int id, [FromBody] JsonElement body)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null) return NotFound(new { message = "Indexer not found" });

            var result = await _debugSearchWorkflow.ExecuteMyAnonamouseAsync(indexer, id, body, Request, HttpContext);
            return StatusCode(result.StatusCode, result.Payload);
        }

        /// <summary>
        /// Toggle an indexer's enabled/disabled state.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpPut("{id}/toggle")]
        public async Task<IActionResult> Toggle(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            indexer.IsEnabled = !indexer.IsEnabled;
            indexer.UpdatedAt = DateTime.UtcNow;
            await _indexerRepository.UpdateAsync(indexer);

            _logger.LogInformation("Toggled indexer '{Name}' to {State}",
                indexer.Name, indexer.IsEnabled ? "enabled" : "disabled");

            return Ok(RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Get only enabled indexers.
        /// </summary>
        [HttpGet("enabled")]
        public async Task<IActionResult> GetEnabled()
        {
            var indexers = (await _indexerRepository.GetAllAsync())
                .Where(i => i.IsEnabled)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .ToList();

            return Ok(RedactIndexersForCaller(indexers));
        }

    }
}
