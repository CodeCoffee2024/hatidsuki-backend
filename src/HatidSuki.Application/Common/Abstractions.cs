using FluentValidation;
using FluentValidation.Results;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Orders;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Application.Common;

public interface IAppDbContext
{
    DbSet<Workspace> Workspaces { get; }
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Item> Items { get; }
    DbSet<Form> Forms { get; }
    DbSet<FormVersion> FormVersions { get; }
    DbSet<FormSource> FormSources { get; }
    DbSet<DeliveryLocation> DeliveryLocations { get; }
    DbSet<Order> Orders { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Atomically reserves the next per-workspace order number (safe under concurrent orders).</summary>
    Task<int> NextOrderNumberAsync(Guid workspaceId, CancellationToken ct);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? WorkspaceId { get; }
    Role? Role { get; }
    string? Name { get; }
    /// <summary>The caller's workspace, or throws <see cref="ForbiddenException"/> for anonymous callers.</summary>
    Guid RequireWorkspaceId();
}

public interface IClock { DateTime UtcNow { get; } }

public interface ITokenService
{
    string CreateAccessToken(User user, Workspace workspace);
    /// <summary>A platform-admin token: no workspace_id claim at all, so it can never satisfy RequireWorkspaceId()
    /// and so can never reach a tenant-scoped endpoint, by construction rather than by convention.</summary>
    string CreatePlatformAdminToken();
    /// <summary>A new random refresh token (the raw value goes to the client, only its hash is stored).</summary>
    string CreateRefreshToken();
    string HashToken(string token);
    TimeSpan RefreshLifetime { get; }
}

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public interface IQrCodeService
{
    byte[] Png(string text, int pixelsPerModule);
    string Svg(string text);
}

public class AppOptions
{
    /// <summary>Public address customers reach (encoded into QR codes and tracking links).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:4200";
}

public class CorsOptions
{
    /// <summary>Origins allowed to call the API directly. Not needed when the web app proxies /api/* same-origin.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>Sends transactional email (order confirmations). A no-op implementation is used until a provider is configured.</summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string textBody, CancellationToken ct);
}

/// <summary>The single platform-operator login. Deliberately not a database row: this is one person's own credential,
/// rotated by changing an environment variable, never a self-service account.</summary>
public class PlatformAdminOptions
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

// ---- errors mapped to HTTP status codes by the API ---------------------------------------------

public class NotFoundException(string message = "We couldn't find that.") : Exception(message);
public class ForbiddenException(string message = "You don't have access to that.") : Exception(message);
public class UnauthorizedException(string message = "Please sign in.") : Exception(message);
public class ConflictException(string message) : Exception(message);

/// <summary>Runs FluentValidation validators before any handler. Failures become a 400 with per-field errors.</summary>
public class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct))))
                .SelectMany(r => r.Errors).Where(f => f is not null).ToList();
            if (failures.Count > 0) throw new ValidationException(failures);
        }
        return await next();
    }
}

public static class Problems
{
    /// <summary>Builds a validation exception from plain messages (used for order problems and similar).</summary>
    public static ValidationException Validation(IEnumerable<(string Key, string Message)> problems) =>
        new(problems.Select(p => new ValidationFailure(p.Key, p.Message)));
}

public record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);
