using AgOpenNtripCaster.Server.Services.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.Auth;

public class JwtSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("your-secret-key-here-min-32-chars")]
    public void UnsafeSigningSecretsFailClosed(string? secret)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["JWT_SECRET"] = secret }).Build();
        Assert.Throws<InvalidOperationException>(() => JwtSettings.SigningKey(configuration));
    }
}
