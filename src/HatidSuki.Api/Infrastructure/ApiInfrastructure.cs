using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Api.Infrastructure;

/// <summary>Reads who is calling from the validated JWT. Anonymous callers (public pages) have no workspace.</summary>
public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    private string? Claim(string type) => Principal?.FindFirst(type)?.Value;

    public Guid? UserId => Guid.TryParse(Claim("sub"), out var id) ? id : null;
    public Guid? WorkspaceId => Guid.TryParse(Claim("workspace_id"), out var id) ? id : null;
    public Role? Role => Enum.TryParse<Role>(Claim("role"), out var r) ? r : null;
    public string? Name => Claim("name");

    public Guid RequireWorkspaceId() => WorkspaceId ?? throw new ForbiddenException("Please sign in.");
}

/// <summary>Turns exceptions into consistent RFC 7807 problem responses with friendly messages.</summary>
public class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title, errors) = ex switch
        {
            ValidationException v => (400, "Please check the highlighted fields.", Group(v)),
            NotFoundException n => (404, n.Message, null),
            UnauthorizedException u => (401, u.Message, null),
            ForbiddenException f => (403, f.Message, null),
            ConflictException c => (409, c.Message, null),
            DomainException d => (422, d.Message, null),
            DbUpdateConcurrencyException => (409, "Someone else just changed this. We've refreshed it — please try again.", null),
            DbUpdateException => (409, "That conflicts with existing data. Please check and try again.", null),
            _ => (500, "Something went wrong on our side. Please try again.", null)
        };
        if (status == 500) log.LogError(ex, "Unhandled exception for {Path}", http.Request.Path);

        http.Response.StatusCode = status;
        var problem = new ValidationProblemDetails(errors ?? new Dictionary<string, string[]>())
        {
            Status = status, Title = title, Instance = http.Request.Path
        };
        await http.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }

    private static Dictionary<string, string[]> Group(ValidationException v) =>
        v.Errors.GroupBy(e => Camel(e.PropertyName)).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

    private static string Camel(string s) => string.IsNullOrEmpty(s) || s.StartsWith("field:") || s.StartsWith("parts") ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
