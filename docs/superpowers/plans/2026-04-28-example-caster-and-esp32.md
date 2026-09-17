# Example Caster And ESP32 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Patch and deploy the AgOpenNtripCaster fork and ESP32 firmware so `xxxxxx.xxxxxxxxx.com:2101/demo-mount` works with the waiting base station and AgOpenGPS rover.

**Architecture:** The caster NTRIP listener will use byte-level header parsing, route `SOURCE`, `POST`, and `GET`, decode source chunked bodies, and broadcast only raw RTCM payloads. Deployment remains Docker Compose on Rocky Linux with a seeded private user and mountpoint.

**Tech Stack:** ASP.NET Core 9, C#, PostgreSQL, Docker Compose, ESP32 C++ firmware.

---

## Files

- Modify: `AgOpenNtripCaster.Server/Services/NTRIP/NtripServerService.cs`
- Modify: `AgOpenNtripCaster.Server/Services/Auth/NtripAuthenticationService.cs`
- Modify: `AgOpenNtripCaster.Server/Services/NTRIP/ConnectionPool.cs`
- Add: `AgOpenNtripCaster.Server/Services/NTRIP/NtripRequestParser.cs`
- Add: `AgOpenNtripCaster.Server/Services/NTRIP/NtripChunkedStreamReader.cs`
- Add: `AgOpenNtripCaster.Server/Services/NTRIP/NtripProtocol.cs`
- Add: `AgOpenNtripCaster.Server.Tests/Services/NTRIP/NtripRequestParserTests.cs`
- Add: `AgOpenNtripCaster.Server.Tests/Services/NTRIP/NtripChunkedStreamReaderTests.cs`
- Modify: `<path-to-ESP32-ETH-NTRIP>/src/network/ntrip.cpp`
- Modify: ESP32 version definition file found by `rg "0\\.42\\.1|FIRMWARE_VERSION"`
- Create on VPS: `/opt/AgOpenNtripCaster/deploy/.env`

## Task 1: Caster Parser And Chunk Decoder

- [ ] Write tests for parsing `SOURCE`, `POST`, `GET /`, `GET /demo-mount`, Basic auth, leading slash normalization, and chunk payload decoding.
- [ ] Run `dotnet test AgOpenNtripCaster.sln --filter "NtripRequestParserTests|NtripChunkedStreamReaderTests"` and verify the new tests fail because the classes do not exist.
- [ ] Add `NtripProtocol`, `NtripRequestParser`, and `NtripChunkedStreamReader`.
- [ ] Run the focused parser/chunk tests and verify they pass.

## Task 2: Caster Listener Integration

- [ ] Replace `StreamReader` request routing in `NtripServerService` with `NtripRequestParser`.
- [ ] Add `POST` source handling and reuse common source registration/broadcast logic.
- [ ] Decode chunked source data before `SharedRtcmBuffer` broadcast.
- [ ] Emit NTRIP responses and sourcetable with CRLF.
- [ ] Keep rover stream output raw and unchunked.
- [ ] Run `dotnet test AgOpenNtripCaster.sln`.

## Task 3: Authentication And Duplicate Source Rules

- [ ] Update source authentication to validate against `MountPoint.SourcePassword`.
- [ ] Accept v2 Basic usernames `demo-mount` and `demo-user` when the password matches the mountpoint source password.
- [ ] Reject a second active source for the same mountpoint in `ConnectionPool.RegisterSource`.
- [ ] Run `dotnet test AgOpenNtripCaster.sln`.

## Task 4: ESP32 0.42.2

- [ ] Locate firmware version definition and NTRIP v2 request construction.
- [ ] Add `Transfer-Encoding: chunked` and `Content-Type: gnss/data`.
- [ ] Bump version from `0.42.1` to `0.42.2`.
- [ ] Run available firmware compile or, if toolchain is absent, verify by source inspection.

## Task 5: Local Build Verification

- [ ] Run `dotnet test AgOpenNtripCaster.sln`.
- [ ] Run `docker compose -f deploy/docker-compose.yml config`.
- [ ] Run `git diff --check` in both repos.

## Task 6: VPS Deployment

- [ ] Install Docker and Compose plugin on `caster-host-1` if absent.
- [ ] Sync patched caster fork to `/opt/AgOpenNtripCaster`.
- [ ] Create production `.env` with strong DB/JWT values and the requested admin credentials.
- [ ] Start the stack with `docker compose -f deploy/docker-compose.yml up -d --build`.
- [ ] Seed user `demo-user` and mountpoint `demo-mount` with the privately configured source password.
- [ ] Verify `xxxxxx.xxxxxxxxx.com:2101` sourcetable and rover/source handshakes.
