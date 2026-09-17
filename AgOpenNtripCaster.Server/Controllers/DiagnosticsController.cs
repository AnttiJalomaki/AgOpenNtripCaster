using AgOpenNtripCaster.Server.Data;
using AgOpenNtripCaster.Server.Models.DTOs;
using AgOpenNtripCaster.Server.Models.Entities;
using AgOpenNtripCaster.Server.Services.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgOpenNtripCaster.Server.Controllers;

[ApiController]
[Route("api/admin/diagnostics")]
[Authorize(Roles = "Admin,ReadOnly")]
public class DiagnosticsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IDiagnosticEventService _diagnosticEventService;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        ApplicationDbContext dbContext,
        IDiagnosticEventService diagnosticEventService,
        ILogger<DiagnosticsController> logger)
    {
        _dbContext = dbContext;
        _diagnosticEventService = diagnosticEventService;
        _logger = logger;
    }

    [HttpGet("overview")]
    [ProducesResponseType(typeof(DiagnosticsOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsOverviewDto>> GetOverview(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var since24h = now.AddHours(-24);
            var staleBefore = now.AddSeconds(-15);

            var mountPoints = await _dbContext.MountPoints
                .AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);

            var activeSourceMountPoints = (await _dbContext.SourceConnections
                    .AsNoTracking()
                    .Where(sc => sc.DisconnectedAt == null)
                    .Select(sc => sc.MountPointId)
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var activeRoversByMountPoint = await _dbContext.ClientSessions
                .AsNoTracking()
                .Where(cs => cs.DisconnectedAt == null)
                .GroupBy(cs => cs.MountPointId)
                .Select(g => new { MountPointId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.MountPointId, x => x.Count, cancellationToken);

            var sourceReconnects24h = await _dbContext.SourceConnections
                .AsNoTracking()
                .Where(sc => sc.ConnectedAt >= since24h)
                .GroupBy(sc => sc.MountPointId)
                .Select(g => new { MountPointId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.MountPointId, x => x.Count, cancellationToken);

            var lastRtcmByMountPoint = await _dbContext.SourceConnections
                .AsNoTracking()
                .Where(sc => sc.LastRtcmAt != null)
                .GroupBy(sc => sc.MountPointId)
                .Select(g => new { MountPointId = g.Key, LastRtcmAt = g.Max(sc => sc.LastRtcmAt) })
                .ToDictionaryAsync(x => x.MountPointId, x => x.LastRtcmAt, cancellationToken);

            var recentSessions = await _dbContext.ClientSessions
                .AsNoTracking()
                .Include(cs => cs.User)
                .Include(cs => cs.MountPoint)
                .OrderByDescending(cs => cs.ConnectedAt)
                .Take(25)
                .ToListAsync(cancellationToken);

            var recentEvents = await _diagnosticEventService.GetEventsAsync(new DiagnosticEventQuery
            {
                Since = since24h,
                Limit = 25
            }, cancellationToken);

            var lastRtcmAt = mountPoints
                .Select(m => m.LastRtcmMessageTime)
                .Concat(lastRtcmByMountPoint.Values)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .DefaultIfEmpty()
                .Max();

            var overview = new DiagnosticsOverviewDto
            {
                ActiveRovers = activeRoversByMountPoint.Values.Sum(),
                ActiveSources = activeSourceMountPoints.Count,
                StaleRovers = await _dbContext.ClientSessions
                    .AsNoTracking()
                    .CountAsync(cs => cs.DisconnectedAt == null &&
                                      (cs.LastPositionAt == null || cs.LastPositionAt < staleBefore), cancellationToken),
                LastRtcmAt = lastRtcmAt == default ? null : lastRtcmAt,
                MountPoints = mountPoints.Select(m => MapMountPointSummary(
                    m,
                    activeSourceMountPoints,
                    activeRoversByMountPoint,
                    sourceReconnects24h,
                    lastRtcmByMountPoint)).ToList(),
                RecentSessions = recentSessions.Select(MapSession).ToList(),
                RecentEvents = recentEvents
            };

            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving diagnostics overview");
            return StatusCode(500, new { message = "Error retrieving diagnostics overview", error = ex.Message });
        }
    }

    [HttpGet("users/{userId}")]
    [ProducesResponseType(typeof(DiagnosticUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiagnosticUserDetailDto>> GetUserDiagnostics(
        [FromRoute] string userId,
        CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
            return NotFound(new { message = "User not found" });

        var now = DateTime.UtcNow;
        var since24h = now.AddHours(-24);
        var since7d = now.AddDays(-7);

        var recentSessions = await _dbContext.ClientSessions
            .AsNoTracking()
            .Include(cs => cs.User)
            .Include(cs => cs.MountPoint)
            .Where(cs => cs.UserId == userId)
            .OrderByDescending(cs => cs.ConnectedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        var sessions7d = _dbContext.ClientSessions
            .AsNoTracking()
            .Where(cs => cs.UserId == userId && cs.ConnectedAt >= since7d);

        var recentEvents = await _diagnosticEventService.GetEventsAsync(new DiagnosticEventQuery
        {
            UserId = userId,
            Since = since7d,
            Limit = 100
        }, cancellationToken);

        var detail = new DiagnosticUserDetailDto
        {
            UserId = user.Id,
            UserName = user.UserName,
            FullName = user.FullName,
            Sessions24h = await _dbContext.ClientSessions
                .AsNoTracking()
                .CountAsync(cs => cs.UserId == userId && cs.ConnectedAt >= since24h, cancellationToken),
            Sessions7d = await sessions7d.CountAsync(cancellationToken),
            BytesSent7d = await sessions7d.SumAsync(cs => cs.BytesSent, cancellationToken),
            BytesReceived7d = await sessions7d.SumAsync(cs => cs.BytesReceived, cancellationToken),
            InvalidGgaFrames7d = await sessions7d.SumAsync(cs => cs.InvalidGgaFrameCount, cancellationToken),
            StalePositionPeriods7d = await sessions7d.SumAsync(cs => cs.StalePositionPeriods, cancellationToken),
            LatestSession = recentSessions.Select(MapSession).FirstOrDefault(),
            RecentSessions = recentSessions.Select(MapSession).ToList(),
            RecentEvents = recentEvents
        };

        return Ok(detail);
    }

    [HttpGet("sessions/{sessionId}")]
    [ProducesResponseType(typeof(DiagnosticSessionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiagnosticSessionDetailDto>> GetSessionDiagnostics(
        [FromRoute] string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _dbContext.ClientSessions
            .AsNoTracking()
            .Include(cs => cs.User)
            .Include(cs => cs.MountPoint)
            .FirstOrDefaultAsync(cs => cs.Id == sessionId, cancellationToken);

        if (session == null)
            return NotFound(new { message = "Session not found" });

        var events = await _diagnosticEventService.GetEventsAsync(new DiagnosticEventQuery
        {
            ClientSessionId = sessionId,
            Limit = 500
        }, cancellationToken);

        return Ok(new DiagnosticSessionDetailDto
        {
            Session = MapSession(session),
            Events = events.OrderBy(e => e.Timestamp).ToList()
        });
    }

    [HttpGet("mountpoints/{mountPointId:int}")]
    [ProducesResponseType(typeof(DiagnosticMountPointDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DiagnosticMountPointDetailDto>> GetMountPointDiagnostics(
        [FromRoute] int mountPointId,
        CancellationToken cancellationToken)
    {
        var mountPoint = await _dbContext.MountPoints
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mountPointId, cancellationToken);

        if (mountPoint == null)
            return NotFound(new { message = "Mount point not found" });

        var summary = await BuildSingleMountPointSummaryAsync(mountPoint, cancellationToken);

        var sourceConnections = await _dbContext.SourceConnections
            .AsNoTracking()
            .Include(sc => sc.MountPoint)
            .Where(sc => sc.MountPointId == mountPointId)
            .OrderByDescending(sc => sc.ConnectedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        var sessions = await _dbContext.ClientSessions
            .AsNoTracking()
            .Include(cs => cs.User)
            .Include(cs => cs.MountPoint)
            .Where(cs => cs.MountPointId == mountPointId)
            .OrderByDescending(cs => cs.ConnectedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        var events = await _diagnosticEventService.GetEventsAsync(new DiagnosticEventQuery
        {
            MountPointId = mountPointId,
            Limit = 100
        }, cancellationToken);

        return Ok(new DiagnosticMountPointDetailDto
        {
            MountPoint = summary,
            RecentSourceConnections = sourceConnections.Select(MapSourceConnection).ToList(),
            RecentSessions = sessions.Select(MapSession).ToList(),
            RecentEvents = events
        });
    }

    [HttpGet("events")]
    [ProducesResponseType(typeof(List<DiagnosticEventDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DiagnosticEventDto>>> GetEvents(
        [FromQuery] DiagnosticEventQuery query,
        CancellationToken cancellationToken)
    {
        var events = await _diagnosticEventService.GetEventsAsync(query, cancellationToken);
        return Ok(events);
    }

    private async Task<DiagnosticMountPointSummaryDto> BuildSingleMountPointSummaryAsync(
        MountPoint mountPoint,
        CancellationToken cancellationToken)
    {
        var since24h = DateTime.UtcNow.AddHours(-24);
        var activeSourceMountPoints = new HashSet<int>();
        if (await _dbContext.SourceConnections
                .AsNoTracking()
                .AnyAsync(sc => sc.MountPointId == mountPoint.Id && sc.DisconnectedAt == null, cancellationToken))
        {
            activeSourceMountPoints.Add(mountPoint.Id);
        }

        var activeRoversByMountPoint = new Dictionary<int, int>
        {
            [mountPoint.Id] = await _dbContext.ClientSessions
                .AsNoTracking()
                .CountAsync(cs => cs.MountPointId == mountPoint.Id && cs.DisconnectedAt == null, cancellationToken)
        };

        var sourceReconnects24h = new Dictionary<int, int>
        {
            [mountPoint.Id] = await _dbContext.SourceConnections
                .AsNoTracking()
                .CountAsync(sc => sc.MountPointId == mountPoint.Id && sc.ConnectedAt >= since24h, cancellationToken)
        };

        var lastRtcmAt = await _dbContext.SourceConnections
            .AsNoTracking()
            .Where(sc => sc.MountPointId == mountPoint.Id && sc.LastRtcmAt != null)
            .OrderByDescending(sc => sc.LastRtcmAt)
            .Select(sc => sc.LastRtcmAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastRtcmByMountPoint = new Dictionary<int, DateTime?>
        {
            [mountPoint.Id] = lastRtcmAt
        };

        return MapMountPointSummary(
            mountPoint,
            activeSourceMountPoints,
            activeRoversByMountPoint,
            sourceReconnects24h,
            lastRtcmByMountPoint);
    }

    private static DiagnosticMountPointSummaryDto MapMountPointSummary(
        MountPoint mountPoint,
        HashSet<int> activeSourceMountPoints,
        IReadOnlyDictionary<int, int> activeRoversByMountPoint,
        IReadOnlyDictionary<int, int> sourceReconnects24h,
        IReadOnlyDictionary<int, DateTime?> lastRtcmByMountPoint)
    {
        lastRtcmByMountPoint.TryGetValue(mountPoint.Id, out var lastSourceRtcmAt);

        return new DiagnosticMountPointSummaryDto
        {
            MountPointId = mountPoint.Id,
            Name = mountPoint.Name,
            SourceOnline = activeSourceMountPoints.Contains(mountPoint.Id),
            ActiveRovers = activeRoversByMountPoint.GetValueOrDefault(mountPoint.Id),
            LastRtcmAt = mountPoint.LastRtcmMessageTime ?? lastSourceRtcmAt,
            SourceReconnects24h = sourceReconnects24h.GetValueOrDefault(mountPoint.Id),
            RtcmLatitude = mountPoint.RtcmLatitude ?? mountPoint.Latitude,
            RtcmLongitude = mountPoint.RtcmLongitude ?? mountPoint.Longitude
        };
    }

    private static DiagnosticSessionSummaryDto MapSession(ClientSession session)
    {
        return new DiagnosticSessionSummaryDto
        {
            Id = session.Id,
            UserId = session.UserId,
            UserName = session.User?.UserName,
            ClientIpAddress = session.ClientIpAddress,
            ClientUserAgent = session.ClientUserAgent,
            MountPointId = session.MountPointId,
            MountPointName = session.MountPoint?.Name,
            SerialNumber = session.SerialNumber,
            ConnectedAt = session.ConnectedAt,
            DisconnectedAt = session.DisconnectedAt,
            Status = session.Status.ToString(),
            DisconnectReason = session.DisconnectReason,
            BytesSent = session.BytesSent,
            BytesReceived = session.BytesReceived,
            LastPositionAt = session.LastPositionAt,
            LastLatitude = session.LastLatitude,
            LastLongitude = session.LastLongitude,
            LastAccuracy = session.LastAccuracy,
            FirstGgaAt = session.FirstGgaAt,
            GgaFrameCount = session.GgaFrameCount,
            InvalidGgaFrameCount = session.InvalidGgaFrameCount,
            LastFixQuality = session.LastFixQuality,
            LastSatelliteCount = session.LastSatelliteCount,
            LastHdop = session.LastHdop,
            LastAltitudeMeters = session.LastAltitudeMeters,
            LastGeoidSeparationMeters = session.LastGeoidSeparationMeters,
            LastDifferentialAgeSeconds = session.LastDifferentialAgeSeconds,
            LastDifferentialStationId = session.LastDifferentialStationId,
            StalePositionPeriods = session.StalePositionPeriods,
            StreamPausedSeconds = session.StreamPausedSeconds,
            PeakPendingBufferCount = session.PeakPendingBufferCount
        };
    }

    private static DiagnosticSourceConnectionDto MapSourceConnection(SourceConnection connection)
    {
        return new DiagnosticSourceConnectionDto
        {
            Id = connection.Id,
            MountPointId = connection.MountPointId,
            MountPointName = connection.MountPoint?.Name,
            ConnectedAt = connection.ConnectedAt,
            DisconnectedAt = connection.DisconnectedAt,
            Status = connection.Status.ToString(),
            DisconnectReason = connection.DisconnectReason,
            BytesReceived = connection.BytesReceived,
            BytesSent = connection.BytesSent,
            LastRtcmAt = connection.LastRtcmAt,
            RtcmChunkCount = connection.RtcmChunkCount
        };
    }
}
