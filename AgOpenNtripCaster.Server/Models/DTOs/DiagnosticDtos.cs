namespace AgOpenNtripCaster.Server.Models.DTOs;

public class DiagnosticEventDto
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Severity { get; set; } = "info";
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? ClientSessionId { get; set; }
    public int? SourceConnectionId { get; set; }
    public int? MountPointId { get; set; }
    public string? MountPointName { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DataJson { get; set; }
}

public class DiagnosticEventQuery
{
    public DateTime? Since { get; set; }
    public DateTime? Until { get; set; }
    public string? UserId { get; set; }
    public string? ClientSessionId { get; set; }
    public int? SourceConnectionId { get; set; }
    public int? MountPointId { get; set; }
    public string? Severity { get; set; }
    public string? Kind { get; set; }
    public int Limit { get; set; } = 200;
}

public class CreateDiagnosticEventRequest
{
    public string Severity { get; set; } = "info";
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ClientSessionId { get; set; }
    public int? SourceConnectionId { get; set; }
    public int? MountPointId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DataJson { get; set; }
}

public class DiagnosticsOverviewDto
{
    public int ActiveRovers { get; set; }
    public int ActiveSources { get; set; }
    public int StaleRovers { get; set; }
    public DateTime? LastRtcmAt { get; set; }
    public List<DiagnosticMountPointSummaryDto> MountPoints { get; set; } = new();
    public List<DiagnosticSessionSummaryDto> RecentSessions { get; set; } = new();
    public List<DiagnosticEventDto> RecentEvents { get; set; } = new();
}

public class DiagnosticMountPointSummaryDto
{
    public int MountPointId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool SourceOnline { get; set; }
    public int ActiveRovers { get; set; }
    public DateTime? LastRtcmAt { get; set; }
    public int SourceReconnects24h { get; set; }
    public decimal? RtcmLatitude { get; set; }
    public decimal? RtcmLongitude { get; set; }
}

public class DiagnosticSessionSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ClientUserAgent { get; set; }
    public int MountPointId { get; set; }
    public string? MountPointName { get; set; }
    public int SerialNumber { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DisconnectReason { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public DateTime? LastPositionAt { get; set; }
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public double? LastAccuracy { get; set; }
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
    public int StalePositionPeriods { get; set; }
    public double StreamPausedSeconds { get; set; }
    public int PeakPendingBufferCount { get; set; }
}

public class DiagnosticSessionDetailDto
{
    public DiagnosticSessionSummaryDto Session { get; set; } = new();
    public List<DiagnosticEventDto> Events { get; set; } = new();
}

public class DiagnosticUserDetailDto
{
    public string UserId { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? FullName { get; set; }
    public int Sessions24h { get; set; }
    public int Sessions7d { get; set; }
    public long BytesSent7d { get; set; }
    public long BytesReceived7d { get; set; }
    public int InvalidGgaFrames7d { get; set; }
    public int StalePositionPeriods7d { get; set; }
    public DiagnosticSessionSummaryDto? LatestSession { get; set; }
    public List<DiagnosticSessionSummaryDto> RecentSessions { get; set; } = new();
    public List<DiagnosticEventDto> RecentEvents { get; set; } = new();
}

public class DiagnosticSourceConnectionDto
{
    public int Id { get; set; }
    public int MountPointId { get; set; }
    public string? MountPointName { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DisconnectReason { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public DateTime? LastRtcmAt { get; set; }
    public int RtcmChunkCount { get; set; }
}

public class DiagnosticMountPointDetailDto
{
    public DiagnosticMountPointSummaryDto MountPoint { get; set; } = new();
    public List<DiagnosticSourceConnectionDto> RecentSourceConnections { get; set; } = new();
    public List<DiagnosticSessionSummaryDto> RecentSessions { get; set; } = new();
    public List<DiagnosticEventDto> RecentEvents { get; set; } = new();
}
