/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Common;
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Common;

public sealed class ListenarrExceptionHandlerTests
{
    [Theory]
    [InlineData("validation", 400)]
    [InlineData("missing", 404)]
    [InlineData("conflict", 409)]
    [InlineData("forbidden", 403)]
    [InlineData("external", 502)]
    public async Task TypedExceptions_MapToStableProblemDetails(string kind, int expectedStatus)
    {
        ProblemDetails? written = null;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(service => service.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(context => written = context.ProblemDetails)
            .ReturnsAsync(true);
        var handler = new ListenarrExceptionHandler(
            new TestHostEnvironment(Environments.Production),
            NullLogger<ListenarrExceptionHandler>.Instance,
            problemDetailsService.Object);
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "trace-test"
        };
        httpContext.Request.Path = "/api/test";

        ListenarrApplicationException exception = kind switch
        {
            "validation" => new ApplicationValidationException("invalid_input", "Input is invalid."),
            "missing" => new ApplicationNotFoundException("missing_item", "Item was not found."),
            "conflict" => new ApplicationConflictException("duplicate_item", "Item already exists."),
            "forbidden" => new ApplicationForbiddenException("operation_forbidden", "Operation is forbidden."),
            _ => new ExternalServiceException("provider_unavailable", "Provider is unavailable.")
        };

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.NotNull(written);
        Assert.Equal(expectedStatus, written.Status);
        Assert.Equal(exception.Code, written.Extensions["code"]);
        Assert.Equal("trace-test", written.Extensions["traceId"]);
        Assert.Equal(exception.SafeDetail, written.Detail);
    }

    [Fact]
    public async Task UnknownException_DoesNotExposeProductionMessage()
    {
        ProblemDetails? written = null;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService
            .Setup(service => service.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(context => written = context.ProblemDetails)
            .ReturnsAsync(true);
        var handler = new ListenarrExceptionHandler(
            new TestHostEnvironment(Environments.Production),
            NullLogger<ListenarrExceptionHandler>.Instance,
            problemDetailsService.Object);

        await handler.TryHandleAsync(
            new DefaultHttpContext { TraceIdentifier = "trace-secret" },
            new InvalidOperationException("secret internal detail"),
            CancellationToken.None);

        Assert.NotNull(written);
        Assert.Null(written.Detail);
        Assert.Equal("internal_error", written.Extensions["code"]);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Listenarr.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
