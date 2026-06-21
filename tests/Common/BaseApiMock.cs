using System.Collections.ObjectModel;
using System.Text.Json;

namespace Listenarr.Tests.Common
{
    public sealed class ApiRequestSnapshot
    {
        private static readonly IReadOnlyList<string> EmptyHeaderValues = Array.Empty<string>();

        private ApiRequestSnapshot(
            Uri requestUri,
            IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
        {
            RequestUri = requestUri;
            Headers = headers;
        }

        public Uri RequestUri { get; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

        public IReadOnlyList<string> GetHeaderValues(string name)
        {
            return Headers.TryGetValue(name, out var values) ? values : EmptyHeaderValues;
        }

        internal static ApiRequestSnapshot Capture(HttpRequestMessage request)
        {
            var requestUri = request.RequestUri
                ?? throw new InvalidOperationException("Cannot snapshot a request without a URI.");
            var copiedUri = new Uri(
                requestUri.OriginalString,
                requestUri.IsAbsoluteUri ? UriKind.Absolute : UriKind.Relative);
            var copiedHeaders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            CopyHeaders(request.Headers, copiedHeaders);
            if (request.Content != null)
            {
                CopyHeaders(request.Content.Headers, copiedHeaders);
            }

            return new ApiRequestSnapshot(
                copiedUri,
                new ReadOnlyDictionary<string, IReadOnlyList<string>>(copiedHeaders));
        }

        private static void CopyHeaders(
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
            IDictionary<string, IReadOnlyList<string>> destination)
        {
            foreach (var (name, values) in source)
            {
                destination[name] = Array.AsReadOnly(values.ToArray());
            }
        }
    }

    public class BaseApiMock : RegexDelegatingHandlerMock
    {
        private readonly object _observationLock = new();
        private readonly List<ApiRequestSnapshot> _requestHistory = new();
        protected int _calls = 0;
        private string _lastContent = "";

        public IReadOnlyList<ApiRequestSnapshot> RequestHistory
        {
            get
            {
                lock (_observationLock)
                {
                    return _requestHistory.ToList().AsReadOnly();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = ApiRequestSnapshot.Capture(request);
            lock (_observationLock)
            {
                _calls++;
                _requestHistory.Add(snapshot);
                _lastContent = "";
            }

            if (request.Content != null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_observationLock)
                {
                    _lastContent = content;
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }

        public void ResetCallCount()
        {
            lock (_observationLock)
            {
                _calls = 0;
            }
        }

        public int GetCallCount()
        {
            lock (_observationLock)
            {
                var callCount = _calls;
                _calls = 0;
                return callCount;
            }
        }

        public void ResetRequestHistory()
        {
            lock (_observationLock)
            {
                _requestHistory.Clear();
                _lastContent = "";
            }
        }

        public string GetLastContent()
        {
            lock (_observationLock)
            {
                return _lastContent;
            }
        }

        public JsonDocument? GetLastJsonContent()
        {
            try
            {
                return JsonDocument.Parse(_lastContent);
            }
            catch (JsonException)
            {
            }
            catch (ArgumentException)
            {
            }

            return null;
        }

        public HttpRequestMessage GetLastRequest()
        {
            ApiRequestSnapshot? snapshot;
            lock (_observationLock)
            {
                snapshot = _requestHistory.LastOrDefault();
            }

            if (snapshot == null)
            {
                return new HttpRequestMessage();
            }

            var request = new HttpRequestMessage(HttpMethod.Get, snapshot.RequestUri);
            foreach (var (name, values) in snapshot.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, values);
            }

            return request;
        }
    }
}
