using System.Runtime.CompilerServices;
using Asp.Versioning.ApiExplorer;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Tests.Common
{
    public abstract class TestUtils
    {
        private static readonly string featureDirectory = "Features";
        private static readonly string dataDirectory = "Data";

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

        /// <summary>
        /// Gives the equivalent directory where the data files are stored
        /// Note: This will fail if you have another <see cref="featureDirectory"/> directory in the caller path
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="callerPath"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">In case the path does not contain one and only one <see cref="featureDirectory"/> directory</exception>
        public static string GetDataPath(string filename, [CallerFilePath] string callerPath = "")
        {
            string[] folders = callerPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int count = folders.Count(f => string.Equals(f, featureDirectory, StringComparison.OrdinalIgnoreCase));

            if (count > 1)
            {
                throw new InvalidOperationException($"More than one '{featureDirectory}' directory: Update the algorithm to handle that case or move the project elsewhere: {callerPath}");
            }
            if (count == 0)
            {
                throw new InvalidOperationException($"Missing '{featureDirectory}' directory, can't determine where data are: {callerPath}");
            }

            string folder = Path.GetDirectoryName(callerPath)
                .Replace($"{Path.DirectorySeparatorChar}{featureDirectory}{Path.DirectorySeparatorChar}",
                         $"{Path.DirectorySeparatorChar}{dataDirectory}{Path.DirectorySeparatorChar}");

            return Path.Combine(folder, filename);
        }
    }
}
