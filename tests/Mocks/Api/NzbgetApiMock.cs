using Listenarr.Tests.Common;
using System.Xml.Linq;

namespace Listenarr.Tests.Mocks.Api
{
    public class NzbgetApiMock : BaseApiMock
    {
        public sealed record XmlRpcCall(string MethodName, IReadOnlyList<XElement> Parameters);

        private readonly object _xmlRpcLock = new();
        private readonly List<XmlRpcCall> _xmlRpcCalls = [];
        private readonly Dictionary<string, Queue<string>> _responses =
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

        public NzbgetApiMock()
        {
            AddRoute("xmlrpc", ProcessXmlRpcRequest, HttpMethod.Post);
        }

        public void QueueXmlRpcResponse(string methodName, string response)
        {
            lock (_xmlRpcLock)
            {
                if (!_responses.TryGetValue(methodName, out var responses))
                {
                    responses = new Queue<string>();
                    _responses.Add(methodName, responses);
                }

                responses.Enqueue(response);
            }
        }

        public void ResetXmlRpcCapture()
        {
            lock (_xmlRpcLock)
            {
                _xmlRpcCalls.Clear();
                _responses.Clear();
            }
        }

        public static string CreateHistoryResponse(string serializedEntries)
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

            string? response;
            lock (_xmlRpcLock)
            {
                _xmlRpcCalls.Add(new XmlRpcCall(methodName, Array.AsReadOnly(parameters)));
                response = _responses.TryGetValue(methodName, out var responses) &&
                    responses.TryDequeue(out var queuedResponse)
                        ? queuedResponse
                        : null;
            }

            if (response == null && string.Equals(methodName, "history", StringComparison.Ordinal))
            {
                response = DefaultHistoryResponse();
            }

            return response == null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : MockUtils.GetCannedResponse(response, "text/xml");
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
