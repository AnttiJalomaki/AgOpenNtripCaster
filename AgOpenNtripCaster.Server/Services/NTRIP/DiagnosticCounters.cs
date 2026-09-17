namespace AgOpenNtripCaster.Server.Services.NTRIP;

public sealed class RoverDiagnosticCounters
{
    private DateTime? _pauseStartedAt;

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public int GgaFrameCount { get; private set; }
    public int InvalidGgaFrameCount { get; private set; }
    public DateTime? FirstGgaAt { get; private set; }
    public DateTime? LastGgaAt { get; private set; }
    public double? LastValidLatitude { get; private set; }
    public double? LastValidLongitude { get; private set; }
    public double? LastAccuracy { get; private set; }
    public int? LastFixQuality { get; private set; }
    public int? LastSatelliteCount { get; private set; }
    public double? LastHdop { get; private set; }
    public double? LastAltitudeMeters { get; private set; }
    public double? LastGeoidSeparationMeters { get; private set; }
    public double? LastDifferentialAgeSeconds { get; private set; }
    public string? LastDifferentialStationId { get; private set; }
    public int StalePositionPeriods { get; private set; }
    public double StreamPausedSeconds { get; private set; }
    public int PeakPendingBufferCount { get; private set; }

    public bool HasPausedStream => _pauseStartedAt.HasValue;

    public void RecordBytesSent(int bytes)
    {
        if (bytes > 0)
            BytesSent += bytes;
    }

    public void RecordBytesReceived(int bytes)
    {
        if (bytes > 0)
            BytesReceived += bytes;
    }

    public void RecordPendingBufferCount(int pending) =>
        PeakPendingBufferCount = Math.Max(PeakPendingBufferCount, pending);

    public void RecordGga(GgaFrame frame, DateTime now)
    {
        GgaFrameCount++;
        FirstGgaAt ??= now;
        LastGgaAt = now;

        if (!frame.IsPositionValid)
        {
            InvalidGgaFrameCount++;
            return;
        }

        LastValidLatitude = frame.Latitude;
        LastValidLongitude = frame.Longitude;
        LastFixQuality = frame.FixQuality;
        LastSatelliteCount = frame.SatelliteCount;
        LastHdop = frame.Hdop;
        LastAltitudeMeters = frame.AltitudeMeters;
        LastGeoidSeparationMeters = frame.GeoidSeparationMeters;
        LastDifferentialAgeSeconds = frame.DifferentialAgeSeconds;
        LastDifferentialStationId = frame.DifferentialStationId;
        LastAccuracy = frame.Hdop;
    }

    public bool MarkPaused(DateTime now)
    {
        if (_pauseStartedAt.HasValue)
            return false;

        _pauseStartedAt = now;
        StalePositionPeriods++;
        return true;
    }

    public bool MarkResumed(DateTime now)
    {
        if (!_pauseStartedAt.HasValue)
            return false;

        StreamPausedSeconds += Math.Max(0, (now - _pauseStartedAt.Value).TotalSeconds);
        _pauseStartedAt = null;
        return true;
    }
}

public sealed class SourceDiagnosticCounters
{
    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }
    public DateTime? LastRtcmAt { get; private set; }
    public int RtcmChunkCount { get; private set; }

    public void RecordRtcmReceived(int bytes, DateTime now)
    {
        if (bytes <= 0)
            return;

        BytesReceived += bytes;
        RtcmChunkCount++;
        LastRtcmAt = now;
    }

    public void RecordBytesBroadcastToRovers(long bytes)
    {
        if (bytes > 0)
            BytesSent += bytes;
    }
}
