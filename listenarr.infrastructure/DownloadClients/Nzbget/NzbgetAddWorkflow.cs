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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetAddWorkflow(
        NzbgetXmlRpcClient xmlRpcClient,
        ILogger logger)
    {
        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedUsenetSubmission submission,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            logger.LogInformation("Using NZBGet JSON-RPC append method");
            return await AddViaJsonRpcAsync(client, submission, ct);
        }

        private async Task<DownloadClientSubmissionResult> AddViaJsonRpcAsync(
            DownloadClientConfiguration client,
            PreparedUsenetSubmission submission,
            CancellationToken ct)
        {
            var category = NzbgetRequestPlanner.ResolveCategory(client);
            var priority = NzbgetRequestPlanner.ResolvePriority(client);
            var droneId = Guid.NewGuid().ToString().Replace("-", string.Empty);

            var nzbContentBase64 = Convert.ToBase64String(submission.NzbBytes);
            var nzbFileName = submission.FileName;

            var ppParams = new[]
            {
                new Dictionary<string, object>
                {
                    { "Name", "drone" },
                    { "Value", droneId }
                }
            };

            try
            {
                logger.LogInformation("Calling NZBGet append via XML-RPC for '{Title}'", LogRedaction.SanitizeText(submission.Title));
                var appendResult = await xmlRpcClient.CallAsync(client, "append",
                    nzbFileName,
                    nzbContentBase64,
                    category ?? string.Empty,
                    priority,
                    false,
                    false,
                    string.Empty,
                    0,
                    "SCORE",
                    ppParams
                );

                var queueId = int.Parse(appendResult.Element("i4")?.Value ?? appendResult.Element("int")?.Value ?? "0");

                if (queueId <= 0)
                {
                    throw new DownloadClientSubmissionException("NZBGet rejected the prepared NZB.");
                }

                logger.LogInformation("NZBGet XML-RPC queued '{Title}' with ID {QueueId}, droneId: {DroneId}", LogRedaction.SanitizeText(submission.Title), queueId, LogRedaction.SanitizeText(droneId));
                return new DownloadClientSubmissionResult(queueId.ToString(), droneId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to add NZB via XML-RPC");
                throw;
            }
        }
    }
}
