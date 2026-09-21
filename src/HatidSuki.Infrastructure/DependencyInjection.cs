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
        services.Configure<AppOptions>(config.GetSection("App"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<IQrCodeService, QrCodeService>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}

/// <summary>Applies migrations (and optional demo data) before the API starts accepting requests.</summary>
public class DatabaseInitializer(IServiceProvider services, IConfiguration config, ILogger<DatabaseInitializer> log) : IHostedService
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
        if (config.GetValue<bool>("Seed:Demo"))
        {
            log.LogInformation("Seeding demo data if needed…");
            await DemoSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordService>(),
                scope.ServiceProvider.GetRequiredService<IClock>(), ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
