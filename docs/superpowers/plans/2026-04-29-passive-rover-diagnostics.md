# Passive Rover Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture richer passive rover/base diagnostics in the existing caster so VPS queries and the existing admin UI can explain RTK connection problems after the fact.

**Architecture:** Keep the existing ASP.NET service as the only telemetry collector. Add pure parsing/snapshot helpers for testable behavior, persist compact session/source summaries and diagnostic events through EF Core, expose read-only diagnostics endpoints, then add lightweight React views using the existing dashboard shell.

**Tech Stack:** ASP.NET Core 9, EF Core/Npgsql, xUnit, React 19, Vite, CSS modules, existing Docker Compose deployment.

---

## File Structure

- Create `AgOpenNtripCaster.Server/Services/NTRIP/GgaFrameParser.cs`: pure `$GPGGA` parser and DTO.
- Create `AgOpenNtripCaster.Server/Services/NTRIP/DiagnosticCounters.cs`: small in-memory session/source counter helpers used by `NtripServerService`.
- Create `AgOpenNtripCaster.Server/Services/Diagnostics/IDiagnosticEventService.cs`: interface for low-volume event persistence.
- Create `AgOpenNtripCaster.Server/Services/Diagnostics/DiagnosticEventService.cs`: best-effort EF writer and query helper.
- Create `AgOpenNtripCaster.Server/Models/Entities/DiagnosticEvent.cs`: structured event entity.
- Create `AgOpenNtripCaster.Server/Models/DTOs/DiagnosticDtos.cs`: API response/request DTOs.
- Create `AgOpenNtripCaster.Server/Controllers/DiagnosticsController.cs`: read-only admin diagnostics endpoints.
- Modify `AgOpenNtripCaster.Server/Models/Entities/ClientSession.cs`: add passive rover summary fields.
- Modify `AgOpenNtripCaster.Server/Models/Entities/SourceConnection.cs`: add passive source summary fields.
- Modify `AgOpenNtripCaster.Server/Data/ApplicationDbContext.cs`: add `DiagnosticEvents` DbSet and relationships/indexes.
- Add EF migration under `AgOpenNtripCaster.Server/Data/Migrations/`: schema for new fields/table.
- Modify `AgOpenNtripCaster.Server/Program.cs`: register diagnostic service.
- Modify `AgOpenNtripCaster.Server/Services/NTRIP/NtripServerService.cs`: wire parser, counters, final persistence, and diagnostic events.
- Test `AgOpenNtripCaster.Server.Tests/Services/NTRIP/GgaFrameParserTests.cs`: parser behavior.
- Test `AgOpenNtripCaster.Server.Tests/Services/NTRIP/DiagnosticCountersTests.cs`: counter/state transitions.
- Create `AgOpenNtripCaster.Client/src/services/diagnosticsApi.ts`: frontend API client.
- Create `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticsPage.tsx`: overview page.
- Create `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticsPage.module.css`: overview styles.
- Create `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticSessionPage.tsx`: session detail/timeline.
- Create `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticSessionPage.module.css`: detail styles.
- Modify `AgOpenNtripCaster.Client/src/App.tsx`: add admin diagnostics routes.
- Modify `AgOpenNtripCaster.Client/src/components/Layout/Sidebar.tsx`: add diagnostics navigation.

## Baseline

- Local frontend baseline passed with `npm run build`.
- Local backend baseline could not run because this environment has no `dotnet` or `docker` executable. Backend tests must be run in an environment with the .NET 9 SDK or on the VPS/container workflow before deployment.

## Task 1: GGA Parser

**Files:**
- Create: `AgOpenNtripCaster.Server/Services/NTRIP/GgaFrameParser.cs`
- Test: `AgOpenNtripCaster.Server.Tests/Services/NTRIP/GgaFrameParserTests.cs`

- [ ] **Step 1: Write failing parser tests**

Add tests:

