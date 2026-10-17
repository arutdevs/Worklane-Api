using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using Worklane.Application;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure;
using Worklane.Infrastructure.Persistence;
using Worklane.WebApi.Hubs;
using Worklane.WebApi.Middleware;
using Worklane.WebApi.Seed;
using Worklane.WebApi.Services;

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

// Every other endpoint keeps Kestrel's 30 MB ceiling. Only attachment upload is
// tightened, to the configured file cap plus room for the multipart envelope
// (~11 MB at the default). The per-request value is read from options so test
// overrides of MaxBytes apply.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = FileStorageSettings.GlobalRequestBodyLimitBytes);

builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<IBoardNotifier, SignalRBoardNotifier>();
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.AddSingleton<IRealtimeGroupMembership, SignalRGroupMembership>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Web", policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:4200" })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSettings.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && IsAttachmentUpload(context.Request.Path))
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            var limit = context.RequestServices.GetRequiredService<IOptions<FileStorageSettings>>().Value.AttachmentRequestLimit;
            feature.MaxRequestBodySize = limit;
        }
    }

    await next();
});

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

app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

app.Run();

static bool IsAttachmentUpload(PathString path)
{
    if (!path.HasValue) return false;
    var value = path.Value!;
    const string prefix = "/api/tasks/";
    const string suffix = "/attachments";
    if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        return false;

    var id = value[prefix.Length..^suffix.Length];
    return Guid.TryParse(id, out _);
}

public partial class Program { }
