namespace HatidSuki.Domain.Identity;

public class User : Entity
{
    private User() { }

    public Guid WorkspaceId { get; private set; }
    public string Email { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public Role Role { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int FailedLoginCount { get; private set; }
    public DateTime? LockedUntilUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Create(Guid workspaceId, string email, string name, Role role, DateTime now) => new()
    {
        WorkspaceId = workspaceId, Email = NormalizeEmail(email), Name = name.Trim(), Role = role, CreatedAtUtc = now
    };

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public bool IsLocked(DateTime now) => LockedUntilUtc is { } until && until > now;

    public void RegisterFailedLogin(DateTime now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= 5) { LockedUntilUtc = now.AddMinutes(15); FailedLoginCount = 0; }
    }

    public void RegisterSuccessfulLogin() { FailedLoginCount = 0; LockedUntilUtc = null; }
}

public class RefreshToken : Entity
{
    private RefreshToken() { }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public static RefreshToken Create(Guid userId, string tokenHash, DateTime expires) =>
        new() { UserId = userId, TokenHash = tokenHash, ExpiresAtUtc = expires };

    public bool IsActive(DateTime now) => RevokedAtUtc is null && ExpiresAtUtc > now;
    public void Revoke(DateTime now) => RevokedAtUtc ??= now;
}
