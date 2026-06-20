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
            _indexerTestWorkflow = indexerTestWorkflow ?? new IndexerTestWorkflow(
                indexerRepository,
                httpClient,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexerTestWorkflow>.Instance);
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

        private async Task SaveTestResultAsync(Indexer indexer, bool persist, bool success, string? error)
        {
            // Update the passed indexer instance
            indexer.LastTestedAt = DateTime.UtcNow;
            indexer.LastTestSuccessful = success;
            indexer.LastTestError = error;

            if (persist && indexer.Id != 0)
            {
                // Persist test result back to the database for the stored indexer
                var existing = await _indexerRepository.GetByIdAsync(indexer.Id);
                if (existing != null)
                {
                    existing.LastTestedAt = indexer.LastTestedAt;
                    existing.LastTestSuccessful = success;
                    existing.LastTestError = error;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _indexerRepository.UpdateAsync(existing);
                }
            }
        }

        private async Task<IActionResult> ExecuteIndexerTestAsync(Indexer indexer, bool persist)
        {
            // Normalize URL first
            indexer.Url = IndexerUrlNormalizer.NormalizeIndexerUrl(indexer.Url);

            var impl = (indexer.Implementation ?? string.Empty).Trim().ToLowerInvariant();

            try
            {
                _logger.LogInformation("[IndexerTest] Testing indexer {Name} (impl={Impl}, url={Url})", LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(indexer.Implementation), LogRedaction.SanitizeUrl(indexer.Url));
                return impl switch
                {
                    var s when s == "internetarchive" || s == "internet archive" => await TestInternetArchive(indexer, persist),
                    var s when s == "myanonamouse" => await TestMyAnonamouse(indexer, persist),
                    // For Newznab/Torznab/Custom fall back to a generic connectivity check
                    _ => await TestGenericIndexer(indexer, persist)
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (System.Net.CookieException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
        }

        private async Task<IActionResult> BuildIndexerTestBadRequestAsync(Indexer indexer, bool persist, string message, Exception ex)
        {
            await SaveTestResultAsync(indexer, persist, false, ex.Message);
            return BadRequest(new
            {
                success = false,
                message,
                error = ex.Message,
                indexer = RedactIndexerForCaller(indexer)
            });
        }

        private async Task<IActionResult> TestGenericIndexer(Indexer indexer, bool persist)
        {
            var result = await _indexerTestWorkflow.TestGenericIndexerAsync(indexer, persist);
            if (result.Succeeded)
            {
                return Ok(new { success = true, message = result.Message, indexer = RedactIndexerForCaller(indexer) });
            }

            if (result.Status.HasValue)
            {
                return BadRequest(new { success = false, message = result.Message, status = result.Status.Value, indexer = RedactIndexerForCaller(indexer) });
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                return BadRequest(new { success = false, message = result.Message, error = result.Error, indexer = RedactIndexerForCaller(indexer) });
            }

            return BadRequest(new { success = false, message = result.Message, indexer = RedactIndexerForCaller(indexer) });
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
        [HttpPost("{id}/test")]
        public async Task<IActionResult> Test(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            return await ExecuteIndexerTestAsync(indexer, persist: true);
        }

        /// <summary>
        /// Test an indexer configuration without saving it. Useful for validating settings before creating an indexer.
        /// </summary>
        /// <param name="indexer">Indexer configuration to test (not persisted).</param>
        [HttpPost("test")]
        public async Task<IActionResult> TestDraft([FromBody] Indexer indexer)
        {
            if (indexer == null)
            {
                return BadRequest(new { message = "Index data is required" });
            }

            return await ExecuteIndexerTestAsync(indexer, persist: false);
        }

        /// <summary>
        /// Test Internet Archive indexer connection
        /// </summary>
        private async Task<IActionResult> TestInternetArchive(Indexer indexer, bool persist)
        {
            var result = await _indexerTestWorkflow.TestInternetArchiveAsync(indexer, persist);
            if (result.Succeeded)
            {
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    collection = result.Collection,
                    indexer = RedactIndexerForCaller(indexer)
                });
            }

            return BadRequest(new
            {
                success = false,
                message = result.Message,
                error = result.Error,
                indexer = RedactIndexerForCaller(indexer)
            });
        }

        /// <summary>
        /// Test MyAnonamouse indexer connection
        /// </summary>
        private async Task<IActionResult> TestMyAnonamouse(Indexer indexer, bool persist)
        {
            var result = await _indexerTestWorkflow.TestMyAnonamouseAsync(indexer, persist);
            if (result.Succeeded)
            {
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    mam_id = RedactMamIdForCaller(result.MamId),
                    indexer = RedactIndexerForCaller(indexer)
                });
            }

            return BadRequest(new
            {
                success = false,
                message = result.Message,
                error = result.Error,
                indexer = RedactIndexerForCaller(indexer)
            });
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
