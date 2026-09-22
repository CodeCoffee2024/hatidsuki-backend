using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Application.Auth;

public record UserDto(Guid Id, string Name, string Email, string Role, Guid WorkspaceId, string WorkspaceName,
    string Currency, string Timezone, string PhoneCountryCode);

/// <summary>The raw refresh token is returned only so the API can put it in an httpOnly cookie.</summary>
public record AuthResult(string AccessToken, string RefreshToken, UserDto User);

internal static class AuthHelpers
{
    public static UserDto ToDto(User u, Workspace w) =>
        new(u.Id, u.Name, u.Email, u.Role.ToString(), w.Id, w.Name, w.Currency, w.Timezone, w.PhoneCountryCode);

    public static async Task<AuthResult> IssueAsync(IAppDbContext db, ITokenService tokens, IClock clock, User user,
        Workspace workspace, CancellationToken ct)
    {
        var refresh = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Create(user.Id, tokens.HashToken(refresh), clock.UtcNow + tokens.RefreshLifetime));
        await db.SaveChangesAsync(ct);
        return new AuthResult(tokens.CreateAccessToken(user, workspace), refresh, ToDto(user, workspace));
    }

    public static bool IsValidTimezone(string tz)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); return true; } catch { return false; }
    }
}

// ---- register ----------------------------------------------------------------------------------

public record RegisterCommand(string BusinessName, string Slug, string Name, string Email, string Password,
    string Currency, string Timezone) : IRequest<AuthResult>;

public class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(80).WithMessage("Enter your business name.");
        RuleFor(x => x.Slug).Must(Workspace.IsValidSlug)
            .WithMessage("Use 3–40 lowercase letters, digits or hyphens (and not a reserved word).");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80).WithMessage("Enter your name.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200).WithMessage("Enter a valid email address.");
        RuleFor(x => x.Password).MinimumLength(10).WithMessage("Use at least 10 characters for your password.");
        RuleFor(x => x.Currency).Length(3).WithMessage("Choose a currency.");
        RuleFor(x => x.Timezone).Must(AuthHelpers.IsValidTimezone).WithMessage("Choose a valid timezone.");
    }
}

public class RegisterHandler(IAppDbContext db, IPasswordService passwords, ITokenService tokens, IClock clock)
    : IRequestHandler<RegisterCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RegisterCommand r, CancellationToken ct)
    {
        var slug = r.Slug.Trim().ToLowerInvariant();
        if (await db.Workspaces.AnyAsync(w => w.Slug == slug, ct)) throw new ConflictException("That business URL name is already taken.");
        var email = User.NormalizeEmail(r.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new ConflictException("An account with that email already exists.");

        var now = clock.UtcNow;
        var workspace = Workspace.Create(r.BusinessName, slug, r.Currency, r.Timezone, now);
        var user = User.Create(workspace.Id, email, r.Name, Role.Owner, now);
        user.SetPasswordHash(passwords.Hash(r.Password));
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        return await AuthHelpers.IssueAsync(db, tokens, clock, user, workspace, ct);
    }
}

// ---- login / refresh / logout / me -------------------------------------------------------------

public record LoginCommand(string Email, string Password) : IRequest<AuthResult>;

public class LoginHandler(IAppDbContext db, IPasswordService passwords, ITokenService tokens, IClock clock)
    : IRequestHandler<LoginCommand, AuthResult>
{
    public async Task<AuthResult> Handle(LoginCommand r, CancellationToken ct)
    {
        var email = User.NormalizeEmail(r.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var now = clock.UtcNow;
        if (user is null || !user.IsActive) throw new UnauthorizedException("Wrong email or password.");
        if (user.IsLocked(now)) throw new UnauthorizedException("Too many failed attempts. Try again in 15 minutes.");
        if (!passwords.Verify(user.PasswordHash, r.Password))
        {
            user.RegisterFailedLogin(now);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException("Wrong email or password.");
        }
        user.RegisterSuccessfulLogin();
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == user.WorkspaceId, ct);
        if (workspace.IsSuspended) throw new UnauthorizedException("This account is suspended. Contact support.");
        return await AuthHelpers.IssueAsync(db, tokens, clock, user, workspace, ct);
    }
}

public record RefreshCommand(string? RefreshToken) : IRequest<AuthResult>;

public class RefreshHandler(IAppDbContext db, ITokenService tokens, IClock clock) : IRequestHandler<RefreshCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RefreshCommand r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.RefreshToken)) throw new UnauthorizedException();
        var hash = tokens.HashToken(r.RefreshToken);
        var now = clock.UtcNow;
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || !stored.IsActive(now)) throw new UnauthorizedException("Your session expired. Please sign in again.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId && u.IsActive, ct)
                   ?? throw new UnauthorizedException();
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == user.WorkspaceId, ct);
        if (workspace.IsSuspended) throw new UnauthorizedException("This account is suspended. Contact support.");
        stored.Revoke(now); // rotation: every refresh token is single-use
        return await AuthHelpers.IssueAsync(db, tokens, clock, user, workspace, ct);
    }
}

public record LogoutCommand(string? RefreshToken) : IRequest;

public class LogoutHandler(IAppDbContext db, ITokenService tokens, IClock clock) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.RefreshToken)) return;
        var hash = tokens.HashToken(r.RefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return;
        stored.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}

public record GetMeQuery : IRequest<UserDto>;

public class GetMeHandler(IAppDbContext db, ICurrentUser current) : IRequestHandler<GetMeQuery, UserDto>
{
    public async Task<UserDto> Handle(GetMeQuery q, CancellationToken ct)
    {
        var id = current.UserId ?? throw new UnauthorizedException();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct) ?? throw new UnauthorizedException();
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == user.WorkspaceId, ct);
        return AuthHelpers.ToDto(user, workspace);
    }
}

// ---- workspace settings ------------------------------------------------------------------------

public record UpdateWorkspaceCommand(string Name, string Currency, string Timezone, string? PhoneCountryCode) : IRequest<UserDto>;

public class UpdateWorkspaceValidator : AbstractValidator<UpdateWorkspaceCommand>
{
    public UpdateWorkspaceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Currency).Length(3);
        RuleFor(x => x.Timezone).Must(AuthHelpers.IsValidTimezone).WithMessage("Choose a valid timezone.");
        RuleFor(x => x.PhoneCountryCode).MaximumLength(4);
    }
}

public class UpdateWorkspaceHandler(IAppDbContext db, ICurrentUser current) : IRequestHandler<UpdateWorkspaceCommand, UserDto>
{
    public async Task<UserDto> Handle(UpdateWorkspaceCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == wsId, ct);
        var user = await db.Users.FirstAsync(u => u.Id == current.UserId, ct);
        workspace.UpdateProfile(r.Name, r.Currency, r.Timezone, r.PhoneCountryCode ?? "");
        await db.SaveChangesAsync(ct);
        return AuthHelpers.ToDto(user, workspace);
    }
}
