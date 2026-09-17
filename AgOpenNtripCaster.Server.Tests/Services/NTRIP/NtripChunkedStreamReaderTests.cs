using System.Text;
using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class NtripChunkedStreamReaderTests
{
    [Fact]
    public async Task ReadChunksAsync_DecodesChunkPayloadsAndStopsAtTerminator()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "4\r\nRTCM\r\n" +
            "3;ext=value\r\n123\r\n" +
            "0\r\n\r\nTRAILING"));
        var output = new MemoryStream();

        await NtripChunkedStreamReader.ReadChunksAsync(
            stream,
            (payload, _) =>
            {
                output.Write(payload.Span);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("RTCM123", Encoding.ASCII.GetString(output.ToArray()));
        var trailing = new byte[8];
        var count = await stream.ReadAsync(trailing);
        Assert.Equal(8, count);
        Assert.Equal("TRAILING", Encoding.ASCII.GetString(trailing));
    }

    [Fact]
    public async Task ReadChunksAsync_AcceptsLfOnlyChunkLines()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("2\nAB\n0\n\n"));
        var output = new MemoryStream();

        await NtripChunkedStreamReader.ReadChunksAsync(
            stream,
            (payload, _) =>
            {
                output.Write(payload.Span);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("AB", Encoding.ASCII.GetString(output.ToArray()));
    }
}
