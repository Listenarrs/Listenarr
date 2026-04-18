namespace Listenarr.Api.Tests
{
    public class BaseMock : RegexDelegatingHandlerMock
    {
        private int _calls = 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
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
    }
}