using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HatidSuki.Application.Common;
using HatidSuki.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QRCoder;

namespace HatidSuki.Infrastructure.Services;

public class JwtOptions
{
    /// <summary>Signing key, at least 32 characters. Supplied by configuration or environment, never committed for production.</summary>
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "hatidsuki";
    public string Audience { get; set; } = "hatidsuki-frontend";
    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 30;
}

public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public TimeSpan RefreshLifetime => TimeSpan.FromDays(_o.RefreshDays);

    public string CreateAccessToken(User user, Workspace workspace)
    {
        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("workspace_id", workspace.Id.ToString()),
            new Claim("role", user.Role.ToString()),
            new Claim("name", user.Name)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(_o.AccessMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreatePlatformAdminToken()
    {
        var claims = new[] { new Claim("sub", "platform-admin"), new Claim("role", "PlatformAdmin"), new Claim("name", "Platform admin") };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key));
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(60), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public class PasswordService : IPasswordService
{
    private readonly PasswordHasher<string> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword("", password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword("", hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}

public class QrCodeService : IQrCodeService
{
    public byte[] Png(string text, int pixelsPerModule)
    {
        using var generator = new QRCodeGenerator();
        // ECC level Q tolerates ~25% damage, so a scuffed printed code still scans.
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(10, "#111111", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
