using Asp.Versioning.ApiExplorer;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Tests.Common
{
    public abstract class TestUtils
    {
        /// <summary>
        /// Resolves the versioned API base path (e.g. "/api/v1") from the test server's service provider.
        /// </summary>
        public static string ResolveApiBasePath(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IApiVersionDescriptionProvider>();
            var groupName = provider?.ApiVersionDescriptions.FirstOrDefault(d => !d.IsDeprecated)?.GroupName
                ?? provider?.ApiVersionDescriptions.FirstOrDefault()?.GroupName;

            return string.IsNullOrWhiteSpace(groupName) ? "/api/v1" : $"/api/{groupName}";
        }

        /// <summary>
        /// Remove the waiting timer on a job
        /// </summary>
        /// <param name="downloadProcessingJobRepository"></param>
        /// <param name="job"></param>
        public static async Task CancelJobRetryWait(IDownloadProcessingJobRepository downloadProcessingJobRepository, DownloadProcessingJob job)
        {
            job.NextRetryAt = null;
            await downloadProcessingJobRepository.UpdateAsync(job);
        }
    }
}
