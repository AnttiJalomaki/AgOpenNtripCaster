using System.Reflection;
using System.Threading.Channels;
using AgOpenNtripCaster.Server.Hubs;
using AgOpenNtripCaster.Server.Services.Notifications;
using AgOpenNtripCaster.Server.Services.NTRIP;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public sealed class NtripServerServiceBroadcastTests
{
    [Fact]
    public async Task BroadcastRtcmAsync_KeepsSourceReferenceWhileQueueingClientBuffers()
    {
        var connectionPool = new ConnectionPool(NullLogger<ConnectionPool>.Instance);
        var service = new NtripServerService(
            NullLogger<NtripServerService>.Instance,
            new EmptyServiceProvider(),
            connectionPool,
            new NullHubContext(),
            new ConfigurationManager(),
            new NullTelegramNotificationService());

        connectionPool.RegisterClient("client-1", "demo-mount", "rover-1", new());
        connectionPool.RegisterClient("client-2", "demo-mount", "rover-2", new());

        foreach (var client in connectionPool.GetAllActiveClients())
        {
            client.BufferChannel = new ReleaseOnWriteChannel();
        }

        var broadcast = typeof(NtripServerService).GetMethod(
            "BroadcastRtcmAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(broadcast);

        var task = (Task)broadcast!.Invoke(
            service,
            new object[]
            {
                "source-1",
                "demo-mount",
                new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
                new List<byte>(),
                CancellationToken.None
            })!;

        await task;
    }

    private sealed class ReleaseOnWriteChannel : Channel<SharedRtcmBuffer>
    {
        public ReleaseOnWriteChannel()
        {
            var backingChannel = Channel.CreateUnbounded<SharedRtcmBuffer>();
            Reader = backingChannel.Reader;
            Writer = new ReleaseOnWriteWriter();
        }
    }

    private sealed class ReleaseOnWriteWriter : ChannelWriter<SharedRtcmBuffer>
    {
        public override bool TryComplete(Exception? error = null) => true;

        public override bool TryWrite(SharedRtcmBuffer item)
        {
            item.Release();
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NullTelegramNotificationService : ITelegramNotificationService
    {
        public Task SendNotificationAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendSourceConnectedAsync(string mountPointName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendSourceDisconnectedAsync(string mountPointName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendErrorAsync(string errorMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendSystemStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullHubContext : IHubContext<NtripHub>
    {
        public IHubClients Clients { get; } = new NullHubClients();
        public IGroupManager Groups { get; } = new NullGroupManager();
    }

    private sealed class NullHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullClientProxy();

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NullGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
