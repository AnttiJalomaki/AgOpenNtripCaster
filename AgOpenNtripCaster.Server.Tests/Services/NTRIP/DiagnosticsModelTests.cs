using AgOpenNtripCaster.Server.Models.Entities;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class DiagnosticsModelTests
{
    [Fact]
    public void ClientSession_CarriesPassiveRoverDiagnostics()
    {
        var session = new ClientSession
        {
            ClientUserAgent = "NTRIP AgOpenGPS",
            DisconnectReason = "TCP disconnected",
            FirstGgaAt = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc),
            GgaFrameCount = 12,
            InvalidGgaFrameCount = 2,
            LastFixQuality = 4,
            LastSatelliteCount = 18,
            LastHdop = 0.7,
            LastAltitudeMeters = 101.2,
            LastGeoidSeparationMeters = 18.4,
            LastDifferentialAgeSeconds = 1.1,
            LastDifferentialStationId = "0001",
            StalePositionPeriods = 1,
            StreamPausedSeconds = 4.5,
            PeakPendingBufferCount = 3
        };

        Assert.Equal("NTRIP AgOpenGPS", session.ClientUserAgent);
        Assert.Equal("TCP disconnected", session.DisconnectReason);
        Assert.Equal(12, session.GgaFrameCount);
        Assert.Equal(2, session.InvalidGgaFrameCount);
        Assert.Equal(4, session.LastFixQuality);
        Assert.Equal(18, session.LastSatelliteCount);
        Assert.Equal(0.7, session.LastHdop);
        Assert.Equal(101.2, session.LastAltitudeMeters);
        Assert.Equal(18.4, session.LastGeoidSeparationMeters);
        Assert.Equal(1.1, session.LastDifferentialAgeSeconds);
        Assert.Equal("0001", session.LastDifferentialStationId);
        Assert.Equal(1, session.StalePositionPeriods);
        Assert.Equal(4.5, session.StreamPausedSeconds);
        Assert.Equal(3, session.PeakPendingBufferCount);
    }

    [Fact]
    public void SourceConnection_CarriesPassiveSourceDiagnostics()
    {
        var source = new SourceConnection
        {
            DisconnectReason = "Inactive for 35s",
            LastRtcmAt = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc),
            RtcmChunkCount = 42
        };

        Assert.Equal("Inactive for 35s", source.DisconnectReason);
        Assert.Equal(42, source.RtcmChunkCount);
        Assert.NotNull(source.LastRtcmAt);
    }

    [Fact]
    public void DiagnosticEvent_CarriesTimelineReferences()
    {
        var diagnosticEvent = new DiagnosticEvent
        {
            Kind = "StreamPaused",
            Severity = "warning",
            UserId = "user-1",
            ClientSessionId = "session-1",
            SourceConnectionId = 7,
            MountPointId = 3,
            Message = "Rover stream paused because GGA is stale",
            DataJson = "{\"ageSeconds\":16}"
        };

        Assert.Equal("StreamPaused", diagnosticEvent.Kind);
        Assert.Equal("warning", diagnosticEvent.Severity);
        Assert.Equal("user-1", diagnosticEvent.UserId);
        Assert.Equal("session-1", diagnosticEvent.ClientSessionId);
        Assert.Equal(7, diagnosticEvent.SourceConnectionId);
        Assert.Equal(3, diagnosticEvent.MountPointId);
        Assert.Equal("{\"ageSeconds\":16}", diagnosticEvent.DataJson);
    }
}