```csharp
using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class GgaFrameParserTests
{
    [Fact]
    public void TryParse_ExtractsFixQualitySatellitesHdopAltitudeCorrectionAge()
    {
        var ok = GgaFrameParser.TryParse(
            "$GPGGA,123519,4807.038,N,01131.000,E,4,12,0.8,545.4,M,46.9,M,1.5,0001*5A",
            out var frame);

        Assert.True(ok);
        Assert.NotNull(frame);
        Assert.True(frame!.IsPositionValid);
        Assert.Equal(48.1173, frame.Latitude!.Value, 4);
        Assert.Equal(11.5166667, frame.Longitude!.Value, 4);
        Assert.Equal(4, frame.FixQuality);
        Assert.Equal(12, frame.SatelliteCount);
        Assert.Equal(0.8, frame.Hdop);
        Assert.Equal(545.4, frame.AltitudeMeters);
        Assert.Equal(46.9, frame.GeoidSeparationMeters);
        Assert.Equal(1.5, frame.DifferentialAgeSeconds);
        Assert.Equal("0001", frame.DifferentialStationId);
    }

    [Theory]
    [InlineData("$GPGGA,123519,0000.000,N,00000.000,E,0,00,99.9,0.0,M,0.0,M,,*48")]
    [InlineData("$GPGGA,123519,,,,,0,00,99.9,,,,,,*48")]
    public void TryParse_ReturnsFrameButMarksInvalidCoordinates(string sentence)
    {
        var ok = GgaFrameParser.TryParse(sentence, out var frame);

        Assert.True(ok);
        Assert.NotNull(frame);
        Assert.False(frame!.IsPositionValid);
    }

    [Fact]
    public void TryParse_RejectsNonGgaLine()
    {
        Assert.False(GgaFrameParser.TryParse("$GPRMC,ignored", out var frame));
        Assert.Null(frame);
    }
}
```

- [ ] **Step 2: Run parser tests and verify RED**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter GgaFrameParserTests`

Expected: FAIL because `GgaFrameParser` does not exist.

- [ ] **Step 3: Implement parser**

Create `GgaFrameParser.cs` with:

```csharp
using System.Globalization;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

public sealed record GgaFrame(
    string Raw,
    TimeOnly? FixTimeUtc,
    double? Latitude,
    double? Longitude,
    int? FixQuality,
    int? SatelliteCount,
    double? Hdop,
    double? AltitudeMeters,
    double? GeoidSeparationMeters,
    double? DifferentialAgeSeconds,
    string? DifferentialStationId)
{
    public bool IsPositionValid =>
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180 &&
        !(Latitude == 0 && Longitude == 0) &&
        FixQuality.GetValueOrDefault() > 0;
}

public static class GgaFrameParser
{
    public static bool TryParse(string? line, out GgaFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("$GPGGA", StringComparison.OrdinalIgnoreCase))
            return false;

        var noChecksum = line.Split('*', 2)[0];
        var parts = noChecksum.Split(',');
        if (parts.Length < 15)
            return false;

        var lat = TryParseCoordinate(parts[2], parts[3], isLatitude: true);
        var lon = TryParseCoordinate(parts[4], parts[5], isLatitude: false);

        frame = new GgaFrame(
            Raw: line,
            FixTimeUtc: TryParseTime(parts[1]),
            Latitude: lat,
            Longitude: lon,
            FixQuality: TryParseInt(parts[6]),
            SatelliteCount: TryParseInt(parts[7]),
            Hdop: TryParseDouble(parts[8]),
            AltitudeMeters: TryParseDouble(parts[9]),
            GeoidSeparationMeters: TryParseDouble(parts[11]),
            DifferentialAgeSeconds: TryParseDouble(parts[13]),
            DifferentialStationId: string.IsNullOrWhiteSpace(parts[14]) ? null : parts[14]);

        return true;
    }

    private static double? TryParseCoordinate(string value, string direction, bool isLatitude)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(direction))
            return null;
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var nmea))
            return null;

        var degrees = isLatitude ? (int)(nmea / 100) : (int)(nmea / 100);
        var minutes = nmea - degrees * 100;
        var decimalDegrees = degrees + minutes / 60.0;

        if (direction is "S" or "W")
            decimalDegrees = -decimalDegrees;
        if (direction is not ("N" or "S" or "E" or "W"))
            return null;

        return decimalDegrees;
    }

    private static TimeOnly? TryParseTime(string value)
    {
        if (value.Length < 6)
            return null;
        if (!int.TryParse(value[..2], out var hh) ||
            !int.TryParse(value.Substring(2, 2), out var mm) ||
            !int.TryParse(value.Substring(4, 2), out var ss))
            return null;
        return new TimeOnly(hh, mm, ss);
    }

    private static int? TryParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? TryParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
```

- [ ] **Step 4: Run parser tests and verify GREEN**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter GgaFrameParserTests`

Expected: PASS.

- [ ] **Step 5: Commit**

Run:

```bash
git add AgOpenNtripCaster.Server/Services/NTRIP/GgaFrameParser.cs AgOpenNtripCaster.Server.Tests/Services/NTRIP/GgaFrameParserTests.cs
git commit -m "feat: parse detailed rover GGA frames"
```

## Task 2: Counter Helpers

**Files:**
- Create: `AgOpenNtripCaster.Server/Services/NTRIP/DiagnosticCounters.cs`
- Test: `AgOpenNtripCaster.Server.Tests/Services/NTRIP/DiagnosticCountersTests.cs`

- [ ] **Step 1: Write failing counter tests**

Add tests for GGA counts, invalid counts, stale transitions, pause duration, byte totals, and peak pending buffers.

