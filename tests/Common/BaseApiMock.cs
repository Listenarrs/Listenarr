using System.Text.Json;

namespace Listenarr.Tests.Common
{
    public class BaseApiMock : RegexDelegatingHandlerMock
    {
        protected int _calls = 0;
        private HttpRequestMessage _lastRequest = new();
        private string _lastContent = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            _lastRequest = request;
            _lastContent = "";
            if (request.Content != null)
            {
                _lastContent = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }

        public void ResetCallCount()
        {
            _calls = 0;
        }

        public int GetCallCount()
        {
            var callCount = _calls;
            ResetCallCount();
            return callCount;
        }

        public string GetLastContent()
        {
            return _lastContent;
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
            return _lastRequest;
        }
    }
}
