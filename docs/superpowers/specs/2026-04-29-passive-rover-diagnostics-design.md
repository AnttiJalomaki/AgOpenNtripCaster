# Passive Rover Diagnostics Design

## Purpose

The caster should become the primary place to understand what happened when a tractor rover has RTK/NTRIP trouble. The design must avoid new maintenance burden: no rover-side agent, no new CLI service, and no extra always-on diagnostics stack for the first phase. The existing ASP.NET backend, PostgreSQL database, React admin UI, Docker deployment, Nginx, and SSH access to the VPS remain the operating surface.

The caster can only record what crosses the NTRIP connection or what it already knows locally. Rover-local AgOpenGPS logs may still be inspected manually over SSH from the laptop when the rover is online, but this feature does not depend on collecting those logs.

## Current Context

The repository already contains a working dashboard product:

- ASP.NET backend with PostgreSQL, Identity/JWT auth, NTRIP listener, SignalR, activity logging, performance sampling, and Docker logs endpoints.
- React UI with user dashboard, admin dashboard, mountpoint management, activity log, analytics, performance, system logs, and database pages.
- Persisted entities for users, mountpoints, rover client sessions, base source connections, activity events, and performance metrics.
- Live in-memory connection state for active rover/source bytes, positions, stream status, and pending buffers.

The deployed VPS already has useful data:

- One admin/user account.
- One mountpoint: `kultajyva`.
- Historical rover sessions and source connections.
- Activity events for rover/base connect and disconnect.
- Periodic performance samples.

Known gap: session/source byte counters are tracked in memory but current database records show zero byte totals. This limits historical analytics.

## Goals

1. Record richer diagnostics passively whenever a rover or base station connects to the caster.
2. Make per-user and per-rover troubleshooting possible from the existing web UI and from direct VPS database/log queries over SSH.
3. Preserve the existing deployment shape and keep maintenance low.
4. Prefer structured persisted data over scraping text logs for analytics.
5. Make irregularities visible: stale position, missing base source, stream pause/resume, reconnect loops, no RTCM flow, write failures, and server restarts.

## Non-Goals

- No new CLI such as `casterctl`.
- No rover-side daemon, agent, or scheduled task.
- No dependency on AgOpenGPS local log ingestion.
- No first-phase Grafana/Prometheus/Loki deployment.
- No public exposure of sensitive diagnostics beyond existing authenticated app access.
- No broad refactor of the NTRIP server beyond the telemetry needed for this feature.

## Data To Capture

### Rover Session Summary

Extend persisted rover session data so each session answers:

- Which user connected.
- Which mountpoint was used.
- Client IP address.
- Optional client software/user-agent if available from NTRIP headers.
- Connected and disconnected time.
- Duration.
- Final status.
- Disconnect reason.
- Bytes sent to rover.
- Bytes received from rover.
- First GGA time.
- Last GGA time.
- Last valid position.
- Number of GGA frames seen.
- Number of invalid GGA frames.
- Number of stale-position periods.
- Total time stream was paused due to stale position.
- Approximate RTCM/correction bytes per second.
- Peak pending buffer count.

### GGA Diagnostics

The current code parses latitude and longitude from `$GPGGA`. Extend parsing to capture:

- UTC fix time from the sentence, when present.
- Fix quality.
- Satellite count.
- HDOP.
- Altitude and geoid separation.
- Differential correction age.
- Differential station ID.
- Whether the position values are valid or zero/invalid.

Persist the latest values on the session and emit diagnostic events for meaningful changes.

### Source/Base Summary

Persist source connection counters and state:

- Connected/disconnected time.
- Disconnect reason.
- Bytes received from base.
- Bytes broadcast to rovers.
- Last RTCM time.
- RTCM message count during connection.
- Approximate RTCM bytes per second.
- Duplicate-source rejection events.

Mountpoint-level existing RTCM fields remain useful: derived base position, last RTCM message time, reference station ID, message count, detected format/nav systems when available.

### Diagnostic Events

Add a compact structured diagnostic event table for timeline queries. This complements the existing `Activities` table, which is user-facing and text-oriented.

Event fields:

- `Id`
- `Timestamp`
- `Severity`: debug/info/warning/error
- `Kind`: enum/string such as `RoverConnected`, `FirstGgaReceived`, `InvalidGga`, `GgaStale`, `StreamPaused`, `StreamResumed`, `RtcmSent`, `RoverDisconnected`, `SourceConnected`, `SourceDisconnected`, `NoSourceForRover`, `DuplicateSourceRejected`, `WriteFailed`, `ServerStartupCleanup`
- `UserId`
- `ClientSessionId`
- `SourceConnectionId`
- `MountPointId`
- `Message`
- `DataJson`: small structured payload for event-specific values

Events should be low-volume. Do not persist every RTCM packet or every GGA frame. Persist transitions, summaries, and periodic samples only when useful.

## User Experience

### Admin Diagnostics Page

Add an admin diagnostics page focused on operation rather than generic analytics:

