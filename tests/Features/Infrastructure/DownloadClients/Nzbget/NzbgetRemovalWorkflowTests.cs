/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using System.Xml.Linq;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget
{
    public sealed class NzbgetRemovalWorkflowTests
    {
        [Fact]
        public async Task RemoveAsync_WhenNumericIdAbsentFromHistoryAndQueue_ReturnsSuccess()
        {
            var api = new XmlRpcQueueHandler();
            api.Enqueue("editqueue", BooleanResponse(false));
            api.Enqueue("editqueue", BooleanResponse(false));
            api.Enqueue("history", ArrayResponse());
            api.Enqueue("listgroups", ArrayResponse());
            var workflow = CreateWorkflow(api);

            var result = await workflow.RemoveAsync(CreateClient(), "42", deleteFiles: false);

            Assert.True(result);
            Assert.Equal(2, api.CallCount("editqueue"));
            Assert.Equal(1, api.CallCount("history"));
            Assert.Equal(1, api.CallCount("listgroups"));
        }

        [Fact]
        public async Task RemoveAsync_WhenIdIsNotNumeric_ReturnsFalseWithoutHistoryLookup()
        {
            var api = new XmlRpcQueueHandler();
            var workflow = CreateWorkflow(api);

            var result = await workflow.RemoveAsync(CreateClient(), "legacy-content-id", deleteFiles: false);

            Assert.False(result);
            Assert.Equal(0, api.CallCount("history"));
            Assert.Equal(0, api.CallCount("editqueue"));
        }

        [Fact]
        public async Task RemoveAsync_WhenItemStillPresentAfterDeleteFailure_ReturnsFalse()
        {
            var api = new XmlRpcQueueHandler();
            api.Enqueue("editqueue", BooleanResponse(false));
            api.Enqueue("editqueue", BooleanResponse(false));
            api.Enqueue("history", ArrayResponse(HistoryEntryValue(42)));
            var workflow = CreateWorkflow(api);

            var result = await workflow.RemoveAsync(CreateClient(), "42", deleteFiles: false);

            Assert.False(result);
            Assert.Equal(2, api.CallCount("editqueue"));
            Assert.Equal(1, api.CallCount("history"));
            Assert.Equal(0, api.CallCount("listgroups"));
        }

        private static NzbgetRemovalWorkflow CreateWorkflow(XmlRpcQueueHandler api)
        {
            var http = new HttpClient(api, disposeHandler: false);
            var xmlRpcClient = new NzbgetXmlRpcClient(new TestHttpClientFactory(http), "nzbget");
            return new NzbgetRemovalWorkflow(xmlRpcClient, NullLogger.Instance);
        }

        private static DownloadClientConfiguration CreateClient() =>
            new DownloadClientConfigurationBuilder()
                .WithType("nzbget")
                .WithHost("localhost")
                .WithPort(6789)
                .Build();

        private static string BooleanResponse(bool value) =>
            ValueResponse($"<boolean>{(value ? "1" : "0")}</boolean>");

        private static string ArrayResponse(params string[] values) =>
            ValueResponse($"<array><data>{string.Concat(values)}</data></array>");

        private static string ValueResponse(string serializedValue) =>
            $"""
            <?xml version="1.0"?>
            <methodResponse><params><param><value>{serializedValue}</value></param></params></methodResponse>
            """;

        private static string HistoryEntryValue(int id) =>
            $"""
            <value><struct>
                <member><name>ID</name><value><i4>{id}</i4></value></member>
                <member><name>Name</name><value><string>Already Present</string></value></member>
            </struct></value>
            """;

        private sealed class TestHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => httpClient;
        }

        private sealed class XmlRpcQueueHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, Queue<(string Body, HttpStatusCode Status)>> _responses = [];
            private readonly Dictionary<string, int> _calls = [];

            public void Enqueue(string methodName, string body, HttpStatusCode status = HttpStatusCode.OK)
            {
                if (!_responses.TryGetValue(methodName, out var queue))
                {
                    queue = new Queue<(string Body, HttpStatusCode Status)>();
                    _responses[methodName] = queue;
                }

                queue.Enqueue((body, status));
            }

            public int CallCount(string methodName) =>
                _calls.TryGetValue(methodName, out var count) ? count : 0;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var content = await request.Content!.ReadAsStringAsync(cancellationToken);
                var methodName = XDocument.Parse(content)
                    .Root!
                    .Element("methodName")!
                    .Value;

                _calls.TryGetValue(methodName, out var count);
                _calls[methodName] = count + 1;

                if (!_responses.TryGetValue(methodName, out var queue) || queue.Count == 0)
                {
                    throw new InvalidOperationException($"No XML-RPC response queued for {methodName}");
                }

                var (body, status) = queue.Dequeue();
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body)
                };
            }
        }
    }
}
