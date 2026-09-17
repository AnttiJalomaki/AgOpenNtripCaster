using AgOpenNtripCaster.Server.Data;
using AgOpenNtripCaster.Server.Models.DTOs;
using AgOpenNtripCaster.Server.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgOpenNtripCaster.Server.Services.Diagnostics;

public class DiagnosticEventService : IDiagnosticEventService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<DiagnosticEventService> _logger;

    public DiagnosticEventService(ApplicationDbContext dbContext, ILogger<DiagnosticEventService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RecordAsync(CreateDiagnosticEventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Kind))
                return;

            var diagnosticEvent = new DiagnosticEvent
            {
                Timestamp = DateTime.UtcNow,
                Severity = NormalizeSeverity(request.Severity),
                Kind = request.Kind,
                UserId = request.UserId,
                ClientSessionId = request.ClientSessionId,
                SourceConnectionId = request.SourceConnectionId,
                MountPointId = request.MountPointId,
                Message = request.Message,
                DataJson = request.DataJson
            };

            _dbContext.DiagnosticEvents.Add(diagnosticEvent);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist diagnostic event {Kind}", request.Kind);
        }
    }

    public async Task<List<DiagnosticEventDto>> GetEventsAsync(
        DiagnosticEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        var events = _dbContext.DiagnosticEvents
            .AsNoTracking()
            .Include(e => e.User)
            .Include(e => e.MountPoint)
            .AsQueryable();

        if (query.Since.HasValue)
            events = events.Where(e => e.Timestamp >= query.Since.Value);

        if (query.Until.HasValue)
            events = events.Where(e => e.Timestamp <= query.Until.Value);

        if (!string.IsNullOrWhiteSpace(query.UserId))
            events = events.Where(e => e.UserId == query.UserId);

        if (!string.IsNullOrWhiteSpace(query.ClientSessionId))
            events = events.Where(e => e.ClientSessionId == query.ClientSessionId);

        if (query.SourceConnectionId.HasValue)
            events = events.Where(e => e.SourceConnectionId == query.SourceConnectionId.Value);

        if (query.MountPointId.HasValue)
            events = events.Where(e => e.MountPointId == query.MountPointId.Value);

        if (!string.IsNullOrWhiteSpace(query.Severity))
            events = events.Where(e => e.Severity == query.Severity);

        if (!string.IsNullOrWhiteSpace(query.Kind))
            events = events.Where(e => e.Kind == query.Kind);

        return await events
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => new DiagnosticEventDto
            {
                Id = e.Id,
                Timestamp = e.Timestamp,
                Severity = e.Severity,
                Kind = e.Kind,
                UserId = e.UserId,
                UserName = e.User != null ? e.User.UserName : null,
                ClientSessionId = e.ClientSessionId,
                SourceConnectionId = e.SourceConnectionId,
                MountPointId = e.MountPointId,
                MountPointName = e.MountPoint != null ? e.MountPoint.Name : null,
                Message = e.Message,
                DataJson = e.DataJson
            })
            .ToListAsync(cancellationToken);
    }

    private static string NormalizeSeverity(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "debug" => "debug",
            "warning" => "warning",
            "error" => "error",
            _ => "info"
        };
    }
}
