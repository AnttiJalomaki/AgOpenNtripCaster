namespace AgOpenNtripCaster.Server.Models.Entities;

public class ClientSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public int MountPointId { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DisconnectedAt { get; set; }

    // Client identification
    public string? ClientIpAddress { get; set; }
    public string? ClientUserAgent { get; set; }
    public int SerialNumber { get; set; } // Sequential number per user (1st client=1, 2nd=2, etc)

    // Position tracking
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public double? LastAccuracy { get; set; }
    public DateTime? LastPositionAt { get; set; }

    // GGA diagnostics
    public DateTime? FirstGgaAt { get; set; }
    public int GgaFrameCount { get; set; }
    public int InvalidGgaFrameCount { get; set; }
    public int? LastFixQuality { get; set; }
    public int? LastSatelliteCount { get; set; }
    public double? LastHdop { get; set; }
    public double? LastAltitudeMeters { get; set; }
    public double? LastGeoidSeparationMeters { get; set; }
    public double? LastDifferentialAgeSeconds { get; set; }
    public string? LastDifferentialStationId { get; set; }

    // Stream status
    public ClientStreamStatus Status { get; set; } = ClientStreamStatus.Connected;
    public DateTime? LastStreamPauseAt { get; set; }
    public int StalePositionPeriods { get; set; }
    public double StreamPausedSeconds { get; set; }
    public string? DisconnectReason { get; set; }

    // Statistics
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public int PeakPendingBufferCount { get; set; }

    // Relations
    public NtripUser? User { get; set; }
    public MountPoint? MountPoint { get; set; }
}

public enum ClientStreamStatus
{
    Connected,      // Just connected, awaiting position
    Streaming,      // Position fresh, actively streaming RTCM
    Paused,         // Position too old (>15 sec), stream on hold
    Disconnected    // Connection closed
}
