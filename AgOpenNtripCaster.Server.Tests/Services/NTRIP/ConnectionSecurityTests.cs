using System.Net;
using System.Net.Sockets;
using System.Text;
using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class ConnectionSecurityTests
{
    [Fact]
    public async Task IncompleteHeaderIsRejectedAtEof()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("GET /private HTTP/1.0\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => NtripRequestParser.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderDeadlineClosesSilentAndSlowSenders(bool sendSlowly)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var receiver = await listener.AcceptTcpClientAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sending = Task.Run(async () =>
        {
            try
            {
                while (sendSlowly && !stop.IsCancellationRequested)
                {
                    await sender.GetStream().WriteAsync("G"u8.ToArray(), stop.Token);
                    await Task.Delay(20, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NtripRequestParser.ReadAsync(receiver.GetStream(), stop.Token, headerTimeout: TimeSpan.FromMilliseconds(150)));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "Header deadline must not reset on each byte.");
        stop.Cancel();
        await sending;
    }

    [Fact]
    public void SocketLimitsAreGlobalAndPerAddressAndLeasesReleaseOnce()
    {
        var admission = new ConnectionAdmission(3, 2);
        using var first = admission.TryAcquire("one");
        var second = admission.TryAcquire("one");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(admission.TryAcquire("one"));
        using var third = admission.TryAcquire("two");
        Assert.NotNull(third);
        Assert.Null(admission.TryAcquire("three"));
        second.Dispose();
        second.Dispose();
        using var replacement = admission.TryAcquire("one");
        Assert.NotNull(replacement);
        Assert.Null(admission.TryAcquire("three"));
    }
}
