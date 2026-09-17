using AgOpenNtripCaster.Server.Models.DTOs;

namespace AgOpenNtripCaster.Server.Services.Diagnostics;

public interface IDiagnosticEventService
{
    Task RecordAsync(CreateDiagnosticEventRequest request, CancellationToken cancellationToken = default);
    Task<List<DiagnosticEventDto>> GetEventsAsync(DiagnosticEventQuery query, CancellationToken cancellationToken = default);
}
