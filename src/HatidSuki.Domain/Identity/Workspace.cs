using System.Text.RegularExpressions;

namespace HatidSuki.Domain.Identity;

/// <summary>A workspace is a tenant: one business. Every tenant-owned row belongs to exactly one.</summary>
public class Workspace : Entity
{
    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved =
        ["admin", "api", "app", "q", "t", "login", "register", "www", "static", "assets", "health"];

    private Workspace() { }

    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string Currency { get; private set; } = "USD";
    public string Timezone { get; private set; } = "UTC";
    /// <summary>Digits only, e.g. "63". Used to turn local phone numbers (0917…) into WhatsApp/SMS links.</summary>
    public string PhoneCountryCode { get; private set; } = "";
    public int NextOrderNumber { get; private set; } = 1;
    public DateTime CreatedAtUtc { get; private set; }

    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug) && !Reserved.Contains(slug);

    public static Workspace Create(string name, string slug, string currency, string timezone, DateTime now)
    {
        if (!IsValidSlug(slug)) throw new DomainException("That business URL name isn't allowed. Use 3–40 letters, digits or hyphens.");
        return new Workspace
        {
            Name = name.Trim(), Slug = slug, Currency = currency.ToUpperInvariant(),
            Timezone = timezone, CreatedAtUtc = now
        };
    }

    public void UpdateProfile(string name, string currency, string timezone, string phoneCountryCode)
    {
        Name = name.Trim(); Currency = currency.ToUpperInvariant(); Timezone = timezone;
        PhoneCountryCode = new string(phoneCountryCode.Where(char.IsDigit).ToArray());
    }
}
