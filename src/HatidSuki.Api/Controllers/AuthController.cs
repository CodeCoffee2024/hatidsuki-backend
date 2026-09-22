using HatidSuki.Application.Auth;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HatidSuki.Api.Controllers;

public record RegisterRequest(string BusinessName, string Slug, string Name, string Email, string Password, string Currency, string Timezone);
public record LoginRequest(string Email, string Password);
public record SessionResponse(string AccessToken, UserDto User);

[ApiController, Route("api/auth"), EnableRateLimiting("auth")]
public class AuthController(IMediator mediator, IConfiguration config) : ControllerBase
{
    private const string RefreshCookie = "hs_refresh";

    [HttpPost("register"), AllowAnonymous]
    public async Task<SessionResponse> Register(RegisterRequest r, CancellationToken ct) =>
        Session(await mediator.Send(new RegisterCommand(r.BusinessName, r.Slug, r.Name, r.Email, r.Password, r.Currency, r.Timezone), ct));

    [HttpPost("login"), AllowAnonymous]
    public async Task<SessionResponse> Login(LoginRequest r, CancellationToken ct) =>
        Session(await mediator.Send(new LoginCommand(r.Email, r.Password), ct));

    /// <summary>Exchanges the httpOnly refresh cookie for a fresh access token (and rotates the cookie).</summary>
    [HttpPost("refresh"), AllowAnonymous]
    public async Task<SessionResponse> Refresh(CancellationToken ct) =>
        Session(await mediator.Send(new RefreshCommand(Request.Cookies[RefreshCookie]), ct));

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await mediator.Send(new LogoutCommand(Request.Cookies[RefreshCookie]), ct);
        Response.Cookies.Delete(RefreshCookie, CookieOptions(DateTimeOffset.UnixEpoch));
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public Task<UserDto> Me(CancellationToken ct) => mediator.Send(new GetMeQuery(), ct);

    [HttpPut("workspace"), Authorize(Roles = "Owner,Manager")]
    public Task<UserDto> UpdateWorkspace(UpdateWorkspaceCommand c, CancellationToken ct) => mediator.Send(c, ct);

    private SessionResponse Session(AuthResult result)
    {
        // The refresh token never reaches JavaScript: it travels only in this httpOnly cookie.
        Response.Cookies.Append(RefreshCookie, result.RefreshToken,
            CookieOptions(DateTimeOffset.UtcNow.AddDays(config.GetValue("Jwt:RefreshDays", 30))));
        return new SessionResponse(result.AccessToken, result.User);
    }

    private CookieOptions CookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true, Secure = Request.IsHttps,
        // Cross-origin (a CORS setup, not the same-origin proxy) needs SameSite=None, which browsers only honor
        // alongside Secure. Plain HTTP (local dev) falls back to Lax so the cookie still gets set at all.
        SameSite = Request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax,
        Path = "/api/auth", Expires = expires
    };
}
