using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AgOpenNtripCaster.Server.Services.Auth;

public static class JwtSettings
{
    public static SymmetricSecurityKey SigningKey(IConfiguration configuration)
    {
        var secret = configuration["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32 ||
            secret.Contains("your-secret", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JWT_SECRET must be a unique, randomly generated secret of at least 32 bytes.");

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public static string Issuer(IConfiguration configuration) => configuration["Jwt:Issuer"] ?? "ntripcaster";
    public static string Audience(IConfiguration configuration) => configuration["Jwt:Audience"] ?? "ntripcaster-clients";
}
