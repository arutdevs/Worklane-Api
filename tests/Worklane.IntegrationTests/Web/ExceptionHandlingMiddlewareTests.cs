using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Worklane.Application.Common.Exceptions;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Web;

public class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task AppException_WritesCodeAndTopLevelParams()
    {
        var body = await Invoke(new ConflictException(
            "column_has_archived_cards",
            "Column has 2 archived card(s). Choose a column to move them to.",
            ErrorParams.Of(("activeTaskCount", 0), ("archivedTaskCount", 2))));

        body.Status.Should().Be(HttpStatusCode.Conflict);
        body.Json.GetProperty("code").GetString().Should().Be("column_has_archived_cards");
        body.Json.GetProperty("title").GetString().Should().Be("Conflict");
        body.Json.GetProperty("detail").GetString().Should().Be("Column has 2 archived card(s). Choose a column to move them to.");
        body.Json.GetProperty("activeTaskCount").GetInt32().Should().Be(0);
        body.Json.GetProperty("archivedTaskCount").GetInt32().Should().Be(2);
        body.Json.TryGetProperty("extensions", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(BadRequestException), "bad_request", HttpStatusCode.BadRequest)]
    [InlineData(typeof(ConflictException), "conflict", HttpStatusCode.Conflict)]
    [InlineData(typeof(ForbiddenAccessException), "forbidden", HttpStatusCode.Forbidden)]
    [InlineData(typeof(AuthenticationFailedException), "unauthorized", HttpStatusCode.Unauthorized)]
    [InlineData(typeof(NotFoundException), "not_found", HttpStatusCode.NotFound)]
    public async Task DefaultCodes_FollowTheExceptionType(Type exceptionType, string code, HttpStatusCode status)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "nope")!;
        var body = await Invoke(exception);

        body.Status.Should().Be(status);
        body.Json.GetProperty("code").GetString().Should().Be(code);
        body.Json.GetProperty("detail").GetString().Should().Be("nope");
    }

    [Fact]
    public async Task NotFound_UsesNotFoundCode()
    {
        var body = await Invoke(new NotFoundException(nameof(BoardColumnPlaceholder), Guid.Empty));
        body.Status.Should().Be(HttpStatusCode.NotFound);
        body.Json.GetProperty("code").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task FileTooLarge_IncludesMaxBytes()
    {
        var body = await Invoke(new PayloadTooLargeException(10 * 1024 * 1024));
        body.Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.Json.GetProperty("code").GetString().Should().Be("file_too_large");
        body.Json.GetProperty("maxBytes").GetInt64().Should().Be(10 * 1024 * 1024);
        body.Json.GetProperty("detail").GetString().Should().Be("File exceeds the maximum size of 10485760 bytes.");
    }

    [Fact]
    public async Task UnsupportedMediaType_UsesItsCode()
    {
        var body = await Invoke(new UnsupportedMediaTypeException(".exe"));
        body.Status.Should().Be(HttpStatusCode.UnsupportedMediaType);
        body.Json.GetProperty("code").GetString().Should().Be("unsupported_file_type");
        body.Json.GetProperty("extension").GetString().Should().Be(".exe");
        body.Json.GetProperty("detail").GetString().Should().Be("The file type is not allowed.");
    }

    [Fact]
    public async Task UnsupportedMediaType_FrameworkFallback_UsesTheFileTypeCode()
    {
        var body = await Invoke(new BadHttpRequestException("raw framework text", StatusCodes.Status415UnsupportedMediaType));
        body.Status.Should().Be(HttpStatusCode.UnsupportedMediaType);
        body.Json.GetProperty("code").GetString().Should().Be("unsupported_file_type");
        body.Json.GetProperty("detail").GetString().Should().Be("The file type is not allowed.");
        body.Json.GetRawText().Should().NotContain("raw framework text");
    }

    [Fact]
    public async Task Validation_WritesFieldCodesAndKeepsEnglishErrors()
    {
        var body = await Invoke(new ValidationException(
        [
            new ValidationFailure("Name", "'Name' must not be empty.") { ErrorCode = "NotEmptyValidator" },
            new ValidationFailure("WipLimit", "This field can't be cleared.") { ErrorCode = "not_nullable" }
        ]));

        body.Status.Should().Be(HttpStatusCode.BadRequest);
        body.Json.GetProperty("code").GetString().Should().Be("validation_failed");
        body.Json.GetProperty("errors").GetProperty("Name")[0].GetString().Should().Be("'Name' must not be empty.");
        body.Json.GetProperty("fieldCodes").GetProperty("Name")[0].GetString().Should().Be("not_empty");
        body.Json.GetProperty("fieldCodes").GetProperty("WipLimit")[0].GetString().Should().Be("not_nullable");
    }

    [Fact]
    public async Task UnhandledException_IsInternalErrorWithoutTheMessage()
    {
        const string secret = "SELECT secret FROM users";
        var exception = new InvalidOperationException(secret, new InvalidOperationException("inner-secret-value"));
        var logs = new List<LoggedException>();
        var middleware = new ExceptionHandlingMiddleware(_ => throw exception, new CollectingLogger<ExceptionHandlingMiddleware>(logs));
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-500";
        context.Request.Path = "/api/example";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var json = document.RootElement;
        json.GetProperty("code").GetString().Should().Be("internal_error");
        json.GetProperty("detail").GetString().Should().Be("An unexpected error occurred.");
        json.GetProperty("traceId").GetString().Should().Be(Activity.Current?.Id ?? "trace-500");
        var raw = json.GetRawText();
        raw.Should().NotContain(secret);
        raw.Should().NotContain("inner-secret-value");
        raw.Should().NotContain(exception.GetType().Name);

        var logged = logs.Should().ContainSingle().Subject;
        logged.Level.Should().Be(LogLevel.Error);
        logged.Exception.Should().BeSameAs(exception);
        logged.Exception!.ToString().Should().Contain(secret);
        logged.Exception.ToString().Should().Contain("inner-secret-value");
        logged.Exception.StackTrace.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FrameworkBodyLimit_UsesRequestTooLarge()
    {
        var body = await Invoke(new BadHttpRequestException("Kestrel limit 2048", StatusCodes.Status413PayloadTooLarge));
        body.Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.Json.GetProperty("code").GetString().Should().Be("request_too_large");
        body.Json.GetProperty("detail").GetString().Should().Be("The request body exceeds the size limit.");
        body.Json.GetRawText().Should().NotContain("2048");
        body.Json.TryGetProperty("maxBytes", out _).Should().BeFalse();
    }

    [Fact]
    public async Task FrameworkBadRequest_UsesInvalidRequestBody()
    {
        var body = await Invoke(new BadHttpRequestException("malformed", StatusCodes.Status400BadRequest));
        body.Status.Should().Be(HttpStatusCode.BadRequest);
        body.Json.GetProperty("code").GetString().Should().Be("invalid_request_body");
        body.Json.GetProperty("detail").GetString().Should().Be("The request is invalid.");
    }

    [Fact]
    public async Task ReservedParamNames_DoNotReplaceCode()
    {
        var body = await Invoke(new ConflictException(
            "column_delete_conflict",
            "The column changed while it was being deleted. Reload the board and try again.",
            ErrorParams.Of(("code", "should-not-win"))));

        body.Json.GetProperty("code").GetString().Should().Be("column_delete_conflict");
    }

    [Fact]
    public async Task ReservedParamNames_IgnoreCase()
    {
        var body = await Invoke(new ConflictException(
            "column_delete_conflict",
            "The column changed while it was being deleted. Reload the board and try again.",
            ErrorParams.Of(("Code", "should-not-win"), ("TraceId", "also-not"))));

        body.Json.GetProperty("code").GetString().Should().Be("column_delete_conflict");
        body.Json.TryGetProperty("Code", out _).Should().BeFalse();
        body.Json.TryGetProperty("TraceId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UnlistedAppException_UsesItsOwnStatusInsteadOf500()
    {
        var body = await Invoke(new TeapotException());

        body.Status.Should().Be((HttpStatusCode)418);
        body.Json.GetProperty("status").GetInt32().Should().Be(418);
        body.Json.GetProperty("code").GetString().Should().Be("teapot");
        body.Json.GetProperty("detail").GetString().Should().Be("The kettle is a client error.");
    }

    [Fact]
    public void InvalidModelState_StampsRequestTooLarge()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/tasks/x/attachments";
        var modelState = new ModelStateDictionary();
        modelState.TryAddModelException("file", new BadHttpRequestException("too big", StatusCodes.Status413PayloadTooLarge));
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor(), modelState);

        var result = ExceptionHandlingMiddleware.InvalidModelStateResponse(context, _ => new BadRequestResult());

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        var body = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        body.Extensions["code"].Should().Be("request_too_large");
    }

    [Fact]
    public void InvalidModelState_StampsInvalidRequestBodyOnTheBuiltInResult()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/tasks";
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("title", "Required");
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor(), modelState);

        var result = ExceptionHandlingMiddleware.InvalidModelStateResponse(
            context,
            ctx => new ObjectResult(new ValidationProblemDetails(ctx.ModelState)) { StatusCode = 400 });

        var problem = result.Should().BeOfType<ObjectResult>().Subject.Value.Should().BeOfType<ValidationProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("invalid_request_body");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json, string? ContentType)> Invoke(Exception exception)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/example";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, document.RootElement.Clone(), context.Response.ContentType);
    }

    private sealed class BoardColumnPlaceholder;

    private sealed class TeapotException() : AppException("teapot", "The kettle is a client error.")
    {
        public override HttpStatusCode StatusCode => (HttpStatusCode)418;
    }

    private sealed record LoggedException(LogLevel Level, Exception? Exception);

    private sealed class CollectingLogger<T>(List<LoggedException> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Add(new LoggedException(logLevel, exception));
        }
    }
}
