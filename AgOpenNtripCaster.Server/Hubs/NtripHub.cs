using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace AgOpenNtripCaster.Server.Hubs;

[Authorize(Roles = "Admin,ReadOnly")]
public class NtripHub : Hub
{
    private readonly ILogger<NtripHub> _logger;

    public NtripHub(ILogger<NtripHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"SignalR client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"SignalR client disconnected: {Context.ConnectionId}");
        // Note: NTRIP ClientDisconnected events are sent by NtripServerService.MarkClientSessionDisconnectedAsync
        // to include clientId and username for proper dashboard updates
        await base.OnDisconnectedAsync(exception);
    }

    // Only server-side IHubContext publishers may broadcast telemetry.
}
