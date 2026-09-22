using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HatidSuki.Application.Platform;

// ---- the one platform-operator login ------------------------------------------------------------

public record PlatformLoginCommand(string Email, string Password) : IRequest<string>;

public class PlatformLoginValidator : AbstractValidator<PlatformLoginCommand>
{
    public PlatformLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class PlatformLoginHandler(IOptions<PlatformAdminOptions> options, ITokenService tokens) : IRequestHandler<PlatformLoginCommand, string>
{
    public Task<string> Handle(PlatformLoginCommand r, CancellationToken ct)
    {
        // Trimmed on both sides: environment-variable UIs (and copy/paste) routinely add invisible leading/trailing
        // whitespace, which would otherwise turn a correct password into a silent, confusing "wrong password".
        var configuredEmail = options.Value.Email.Trim();
        var configuredPassword = options.Value.Password.Trim();
        var emailOk = !string.IsNullOrEmpty(configuredEmail) && FixedTimeEquals(configuredEmail, r.Email.Trim());
        var passwordOk = !string.IsNullOrEmpty(configuredPassword) && FixedTimeEquals(configuredPassword, r.Password.Trim());
        // Fails closed: with no PlatformAdmin:Email/Password configured, this can never succeed, by design.
        if (!emailOk || !passwordOk) throw new UnauthorizedException("Wrong email or password.");
        return Task.FromResult(tokens.CreatePlatformAdminToken());
    }

    // A plain == comparison on secrets leaks timing information; this compares in constant time instead.
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

// ---- every business on the platform --------------------------------------------------------------

public record AdminWorkspaceDto(Guid Id, string Name, string Slug, string Currency, string Timezone, DateTime CreatedAtUtc,
    bool IsSuspended, DateTime? SuspendedAtUtc, string? SuspendedReason, string? OwnerName, string? OwnerEmail,
    int ItemCount, int FormCount, int OrderCount);

public record ListWorkspacesQuery : IRequest<List<AdminWorkspaceDto>>;

public class ListWorkspacesHandler(IAppDbContext db) : IRequestHandler<ListWorkspacesQuery, List<AdminWorkspaceDto>>
{
    public async Task<List<AdminWorkspaceDto>> Handle(ListWorkspacesQuery q, CancellationToken ct)
    {
        var workspaces = await db.Workspaces.AsNoTracking().OrderByDescending(w => w.CreatedAtUtc).ToListAsync(ct);
        var owners = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Role == Role.Owner)
            .ToDictionaryAsync(u => u.WorkspaceId, u => new { u.Name, u.Email }, ct);
        var itemCounts = await db.Items.IgnoreQueryFilters().AsNoTracking().GroupBy(i => i.WorkspaceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var formCounts = await db.Forms.IgnoreQueryFilters().AsNoTracking().GroupBy(f => f.WorkspaceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var orderCounts = await db.Orders.IgnoreQueryFilters().AsNoTracking().GroupBy(o => o.WorkspaceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return workspaces.Select(w =>
        {
            owners.TryGetValue(w.Id, out var owner);
            return new AdminWorkspaceDto(w.Id, w.Name, w.Slug, w.Currency, w.Timezone, w.CreatedAtUtc,
                w.IsSuspended, w.SuspendedAtUtc, w.SuspendedReason, owner?.Name, owner?.Email,
                itemCounts.GetValueOrDefault(w.Id), formCounts.GetValueOrDefault(w.Id), orderCounts.GetValueOrDefault(w.Id));
        }).ToList();
    }
}

// ---- suspend / reactivate --------------------------------------------------------------------

public record SuspendWorkspaceCommand(Guid Id, string? Reason) : IRequest;

public class SuspendWorkspaceHandler(IAppDbContext db, IClock clock) : IRequestHandler<SuspendWorkspaceCommand>
{
    public async Task Handle(SuspendWorkspaceCommand r, CancellationToken ct)
    {
        var ws = await db.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == r.Id, ct) ?? throw new NotFoundException("That business no longer exists.");
        ws.Suspend(r.Reason, clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}

public record ReactivateWorkspaceCommand(Guid Id) : IRequest;

public class ReactivateWorkspaceHandler(IAppDbContext db) : IRequestHandler<ReactivateWorkspaceCommand>
{
    public async Task Handle(ReactivateWorkspaceCommand r, CancellationToken ct)
    {
        var ws = await db.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == r.Id, ct) ?? throw new NotFoundException("That business no longer exists.");
        ws.Reactivate();
        await db.SaveChangesAsync(ct);
    }
}

// ---- delete (irreversible) ---------------------------------------------------------------------

public record DeleteWorkspaceCommand(Guid Id) : IRequest;

public class DeleteWorkspaceHandler(IAppDbContext db) : IRequestHandler<DeleteWorkspaceCommand>
{
    public async Task Handle(DeleteWorkspaceCommand r, CancellationToken ct)
    {
        var ws = await db.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == r.Id, ct) ?? throw new NotFoundException("That business no longer exists.");

        // Loaded and removed one table at a time (not a bulk ExecuteDelete) so this works the same way against every
        // provider, including the in-memory one the test suite uses. One business's data is small; this is plenty fast.
        var formIds = await db.Forms.IgnoreQueryFilters().Where(f => f.WorkspaceId == ws.Id).Select(f => f.Id).ToListAsync(ct);
        db.FormSources.RemoveRange(await db.FormSources.IgnoreQueryFilters().Where(s => formIds.Contains(s.FormId)).ToListAsync(ct));
        // Orders/Forms cascade to their own children (parts/lines/events, versions) at the database level.
        db.Orders.RemoveRange(await db.Orders.IgnoreQueryFilters().Where(o => o.WorkspaceId == ws.Id).ToListAsync(ct));
        db.Forms.RemoveRange(await db.Forms.IgnoreQueryFilters().Where(f => f.WorkspaceId == ws.Id).ToListAsync(ct));
        db.DeliveryLocations.RemoveRange(await db.DeliveryLocations.IgnoreQueryFilters().Where(l => l.WorkspaceId == ws.Id).ToListAsync(ct));
        db.Items.RemoveRange(await db.Items.IgnoreQueryFilters().Where(i => i.WorkspaceId == ws.Id).ToListAsync(ct));
        var userIds = await db.Users.IgnoreQueryFilters().Where(u => u.WorkspaceId == ws.Id).Select(u => u.Id).ToListAsync(ct);
        db.RefreshTokens.RemoveRange(await db.RefreshTokens.IgnoreQueryFilters().Where(t => userIds.Contains(t.UserId)).ToListAsync(ct));
        db.Users.RemoveRange(await db.Users.IgnoreQueryFilters().Where(u => u.WorkspaceId == ws.Id).ToListAsync(ct));
        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(ct);
    }
}
