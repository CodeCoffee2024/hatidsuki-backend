using HatidSuki.Application.Platform;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HatidSuki.Api.Controllers;

public record PlatformLoginRequest(string Email, string Password);
public record PlatformLoginResponse(string AccessToken);
public record SuspendWorkspaceRequest(string? Reason);

/// <summary>
/// The platform operator's own console: every business on Hatid Suki, not just one workspace. Nothing here is reachable
/// by a regular Owner/Manager/Staff token — the platform-admin token carries no workspace_id claim at all, so the
/// ordinary tenant machinery (RequireWorkspaceId, the EF query filter) refuses it by construction, not just by role check.
/// </summary>
[ApiController, Route("api/platform")]
public class PlatformController(IMediator mediator) : ControllerBase
{
    [HttpPost("auth/login"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<PlatformLoginResponse> Login(PlatformLoginRequest r, CancellationToken ct) =>
        new(await mediator.Send(new PlatformLoginCommand(r.Email, r.Password), ct));

    [HttpGet("workspaces"), Authorize(Roles = "PlatformAdmin")]
    public Task<List<AdminWorkspaceDto>> Workspaces(CancellationToken ct) => mediator.Send(new ListWorkspacesQuery(), ct);

    [HttpPost("workspaces/{id:guid}/suspend"), Authorize(Roles = "PlatformAdmin")]
    public async Task<IActionResult> Suspend(Guid id, SuspendWorkspaceRequest r, CancellationToken ct)
    {
        await mediator.Send(new SuspendWorkspaceCommand(id, r.Reason), ct);
        return NoContent();
    }

    [HttpPost("workspaces/{id:guid}/reactivate"), Authorize(Roles = "PlatformAdmin")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        await mediator.Send(new ReactivateWorkspaceCommand(id), ct);
        return NoContent();
    }

    [HttpDelete("workspaces/{id:guid}"), Authorize(Roles = "PlatformAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await mediator.Send(new DeleteWorkspaceCommand(id), ct);
        return NoContent();
    }
}
