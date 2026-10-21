using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;
using Worklane.Application;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Interfaces;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure;
using Worklane.Infrastructure.Persistence;
using Worklane.WebApi.Filters;
using Worklane.WebApi.Hubs;
using Worklane.WebApi.Middleware;
using Worklane.WebApi.Seed;
using Worklane.WebApi.Services;
using Worklane.WebApi.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

// Add services to the container.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IConfigureOptions<MvcOptions>, AttachmentUploadLimitSetup>();
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    var builtIn = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
        ExceptionHandlingMiddleware.InvalidModelStateResponse(context, builtIn);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Worklane API", Version = "v1" });
    options.SchemaFilter<Worklane.WebApi.Swagger.OptionalSchemaFilter>();

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT access token."
    };
    options.AddSecurityDefinition("Bearer", securityScheme);

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer", document), new List<string>() }
    });
});

// 2 MB for ordinary requests. Attachment upload raises its own ceiling from
// FileStorage:MaxBytes plus a 1 MB multipart envelope. Default MaxBytes is 10 MB,
// so that route's ceiling is 11 MB unless MaxBytes is configured higher.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = FileStorageSettings.GlobalRequestBodyLimitBytes);

builder.Services.Configure<AttachmentRateLimitSettings>(
    builder.Configuration.GetSection(AttachmentRateLimitSettings.SectionName));
builder.Services.Configure<CommentRateLimitSettings>(
    builder.Configuration.GetSection(CommentRateLimitSettings.SectionName));
builder.Services.Configure<AuthRateLimitSettings>(
    builder.Configuration.GetSection(AuthRateLimitSettings.SectionName));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = "Too many requests.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["code"] = "rate_limited";
        problem.Extensions["scope"] = RateLimitScope(context.HttpContext);
        await context.HttpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
    };
    options.AddPolicy("attachments", httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<AttachmentRateLimitSettings>>().Value;
        return FixedWindow(httpContext, settings.PermitLimit, settings.WindowSeconds);
    });
    options.AddPolicy("comments", httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<CommentRateLimitSettings>>().Value;
        return FixedWindow(httpContext, settings.PermitLimit, settings.WindowSeconds);
    });
    options.AddPolicy(AuthRateLimitSettings.PolicyName, httpContext =>
    {
        var settings = httpContext.RequestServices.GetRequiredService<IOptions<AuthRateLimitSettings>>().Value;
        var permit = settings.PermitLimit < 1 ? 1 : settings.PermitLimit;
        var window = settings.WindowSeconds < 1 ? 1 : settings.WindowSeconds;
        return RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(window),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    static RateLimitPartition<string> FixedWindow(HttpContext httpContext, int permitLimit, int windowSeconds)
    {
        var permit = permitLimit < 1 ? 1 : permitLimit;
        var window = windowSeconds < 1 ? 1 : windowSeconds;
        return RateLimitPartition.GetFixedWindowLimiter(
            AttachmentRateLimitPartition.Key(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(window),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    static string ClientIp(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip is null)
            return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.ToString();
    }

    static string RateLimitScope(HttpContext httpContext)
    {
        var policy = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return string.Equals(policy, AuthRateLimitSettings.PolicyName, StringComparison.Ordinal) ? "ip" : "user";
    }
});

builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<IBoardNotifier, SignalRBoardNotifier>();
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.AddSingleton<IRealtimeGroupMembership, SignalRGroupMembership>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:4200", "http://localhost:8080" })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders("Content-Disposition", "Retry-After"));
});

builder.Services.AddWorklaneDataProtection(builder.Environment, builder.Configuration);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerOptionsSetup>();

builder.Services.AddAuthorization();

var app = builder.Build();

// Fail before migrate or listen when the signing key is missing, short, or leaked.
_ = app.Services.GetRequiredService<IOptions<JwtSettings>>().Value;

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Worklane API v1"));

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
    await db.Database.MigrateAsync();
    await DevSeeder.SeedAsync(scope.ServiceProvider);
}

app.UseHttpsRedirection();

app.UseCors("Web");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<ContentLengthLimitMiddleware>();

app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

app.Run();

public partial class Program { }
