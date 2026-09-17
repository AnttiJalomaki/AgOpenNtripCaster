using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AgOpenNtripCaster.Server.Services.Auth;

public static class AuthenticationRegistration
{
    public static void AddCasterAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtKey = JwtSettings.SigningKey(configuration);
        
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwtKey,
                ValidateIssuer = true,
                ValidIssuer = JwtSettings.Issuer(configuration),
                ValidateAudience = true,
                ValidAudience = JwtSettings.Audience(configuration),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Browser WebSockets cannot set an Authorization header.
                    if (context.Request.Path.StartsWithSegments("/api/ntrip-hub") &&
                        context.Request.Query.TryGetValue("access_token", out var token))
                        context.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
        
    }
}
