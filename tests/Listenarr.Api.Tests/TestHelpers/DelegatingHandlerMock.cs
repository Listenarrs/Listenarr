namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Simple delegating handler useful in tests to return canned HttpResponseMessage objects.
    /// Used across multiple test files.
    /// </summary>
    public class DelegatingHandlerMock : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handlerFunc;
        private readonly Action<string>? _log;

        public DelegatingHandlerMock(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc, Action<string>? log = null)
        {
            _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
            _log = log;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _log?.Invoke($"DelegatingHandlerMock invoked for: {request.Method} {request.RequestUri}");

            return _handlerFunc(request, cancellationToken);
        }
    }
}
