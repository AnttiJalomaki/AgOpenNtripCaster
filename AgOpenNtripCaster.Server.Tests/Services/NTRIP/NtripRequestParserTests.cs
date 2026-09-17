using System.Text;
using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class NtripRequestParserTests
{
    [Fact]
    public async Task ReadAsync_ParsesGetMountpointAndLeavesBodyUnread()
    {
        var body = Encoding.ASCII.GetBytes("$GPGGA,position\r\n");
        var bytes = Encoding.ASCII.GetBytes(
            "GET /demo-mount?x=1 HTTP/1.0\r\n" +
            "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("demo-user:source-secret-123")) + "\r\n" +
            "Connection: close\r\n\r\n")
            .Concat(body)
            .ToArray();
        using var stream = new MemoryStream(bytes);

        var request = await NtripRequestParser.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("GET", request.Method);
        Assert.Equal("/demo-mount?x=1", request.Path);
        Assert.Equal("demo-mount", request.MountPoint);
        Assert.Equal("HTTP/1.0", request.HttpVersion);
        Assert.Equal("demo-user", request.BasicUsername);
        Assert.Equal("source-secret-123", request.BasicPassword);

        var remaining = new byte[body.Length];
        var count = await stream.ReadAsync(remaining);
        Assert.Equal(body.Length, count);
        Assert.Equal(body, remaining);
    }

    [Fact]
    public async Task ReadAsync_ParsesSourceV1WithLeadingSlash()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "SOURCE source-secret-123 /demo-mount\r\n" +
            "Source-Agent: NTRIP ESP32-ETH-NTRIP/App Version 0.42.1\r\n\r\n"));

        var request = await NtripRequestParser.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("SOURCE", request.Method);
        Assert.Equal("demo-mount", request.MountPoint);
        Assert.Equal("source-secret-123", request.SourcePassword);
        Assert.Equal("NTRIP ESP32-ETH-NTRIP/App Version 0.42.1", request.Headers["Source-Agent"]);
    }

    [Fact]
    public async Task ReadAsync_ParsesPostV2BasicAuthAndChunkedHeader()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "POST /demo-mount HTTP/1.1\r\n" +
            "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("demo-mount:source-secret-123")) + "\r\n" +
            "Ntrip-Version: Ntrip/2.0\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n"));

        var request = await NtripRequestParser.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("POST", request.Method);
        Assert.Equal("demo-mount", request.MountPoint);
        Assert.True(request.IsChunked);
        Assert.Equal("demo-mount", request.BasicUsername);
        Assert.Equal("source-secret-123", request.BasicPassword);
    }

    [Theory]
    [InlineData("/demo-mount", "demo-mount")]
    [InlineData("demo-mount", "demo-mount")]
    [InlineData("/demo-mount?gga=1", "demo-mount")]
    [InlineData("/", "")]
    public void NormalizeMountPoint_RemovesLeadingSlashAndQuery(string value, string expected)
    {
        Assert.Equal(expected, NtripRequestParser.NormalizeMountPoint(value));
    }
}
