using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Interfaces;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Application.Projects.Interfaces;
using Worklane.Application.Reports.Interfaces;
using Worklane.Application.Workspaces.Interfaces;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Multitenancy;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;

namespace Worklane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<WorklaneDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Default")));

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
        services.Configure<FrontendSettings>(configuration.GetSection(FrontendSettings.SectionName));
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
        services.AddScoped<IFileStorage, LocalFileStorage>();
        services.AddScoped<IReportService, ReportService>();

        return services;
    }
}
