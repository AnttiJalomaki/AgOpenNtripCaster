using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class DiagnosticCountersTests
{
    [Fact]
    public void RoverCounters_RecordGgaTracksFirstLastAndInvalidFrames()
    {
        var counters = new RoverDiagnosticCounters();
        var first = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc);
        var second = first.AddSeconds(5);

        GgaFrameParser.TryParse(
            "$GPGGA,100000,4807.038,N,01131.000,E,4,12,0.8,545.4,M,46.9,M,1.5,0001*5A",
            out var validFrame);
        GgaFrameParser.TryParse(
            "$GPGGA,100005,0000.000,N,00000.000,E,0,00,99.9,0.0,M,0.0,M,,*48",
            out var invalidFrame);

        counters.RecordGga(validFrame!, first);
        counters.RecordGga(invalidFrame!, second);

        Assert.Equal(2, counters.GgaFrameCount);
        Assert.Equal(1, counters.InvalidGgaFrameCount);
        Assert.Equal(first, counters.FirstGgaAt);
        Assert.Equal(second, counters.LastGgaAt);
        Assert.Equal(validFrame!.Latitude, counters.LastValidLatitude);
        Assert.Equal(validFrame.Longitude, counters.LastValidLongitude);
        Assert.Equal(validFrame.FixQuality, counters.LastFixQuality);
    }

    [Fact]
    public void RoverCounters_RecordPauseResumeOncePerTransition()
    {
        var counters = new RoverDiagnosticCounters();
        var start = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(counters.MarkPaused(start));
        Assert.False(counters.MarkPaused(start.AddSeconds(1)));
        Assert.True(counters.MarkResumed(start.AddSeconds(7)));
        Assert.False(counters.MarkResumed(start.AddSeconds(9)));

        Assert.Equal(1, counters.StalePositionPeriods);
        Assert.Equal(7, counters.StreamPausedSeconds);
    }

    [Fact]
    public void RoverCounters_RecordBytesAndPeakPendingBuffers()
    {
        var counters = new RoverDiagnosticCounters();

        counters.RecordBytesSent(128);
        counters.RecordBytesSent(256);
        counters.RecordBytesReceived(42);
        counters.RecordPendingBufferCount(4);
        counters.RecordPendingBufferCount(2);
        counters.RecordPendingBufferCount(7);

        Assert.Equal(384, counters.BytesSent);
        Assert.Equal(42, counters.BytesReceived);
        Assert.Equal(7, counters.PeakPendingBufferCount);
    }

    [Fact]
    public void SourceCounters_RecordRtcmAndBroadcastBytes()
    {
        var counters = new SourceDiagnosticCounters();
        var now = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc);

        counters.RecordRtcmReceived(300, now);
        counters.RecordRtcmReceived(500, now.AddSeconds(1));
        counters.RecordBytesBroadcastToRovers(1200);

        Assert.Equal(800, counters.BytesReceived);
        Assert.Equal(1200, counters.BytesSent);
        Assert.Equal(2, counters.RtcmChunkCount);
        Assert.Equal(now.AddSeconds(1), counters.LastRtcmAt);
    }
}
