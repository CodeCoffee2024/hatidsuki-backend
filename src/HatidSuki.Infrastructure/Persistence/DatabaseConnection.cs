using Microsoft.Extensions.Configuration;

namespace HatidSuki.Infrastructure.Persistence;

/// <summary>
/// Works out the PostgreSQL connection string. Local development sets ConnectionStrings:Default. Hosting platforms such as
/// Railway instead provide a single DATABASE_URL like postgresql://user:password@host:5432/dbname, which Npgsql can't read
/// directly, so it is converted here.
/// </summary>
public static class DatabaseConnection
{
    public static string Resolve(IConfiguration config)
    {
        var explicitString = config.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(explicitString)) return explicitString;

        var url = config["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return FromUrl(url);

        throw new InvalidOperationException(
            "No database configured. Set ConnectionStrings__Default, or DATABASE_URL (postgresql://user:password@host:port/database).");
    }

    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("DATABASE_URL must look like postgresql://user:password@host:port/database.");

        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        // "Prefer" uses TLS when the server offers it (public connections) and still works on a private network.
        return $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Prefer;Trust Server Certificate=true";
    }
}
