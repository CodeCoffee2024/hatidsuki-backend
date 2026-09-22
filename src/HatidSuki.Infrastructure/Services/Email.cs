using System.Net.Http.Headers;
using System.Net.Http.Json;
using HatidSuki.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HatidSuki.Infrastructure.Services;

public class ResendOptions
{
    public string ApiKey { get; set; } = "";
    public string FromAddress { get; set; } = "orders@example.com";
}

/// <summary>Sends mail through Resend's HTTP API (https://resend.com). Used when Resend:ApiKey is configured.</summary>
public class ResendEmailSender(HttpClient http, IOptions<ResendOptions> options, ILogger<ResendEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string toEmail, string subject, string textBody, CancellationToken ct)
    {
        var o = options.Value;
        http.BaseAddress ??= new Uri("https://api.resend.com/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        try
        {
            var response = await http.PostAsJsonAsync("emails", new { from = o.FromAddress, to = new[] { toEmail }, subject, text = textBody }, ct);
            if (!response.IsSuccessStatusCode)
                log.LogWarning("Resend rejected an email to {Email}: {Status}", toEmail, response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transactional email is best-effort: a provider outage must never fail order placement.
            log.LogWarning(ex, "Could not send email to {Email}.", toEmail);
        }
    }
}

/// <summary>Used until Resend:ApiKey is set (local dev, or a deploy that hasn't configured email yet). Just logs.</summary>
public class NullEmailSender(ILogger<NullEmailSender> log) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string textBody, CancellationToken ct)
    {
        log.LogInformation("Email not sent (no provider configured): to {Email}, subject '{Subject}'.", toEmail, subject);
        return Task.CompletedTask;
    }
}
