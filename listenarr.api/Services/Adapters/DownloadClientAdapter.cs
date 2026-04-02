using System.Net;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services.Adapters
{
    /// <summary>
    /// Provides basic utility for interfaces
    /// </summary>
    public class DownloadClientAdapter
    {
        
        protected IDbContextFactory<ListenArrDbContext> _dbFactory;
        protected readonly HttpClient _httpClient;
        protected readonly ILogger _logger;

        public DownloadClientAdapter(IDbContextFactory<ListenArrDbContext> dbFactory, HttpClient httpClient, ILogger logger)
        {
            _dbFactory = dbFactory;
            _httpClient = httpClient;
            _logger = logger;
        }

        protected HttpRequestMessage BuildHttpRequestMessage(HttpMethod method, DownloadClientConfiguration client, string endpoint, string query = "")
        {
            var uri = DownloadClientUriBuilder.BuildUri(client, endpoint);
            var request = new HttpRequestMessage(method, $"{uri.ToString()}{(query != "" ? "?" + query : "")}");
            if (client.GetApiKey() != "")
            {
                request = APIAuthentification(client, request);
            }

            return request;
        }

        protected HttpRequestMessage APIAuthentification(DownloadClientConfiguration client, HttpRequestMessage message)
        {
            message.Headers.Add("X-Api-Key", client.GetApiKey());

            return message;
        }

        /// <exception cref="DownloadClientException">Thrown when the call is not succesfull</exception>
        public async Task<HttpContent> Call(HttpRequestMessage request)
        {
            HttpResponseMessage response;
            try
            {
                _logger.LogDebug($"{request.Method} on {request.RequestUri}");
                response = await _httpClient.SendAsync(request);
            }
            catch (HttpRequestException exception)
            {
                throw new DownloadClientException($"Network error ({exception.StatusCode?.ToString() ?? "unavailable"})", request.RequestUri);
            }
            catch (TaskCanceledException)
            {
                throw new DownloadClientException("Connection timed out", request.RequestUri);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException && exception is not StackOverflowException) {
                throw new DownloadClientException("Connection failed", request.RequestUri, exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Map common statuses to simple, actionable messages
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new DownloadClientException("API key invalid or unauthorized", request.RequestUri);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new DownloadClientException("Host or endpoint not found (check host/port)", request.RequestUri);
                }

                throw new DownloadClientException($"Returned {response.StatusCode}", request.RequestUri);
            }

            return response.Content;
        }
    }
}
