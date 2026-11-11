using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Interfaces;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Imports.Interfaces;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Reports.Interfaces;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Multitenancy;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.Infrastructure.Services.Imports;

namespace Worklane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WorklaneDbContext>((sp, options) =>
        {
            var connection = configuration.GetConnectionString("Default");
            // Absent or 0 keeps the non-retrying provider. A positive count is what
            // RegisterAsync has to wrap in CreateExecutionStrategy before BeginTransaction.
            var maxRetryCount = configuration.GetValue("Database:MaxRetryCount", 0);
            if (maxRetryCount > 0)
                options.UseNpgsql(connection, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount));
            else
                options.UseNpgsql(connection);

            // Empty in production. Tests register a gate here to force a slug collision.
            var interceptors = sp.GetServices<ISaveChangesInterceptor>().ToArray();
            if (interceptors.Length > 0)
                options.AddInterceptors(interceptors);
        });

        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<WorklaneDbContext>()
            .AddDefaultTokenProviders();

        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtSettings>, JwtSettingsValidator>();
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.AddOptions<FrontendSettings>()
            .Bind(configuration.GetSection(FrontendSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FrontendSettings>, FrontendSettingsValidator>();
        services.AddOptions<TaskSettings>()
            .Bind(configuration.GetSection(TaskSettings.SectionName))
            .Validate(static settings => settings.MaxChildrenPerParent > 0, "Tasks:MaxChildrenPerParent must be greater than 0.")
            .ValidateOnStart();
        services.AddOptions<ImportSettings>()
            .Bind(configuration.GetSection(ImportSettings.SectionName))
            .Validate(static settings => settings.MaxBytes > 0, "Imports:MaxBytes must be greater than 0.")
            .Validate(static settings => settings.MaxRows > 0, "Imports:MaxRows must be greater than 0.")
            .Validate(static settings => settings.MaxColumns > 0, "Imports:MaxColumns must be greater than 0.")
            .Validate(static settings => settings.MaxUncompressedBytes > 0, "Imports:MaxUncompressedBytes must be greater than 0.")
            .Validate(static settings => settings.RetentionHours > 0, "Imports:RetentionHours must be greater than 0.")
            .Validate(static settings => settings.MaxOpenBatchesPerProject > 0, "Imports:MaxOpenBatchesPerProject must be greater than 0.")
            .Validate(static settings => settings.CommitCommandTimeoutSeconds > 0, "Imports:CommitCommandTimeoutSeconds must be greater than 0.")
            .Validate(static settings => settings.RealtimeFanoutLimit > 0, "Imports:RealtimeFanoutLimit must be greater than 0.")
            .ValidateOnStart();
        services.AddOptions<FileStorageSettings>()
            .Configure<IConfiguration>((settings, config) =>
            {
                var bound = FileStorageSettings.FromConfiguration(config);
                settings.LocalPath = bound.LocalPath;
                settings.MaxBytes = bound.MaxBytes;
                settings.WorkspaceQuotaBytes = bound.WorkspaceQuotaBytes;
                settings.AllowedContentTypes = bound.AllowedContentTypes;
                settings.AllowedExtensions = bound.AllowedExtensions;
            })
            .Validate(static settings => settings.MaxBytes > 0, "FileStorage:MaxBytes must be greater than 0.")
            .Validate(static settings => settings.WorkspaceQuotaBytes > 0, "FileStorage:WorkspaceQuotaBytes must be greater than 0.")
            .ValidateOnStart();

        services.AddScoped<ICurrentTenantService, CurrentTenantService>();
        services.AddScoped<ITokenService, JwtTokenService>();

        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IWorkspaceService, WorkspaceService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IBoardService, BoardService>();
        services.AddScoped<ITaskActivityService, TaskActivityService>();
        services.AddScoped<ITaskCommentService, TaskCommentService>();
        services.AddScoped<ITaskAttachmentService, TaskAttachmentService>();
        services.AddWorklaneFileStorage(configuration);
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IProjectExportService, ProjectExportService>();
        services.AddSingleton<ImportRetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<ImportRetentionService>());

        return services;
    }
}
