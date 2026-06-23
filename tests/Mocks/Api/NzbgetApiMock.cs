using Listenarr.Tests.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Listenarr.Tests.Mocks.Api
{
    public class NzbgetApiMock : BaseApiMock
    {
        public sealed record XmlRpcCall(string MethodName, IReadOnlyList<XElement> Parameters);
        public sealed record JsonRpcCall(string MethodName, string Body);

        private sealed record QueuedResponse(
            string Body,
            HttpStatusCode StatusCode,
            TimeSpan Delay,
            TaskCompletionSource? RequestStarted = null,
            bool WaitForCancellation = false);

        private readonly object _xmlRpcLock = new();
        private readonly List<XmlRpcCall> _xmlRpcCalls = [];
        private readonly List<JsonRpcCall> _jsonRpcCalls = [];
        private readonly Dictionary<string, Queue<QueuedResponse>> _responses =
            new(StringComparer.Ordinal);

        public static readonly string SINGLE_FILE_NZBGET = "101";
        public static readonly string MULTI_FILE_NZBGET = "202";

        public IReadOnlyList<XmlRpcCall> XmlRpcCalls
        {
            get
            {
                lock (_xmlRpcLock)
                {
                    return _xmlRpcCalls.ToList().AsReadOnly();
                }
            }
        }

        public IReadOnlyList<JsonRpcCall> JsonRpcCalls
        {
            get
            {
                lock (_xmlRpcLock)
                {
                    return _jsonRpcCalls.ToList().AsReadOnly();
                }
            }
        }

        public NzbgetApiMock()
        {
            AddRoute("xmlrpc", ProcessXmlRpcRequest, HttpMethod.Post);
            AddRoute("jsonrpc", ProcessJsonRpcRequest, HttpMethod.Post);
        }

        public void QueueXmlRpcResponse(string methodName, string response)
        {
            QueueXmlRpcResponse(methodName, response, HttpStatusCode.OK, TimeSpan.Zero);
        }

        public void QueueXmlRpcResponse(
            string methodName,
            string response,
            HttpStatusCode statusCode,
            TimeSpan delay)
        {
            QueueResponse($"xml:{methodName}", new QueuedResponse(response, statusCode, delay));
        }

        public Task QueueXmlRpcCancellationResponse(
            string methodName,
            string response)
        {
            var requestStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            QueueResponse(
                $"xml:{methodName}",
                new QueuedResponse(
                    response,
                    HttpStatusCode.OK,
                    TimeSpan.Zero,
                    requestStarted,
                    WaitForCancellation: true));
            return requestStarted.Task;
        }

        public void QueueJsonRpcResponse(string methodName, string response)
        {
            QueueResponse(
                $"json:{methodName}",
                new QueuedResponse(response, HttpStatusCode.OK, TimeSpan.Zero));
        }

        private void QueueResponse(string key, QueuedResponse response)
        {
            lock (_xmlRpcLock)
            {
                if (!_responses.TryGetValue(key, out var responses))
                {
                    responses = new Queue<QueuedResponse>();
                    _responses.Add(key, responses);
                }

                responses.Enqueue(response);
            }
        }

        public void ResetXmlRpcCapture()
        {
            lock (_xmlRpcLock)
            {
                _xmlRpcCalls.Clear();
                _jsonRpcCalls.Clear();
                _responses.Clear();
            }
        }

        public static string CreateHistoryResponse(string serializedEntries)
        {
            return CreateArrayResponse(serializedEntries);
        }

        public static string CreateListGroupsResponse(string serializedEntries)
        {
            return CreateArrayResponse(serializedEntries);
        }

        private static string CreateArrayResponse(string serializedEntries)
        {
            return $$"""
            <?xml version="1.0"?>
            <methodResponse>
              <params>
                <param>
                  <value>
                    <array>
                      <data>
                        {{serializedEntries}}
                      </data>
                    </array>
                  </value>
                </param>
              </params>
            </methodResponse>
            """;
        }

        private async Task<HttpResponseMessage> ProcessXmlRpcRequest(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var methodCall = XDocument.Parse(body).Root
                ?? throw new InvalidOperationException("NZBGet XML-RPC request has no root element.");
            var methodName = methodCall.Element("methodName")?.Value
                ?? throw new InvalidOperationException("NZBGet XML-RPC request has no method name.");
            var parameters = methodCall.Element("params")?
                .Elements("param")
                .Select(parameter => new XElement(parameter.Element("value")!))
                .ToArray()
                ?? [];

            QueuedResponse? response;
            lock (_xmlRpcLock)
            {
                _xmlRpcCalls.Add(new XmlRpcCall(methodName, Array.AsReadOnly(parameters)));
                response = _responses.TryGetValue($"xml:{methodName}", out var responses) &&
                    responses.TryDequeue(out var queuedResponse)
                        ? queuedResponse
                        : null;
            }

            if (response == null && string.Equals(methodName, "history", StringComparison.Ordinal))
            {
                response = new QueuedResponse(
                    DefaultHistoryResponse(),
                    HttpStatusCode.OK,
                    TimeSpan.Zero);
            }

            response?.RequestStarted?.TrySetResult();
            return await CreateResponseAsync(response, "text/xml", ct);
        }

        private async Task<HttpResponseMessage> ProcessJsonRpcRequest(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var methodName = document.RootElement.GetProperty("method").GetString()
                ?? throw new InvalidOperationException("NZBGet JSON-RPC request has no method name.");

            QueuedResponse? response;
            lock (_xmlRpcLock)
            {
                _jsonRpcCalls.Add(new JsonRpcCall(methodName, body));
                response = _responses.TryGetValue($"json:{methodName}", out var responses) &&
                    responses.TryDequeue(out var queuedResponse)
                        ? queuedResponse
                        : null;
            }

            return await CreateResponseAsync(response, "application/json", ct);
        }

        private static async Task<HttpResponseMessage> CreateResponseAsync(
            QueuedResponse? response,
            string mediaType,
            CancellationToken cancellationToken)
        {
            if (response == null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (response.Delay > TimeSpan.Zero)
            {
                await Task.Delay(response.Delay, cancellationToken);
            }

            if (response.WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, mediaType)
            };
        }

        private static string DefaultHistoryResponse()
        {
            var response = """
            <?xml version="1.0"?>
            <methodResponse>
                <params>
                    <param>
                        <value>
                            <array>
                                <data>
                                    <value>
                                        <struct>
                                            <member>
                                                <name>ID</name>
                                                <value><string>{{SINGLE_FILE_NZBGET}}</string></value>
                                            </member>
                                            <member>
                                                <name>DestDir</name>
                                                <value><string>{{ARBITRARY_PATH_1}}</string></value>
                                            </member>
                                        </struct>
                                    </value>
                                    <value>
                                        <struct>
                                            <member>
                                                <name>ID</name>
                                                <value><string>{{MULTI_FILE_NZBGET}}</string></value>
                                            </member>
                                            <member>
                                                <name>DestDir</name>
                                                <value><string>{{ARBITRARY_PATH_2}}</string></value>
                                            </member>
                                        </struct>
                                    </value>
                                </data>
                            </array>
                        </value>
                    </param>
                </params>
            </methodResponse>
            """;
            response = response.Replace("{{SINGLE_FILE_NZBGET}}", SINGLE_FILE_NZBGET);
            response = response.Replace("{{MULTI_FILE_NZBGET}}", MULTI_FILE_NZBGET);
            response = response.Replace("{{ARBITRARY_PATH_1}}", FileUtils.GetAbsolutePath("nzbget", "completed", "Book.m4b"));
            response = response.Replace("{{ARBITRARY_PATH_2}}", FileUtils.GetAbsolutePath("nzbget", "completed", "Book Folder"));
            return response;
        }
    }
}
