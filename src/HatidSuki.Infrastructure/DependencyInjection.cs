using HatidSuki.Application.Common;
using HatidSuki.Infrastructure.Persistence;
using HatidSuki.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HatidSuki.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(DatabaseConnection.Resolve(config),
                n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            // Order children (parts, lines) are only ever reached through their tenant-filtered Order.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        // "App:FrontendBaseUrl" is accepted as an alias for "App:PublicBaseUrl" (some deploys were set up with that name).
        services.AddOptions<AppOptions>().Bind(config.GetSection("App")).Configure<IConfiguration>((o, cfg) =>
        {
            var alias = cfg["App:FrontendBaseUrl"];
            if (!string.IsNullOrWhiteSpace(alias)) o.PublicBaseUrl = alias;
        });
        services.Configure<CorsOptions>(config.GetSection("Cors"));
        services.Configure<ResendOptions>(config.GetSection("Resend"));
        services.Configure<PlatformAdminOptions>(config.GetSection("PlatformAdmin"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<IQrCodeService, QrCodeService>();
        if (!string.IsNullOrWhiteSpace(config["Resend:ApiKey"]))
            services.AddHttpClient<IEmailSender, ResendEmailSender>();
        else
            services.AddSingleton<IEmailSender, NullEmailSender>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}

/// <summary>Applies migrations (and optional demo data) before the API starts accepting requests.</summary>
public class DatabaseInitializer(IServiceProvider services, IConfiguration config, IHostEnvironment env, ILogger<DatabaseInitializer> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (config.GetValue<bool>("Database:MigrateOnStartup"))
        {
            log.LogInformation("Applying database migrations…");
            await db.Database.MigrateAsync(ct);
        }

        // Never logs the actual credential — just whether the platform-admin login is even reachable, so a
        // misconfigured deploy (typo'd variable name, never actually redeployed) is obvious from the logs alone.
        var platformAdminConfigured = !string.IsNullOrWhiteSpace(config["PlatformAdmin:Email"]) && !string.IsNullOrWhiteSpace(config["PlatformAdmin:Password"]);
        log.LogInformation("Platform admin login is {Status}.", platformAdminConfigured ? "configured" : "NOT configured (PlatformAdmin__Email / PlatformAdmin__Password not set)");

        // Demo data is deliberately kept out of production (the first registration becomes the real owner) unless
        // Seed:AllowInProduction explicitly overrides that safety, in addition to Seed:Demo turning seeding on at all.
        var wantsSeed = config.GetValue<bool>("Seed:Demo");
        var blockedInProd = env.IsProduction() && !config.GetValue<bool>("Seed:AllowInProduction");
        if (wantsSeed && blockedInProd)
            log.LogWarning("Seed:Demo is set but this is Production without Seed:AllowInProduction — demo data was NOT seeded.");
        else if (wantsSeed)
        {
            log.LogInformation("Seeding demo data if needed…");
            await DemoSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordService>(),
                scope.ServiceProvider.GetRequiredService<IClock>(), ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
