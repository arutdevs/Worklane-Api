using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Web;

public class ContentLengthLimitMiddlewareTests
{
    [Fact]
    public async Task DeclaredBodyOverTheLimit_IsRequestTooLarge()
    {
        var middleware = new ContentLengthLimitMiddleware(
            _ => throw new InvalidOperationException("The body should not be read."),
            Options.Create(new FileStorageSettings()));
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/login";
        context.Request.ContentLength = 50;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new FixedLimit(10)),
            "login"));

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("code").GetString().Should().Be("request_too_large");
        json.RootElement.GetProperty("detail").GetString().Should().Be("The request body exceeds the size limit.");
        json.RootElement.TryGetProperty("maxBytes", out _).Should().BeFalse();
    }

    [Fact]
    public async Task BodyWithinTheLimit_Continues()
    {
        var continued = false;
        var middleware = new ContentLengthLimitMiddleware(_ =>
        {
            continued = true;
            return Task.CompletedTask;
        }, Options.Create(new FileStorageSettings()));
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 4;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new FixedLimit(10)),
            "login"));

        await middleware.InvokeAsync(context);

        continued.Should().BeTrue();
    }

    private sealed class FixedLimit(long? max) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = max;
    }
}