- Current status cards: active base sources, active rovers, last RTCM age, stale rover count, recent warnings.
- Mountpoint table: base online, last RTCM, base position, active rovers, source reconnect count.
- Recent irregularities: stale GGA, stream pauses, duplicate source attempts, disconnects, write errors.
- Links to user/rover detail views.

### User/Rover Detail View

For a selected user or rover session:

- Current online/offline state.
- Last mountpoint and last position.
- Last GGA quality details.
- Session history with durations and disconnect reasons.
- Timeline combining rover events and base events on the same mountpoint.
- Session detail with final byte totals, correction rate, pause/stale counts, and log-adjacent events.

The first version identifies a rover by user plus session and serial number, because NTRIP does not guarantee a stable hardware ID. Separate caster users per physical rover remain the cleanest low-maintenance identity model when more than one physical rover needs distinct long-term analytics.

## Terminal Troubleshooting

No new command-line product is required. Codex or a human operator can use SSH to query the VPS:

- Docker logs for backend/Nginx.
- PostgreSQL queries against diagnostic events, sessions, source connections, and mountpoints.
- Existing NTRIP sourcetable/handshake checks.

Add a local ignored runbook later if useful, but do not make it part of the runtime system.

## API Shape

Add or extend authenticated admin/read-only endpoints:

- `GET /api/admin/diagnostics/overview`
- `GET /api/admin/diagnostics/users/{userId}`
- `GET /api/admin/diagnostics/sessions/{sessionId}`
- `GET /api/admin/diagnostics/mountpoints/{mountPointId}`
- `GET /api/admin/diagnostics/events?since=...&userId=...&mountPointId=...&sessionId=...`

These endpoints should be read-only. Existing management endpoints remain unchanged.

## Data Flow

1. Rover connects to NTRIP mountpoint.
2. Backend authenticates user and creates `ClientSession`.
3. Backend records diagnostic event `RoverConnected`.
4. Rover sends GGA frames; backend parses and updates session summary fields.
5. Meaningful transitions are recorded as diagnostic events, for example first valid GGA, invalid GGA, stale position, stream pause/resume.
6. Base sends RTCM; backend updates source/mountpoint counters and broadcasts to active rovers.
7. On disconnect or failure, backend persists final byte counters, status, disconnect reason, and summary metrics.
8. Admin UI and SSH queries read persisted summaries/events later.

## Error Handling

- Telemetry persistence must not break NTRIP streaming. Diagnostic writes should be best-effort and logged if they fail.
- Avoid writing to the database per packet. Batch or throttle updates where needed.
- Use bounded event volume for noisy states such as invalid GGA and stale position.
- If the backend restarts, startup cleanup should mark orphaned sessions/source connections with a clear disconnect reason.
- If the source/base is offline while a rover connects, record a structured event so the later timeline explains why corrections did not flow.

## Security And Privacy

- Diagnostics contain position data. Keep detailed diagnostics behind authenticated admin/read-only routes.
- Do not store source passwords or rover credentials in diagnostic events.
- Keep any operator-local SSH runbooks ignored by Git.
- Registration/public access hardening is separate work, but diagnostics should not make the public surface larger.

## Testing

Backend tests:

- GGA parser extracts fix quality, satellites, HDOP, altitude, correction age, station ID, and rejects invalid coordinates.
- Session counters persist on disconnect.
- Source counters persist on disconnect.
- Diagnostic events are written for connect, first GGA, stale/resume, disconnect, and no-source conditions.
- Diagnostic persistence failures do not stop streaming.

Frontend tests/manual checks:

- Diagnostics overview loads with empty data and with existing sessions.
- User/session detail renders timelines in chronological order.
- Filters by user, mountpoint, and date range work.

Deployment checks:

- `docker compose -f deploy/docker-compose.yml config`
- backend tests
- frontend build
- production stack rebuild on VPS
- verify `/api/health`, login, diagnostics endpoint, and NTRIP sourcetable still work.

## Phasing

### Phase 1: Persistence Fixes And Event Foundation

- Add disconnect reason/status fields and final counter persistence for rover/source sessions.
- Add diagnostic event entity, migration, service, and minimal API.
- Extend GGA parsing and store session summary fields.
- Add tests for parser and persistence behavior.

### Phase 2: Dashboard Views

- Add admin diagnostics overview.
- Add user/session detail page.
- Surface irregularities and timeline.
- Keep UI scoped to existing app styling and auth.

### Phase 3: Operational Polish

- Add documented SSH/Postgres queries for incident review.
- Keep diagnostic events indefinitely at first, then add a retention/cleanup policy only if measured event volume justifies it.
- Consider VPS metrics/Grafana only if the built-in diagnostics leave a clear gap.

## Explicit Scope Decisions

- Do not add a dedicated rover label field in phase 1. Use user plus session and serial number.
- Do not add event retention in phase 1. Keep data until volume is known.
- Do not include public `/register` hardening in this implementation plan. Treat it as separate deployment/security work.
