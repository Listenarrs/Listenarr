using Listenarr.Domain.Common;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Mocks.Api
{
    public class NzbgetApiMock : BaseApiMock
    {
        public static readonly string SINGLE_FILE_NZBGET = "101";
        public static readonly string MULTI_FILE_NZBGET = "202";

        public NzbgetApiMock()
        {
            AddRoute("xmlrpc", GetHistory, HttpMethod.Post);
        }

        private async Task<HttpResponseMessage> GetHistory(HttpRequestMessage request, CancellationToken ct)
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
            return MockUtils.GetCannedResponse(response, "text/xml");
        }
    }
}