- [ ] **Step 2: Run counter tests and verify RED**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter DiagnosticCountersTests`

Expected: FAIL because `RoverDiagnosticCounters` does not exist.

- [ ] **Step 3: Implement counters**

Create immutable-friendly counter classes:

```csharp
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
    public int StalePositionPeriods { get; private set; }
    public double StreamPausedSeconds { get; private set; }
    public int PeakPendingBufferCount { get; private set; }

    public void RecordBytesSent(int bytes) => BytesSent += bytes;
    public void RecordBytesReceived(int bytes) => BytesReceived += bytes;
    public void RecordPendingBufferCount(int pending) => PeakPendingBufferCount = Math.Max(PeakPendingBufferCount, pending);

    public void RecordGga(GgaFrame frame, DateTime now)
    {
        GgaFrameCount++;
        if (!frame.IsPositionValid)
            InvalidGgaFrameCount++;
        FirstGgaAt ??= now;
        LastGgaAt = now;
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
        BytesReceived += bytes;
        RtcmChunkCount++;
        LastRtcmAt = now;
    }

    public void RecordBytesBroadcastToRovers(long bytes) => BytesSent += bytes;
}
```

- [ ] **Step 4: Run counter tests and verify GREEN**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter DiagnosticCountersTests`

Expected: PASS.

- [ ] **Step 5: Commit**

Run:

```bash
git add AgOpenNtripCaster.Server/Services/NTRIP/DiagnosticCounters.cs AgOpenNtripCaster.Server.Tests/Services/NTRIP/DiagnosticCountersTests.cs
git commit -m "feat: track passive diagnostics counters"
```

## Task 3: Diagnostic Entity, DTOs, Migration, And Service

**Files:**
- Create: `AgOpenNtripCaster.Server/Models/Entities/DiagnosticEvent.cs`
- Create: `AgOpenNtripCaster.Server/Models/DTOs/DiagnosticDtos.cs`
- Create: `AgOpenNtripCaster.Server/Services/Diagnostics/IDiagnosticEventService.cs`
- Create: `AgOpenNtripCaster.Server/Services/Diagnostics/DiagnosticEventService.cs`
- Modify: `AgOpenNtripCaster.Server/Models/Entities/ClientSession.cs`
- Modify: `AgOpenNtripCaster.Server/Models/Entities/SourceConnection.cs`
- Modify: `AgOpenNtripCaster.Server/Data/ApplicationDbContext.cs`
- Modify: `AgOpenNtripCaster.Server/Program.cs`
- Add migration: `AgOpenNtripCaster.Server/Data/Migrations/<timestamp>_AddPassiveRoverDiagnostics.cs`

- [ ] **Step 1: Add failing compile-level tests**

Add tests that instantiate `DiagnosticEvent`, `ClientSession`, and `SourceConnection` with new fields. This fails until properties exist.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter Diagnostic`

Expected: FAIL due missing types/properties.

- [ ] **Step 3: Add entities and DTOs**

Add `DiagnosticEvent` with severity/kind/message/data JSON and optional user/session/source/mountpoint relations. Extend sessions/source connections with summary fields from the design.

- [ ] **Step 4: Register DbSet and service**

Add `DbSet<DiagnosticEvent> DiagnosticEvents`, indexes on timestamp/kind/user/session/mountpoint, and `builder.Services.AddScoped<IDiagnosticEventService, DiagnosticEventService>();`.

- [ ] **Step 5: Add migration**

Preferred command:

```bash
dotnet ef migrations add AddPassiveRoverDiagnostics --project AgOpenNtripCaster.Server --output-dir Data/Migrations
```

If `dotnet ef` is unavailable locally, create the migration manually following the current `Data/Migrations` namespace and validate with a later `dotnet build`.

- [ ] **Step 6: Run backend build/tests**

Run: `dotnet test AgOpenNtripCaster.sln`

Expected: PASS.

- [ ] **Step 7: Commit**

Run:

```bash
git add AgOpenNtripCaster.Server AgOpenNtripCaster.Server.Tests
git commit -m "feat: add passive diagnostics data model"
```

## Task 4: Wire Diagnostics Into NTRIP Runtime

**Files:**
- Modify: `AgOpenNtripCaster.Server/Services/NTRIP/NtripServerService.cs`
- Modify: `AgOpenNtripCaster.Server/Services/NTRIP/ConnectionPool.cs`
- Test: `AgOpenNtripCaster.Server.Tests/Services/NTRIP/NtripServerServiceBroadcastTests.cs`

- [ ] **Step 1: Add failing runtime-adjacent tests**

Add tests for `BroadcastRtcmAsync` updating source counters and client pending buffer peaks through in-memory connection info.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test AgOpenNtripCaster.Server.Tests/AgOpenNtripCaster.Server.Tests.csproj --filter NtripServerServiceBroadcastTests`

