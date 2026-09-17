namespace AgOpenNtripCaster.Server.Models.Entities;

public class DiagnosticEvent
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Severity { get; set; } = "info";
    public string Kind { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ClientSessionId { get; set; }
    public int? SourceConnectionId { get; set; }
    public int? MountPointId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DataJson { get; set; }

    public NtripUser? User { get; set; }
    public ClientSession? ClientSession { get; set; }
    public SourceConnection? SourceConnection { get; set; }
    public MountPoint? MountPoint { get; set; }
}
