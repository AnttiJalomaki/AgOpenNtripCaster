using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgOpenNtripCaster.Server.Hubs;
using AgOpenNtripCaster.Server.Services.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.Auth;

public class HubAuthorizationTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWT_SECRET"] = "test-only-signing-key-with-at-least-32-bytes"
        });
        builder.Services.AddCasterAuthentication(builder.Configuration);
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<NtripHub>("/api/ntrip-hub");
        await app.StartAsync();
        return app;
    }

    private static string Token(IConfiguration config, string role, string? issuer = null) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer ?? JwtSettings.Issuer(config), JwtSettings.Audience(config),
            [new Claim(ClaimTypes.NameIdentifier, "test"), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(JwtSettings.SigningKey(config), SecurityAlgorithms.HmacSha256)));

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("User", null, HttpStatusCode.Forbidden)]
    [InlineData("Admin", "wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("Admin", null, HttpStatusCode.OK)]
    [InlineData("ReadOnly", null, HttpStatusCode.OK)]
    public async Task TelemetryRequiresValidOperatorToken(string? role, string? issuer, HttpStatusCode expected)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        if (role != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(app.Configuration, role, issuer));
        var response = await client.PostAsync("/api/ntrip-hub/negotiate?negotiateVersion=1", null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedWebSocketCannotForgeServerBroadcasts()
    {
        await using var app = await StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = app.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/api/ntrip-hub?access_token=" + Token(app.Configuration, "Admin")), deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[4096];
        await socket.ReceiveAsync(buffer, deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":1,\"invocationId\":\"1\",\"target\":\"OnDashboardStatsUpdate\",\"arguments\":[{}]}\u001e"), WebSocketMessageType.Text, true, deadline.Token);
        var received = await socket.ReceiveAsync(buffer, deadline.Token);
        var message = Encoding.UTF8.GetString(buffer, 0, received.Count);
        using var completion = JsonDocument.Parse(message.TrimEnd('\u001e'));
        Assert.Equal(3, completion.RootElement.GetProperty("type").GetInt32());
        Assert.Equal("1", completion.RootElement.GetProperty("invocationId").GetString());
        Assert.False(string.IsNullOrEmpty(completion.RootElement.GetProperty("error").GetString()));
        Assert.DoesNotContain("DashboardStatsUpdated", message);
        socket.Abort();
    }
}