Expected: FAIL until counters are wired.

- [ ] **Step 3: Add counter dictionaries**

In `NtripServerService`, add dictionaries keyed by `clientId` and `sourceId` for `RoverDiagnosticCounters` and `SourceDiagnosticCounters`.

- [ ] **Step 4: Record lifecycle events**

Record diagnostic events for source/rover connect/disconnect, duplicate source rejection, auth/no-source errors, first GGA, invalid GGA, stream pause/resume, write failures, and startup cleanup.

- [ ] **Step 5: Persist final summaries**

On `MarkClientSessionDisconnectedAsync`, copy counters and last GGA fields into `ClientSession`. On `MarkSourceConnectionDisconnectedAsync`, copy source counters into `SourceConnection`.

- [ ] **Step 6: Run backend tests**

Run: `dotnet test AgOpenNtripCaster.sln`

Expected: PASS.

- [ ] **Step 7: Commit**

Run:

```bash
git add AgOpenNtripCaster.Server AgOpenNtripCaster.Server.Tests
git commit -m "feat: persist rover and base diagnostics"
```

## Task 5: Read-Only Diagnostics API

**Files:**
- Create: `AgOpenNtripCaster.Server/Controllers/DiagnosticsController.cs`
- Modify: `AgOpenNtripCaster.Server/Models/DTOs/DiagnosticDtos.cs`

- [ ] **Step 1: Add DTO mapping tests where practical**

Add tests for DTO helpers if mapping is extracted; otherwise rely on controller compile/build.

- [ ] **Step 2: Implement endpoints**

Implement:

```text
GET /api/admin/diagnostics/overview
GET /api/admin/diagnostics/users/{userId}
GET /api/admin/diagnostics/sessions/{sessionId}
GET /api/admin/diagnostics/mountpoints/{mountPointId}
GET /api/admin/diagnostics/events
```

All endpoints use `[Authorize(Roles = "Admin,ReadOnly")]` and `AsNoTracking()`.

- [ ] **Step 3: Run backend tests/build**

Run: `dotnet test AgOpenNtripCaster.sln`

Expected: PASS.

- [ ] **Step 4: Commit**

Run:

```bash
git add AgOpenNtripCaster.Server
git commit -m "feat: expose passive diagnostics API"
```

## Task 6: Admin Diagnostics UI

**Files:**
- Create: `AgOpenNtripCaster.Client/src/services/diagnosticsApi.ts`
- Create: `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticsPage.tsx`
- Create: `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticsPage.module.css`
- Create: `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticSessionPage.tsx`
- Create: `AgOpenNtripCaster.Client/src/pages/admin/DiagnosticSessionPage.module.css`
- Modify: `AgOpenNtripCaster.Client/src/App.tsx`
- Modify: `AgOpenNtripCaster.Client/src/components/Layout/Sidebar.tsx`

- [ ] **Step 1: Add frontend API client**

Create typed wrappers for overview, session detail, and event filtering.

- [ ] **Step 2: Add overview page**

Show current cards, mountpoint health, recent irregularities, and latest sessions. Use existing dashboard layout and restrained CSS modules.

- [ ] **Step 3: Add session detail page**

Show session summary and chronological events for the selected session.

- [ ] **Step 4: Add routes/nav**

Add `/admin/diagnostics` and `/admin/diagnostics/sessions/:sessionId` routes requiring `Admin` or `ReadOnly`.

- [ ] **Step 5: Run frontend build**

Run: `npm run build` in `AgOpenNtripCaster.Client`.

Expected: PASS.

- [ ] **Step 6: Commit**

Run:

```bash
git add AgOpenNtripCaster.Client
git commit -m "feat: add admin diagnostics views"
```

## Task 7: Verification And Deployment Prep

**Files:**
- Modify only if verification exposes issues.

- [ ] **Step 1: Run backend verification**

Run in an environment with .NET SDK:

```bash
dotnet test AgOpenNtripCaster.sln
```

Expected: PASS.

- [ ] **Step 2: Run frontend verification**

Run:

```bash
cd AgOpenNtripCaster.Client
npm run build
```

Expected: PASS.

- [ ] **Step 3: Validate compose**

Run where Docker is available:

```bash
docker compose -f deploy/docker-compose.yml config
```

Expected: config prints without errors.

- [ ] **Step 4: Commit any fixes**

If verification required changes:

```bash
git add .
git commit -m "fix: complete diagnostics verification"
```

## Self-Review

- Spec coverage: Phase 1 data persistence, diagnostic events, GGA parsing, API, and UI are covered. Grafana, rover agents, and a new CLI are intentionally excluded.
- Placeholder scan: no `TBD`, `TODO`, or deferred implementation placeholders are present.
- Type consistency: parser, counter, entity, service, API, and UI names match the file structure above.
