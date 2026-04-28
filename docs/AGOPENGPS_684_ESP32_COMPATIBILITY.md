# AgOpenGPS 6.8.4, ESP32-ETH-NTRIP 0.42.1, And AgOpenNtripCaster Compatibility

This document describes the changes needed so this caster can work cleanly with:

- AgOpenGPS 6.8.4 as the rover/client. Do not modify AgOpenGPS.
- example-fork/ESP32-ETH-NTRIP 0.42.1 as the private base station/source.
- example-fork/AgOpenNtripCaster as the owned caster running on a VPS.

The fixed contract should be:

- ESP32 base station uploads corrections to the caster.
- Caster authenticates and decodes the source stream.
- Caster forwards raw RTCM bytes to AgOpenGPS.
- AgOpenGPS stays unchanged.

## Inspected Revisions

Local checkouts used for this investigation:

- `<path-to-AgOpenNtripCaster>`
  - Branch: `develop`
  - Commit: `f0fccc0bf08b6a060000e08f50ca01eb6f4ac430`
- `<path-to-ESP32-ETH-NTRIP>`
  - Branch/tag: `main`, `v0.42.1`
  - Commit: `70cf1e05db71fb83a7381a5ba68f6cc4fdafb890`
- `<path-to-AgOpenGPS-6.8.4>`
  - Tag: `6.8.4`
  - Commit: `83079d3ff663e85260fbeb70263c90e20e765938`

## Important AgOpenGPS Constraints

AgOpenGPS is the fixed piece. Design the caster and ESP32 around it.

AgOpenGPS 6.8.4 sends the rover stream request from
`SourceCode/AgIO/Source/Forms/NTRIPComm.Designer.cs`:

```http
GET /BASE HTTP/1.0
User-Agent: NTRIP AgOpenGPSClient/6.4
Authorization: Basic base64(username:password)
Accept: */*
Connection: close

```

It can also use `HTTP/1.1` depending on the AgOpenGPS NTRIP form setting.

After this request, AgOpenGPS reads the TCP socket and treats received bytes as
the correction stream. It does not decode HTTP chunked transfer encoding.
Therefore:

- Do not send `Transfer-Encoding: chunked` to AgOpenGPS.
- Do not forward ESP32 chunk headers to AgOpenGPS.
- Send raw RTCM bytes after the NTRIP response header.
- Keep the AgOpenGPS NTRIP "Use TCP" option unchecked. In the inspected code,
  checked TCP mode skips the normal NTRIP `GET` authorization request.

AgOpenGPS source table fetching is in
`SourceCode/AgIO/Source/Forms/FormNtrip.cs`. It sends:

```http
GET / HTTP/1.0
User-Agent: NTRIP iter.dk
Accept: */*
Connection: close

```

Then it parses only lines whose first semicolon-separated field is `STR`.
Because AgOpenGPS runs on Windows and splits source-table text using
`Environment.NewLine`, the caster should emit NTRIP protocol text with CRLF
line endings (`\r\n`), not Linux-only LF (`\n`).

## Current ESP32 0.42.1 Source Behavior

The ESP32 firmware supports NTRIP v1 and v2 source upload in
`src/network/ntrip.cpp`.

NTRIP v1 currently sends:

```http
SOURCE <sourcePassword> /BASE
Source-Agent: NTRIP <name>/App Version 0.42.1

<raw RTCM bytes>
```

NTRIP v2 currently sends:

```http
POST /BASE HTTP/1.1
Host: <caster-host>
User-Agent: NTRIP <name>/App Version 0.42.1
Authorization: Basic base64(username:password)
Ntrip-Version: Ntrip/2.0
Connection: close

<chunk-size-hex>\r\n
<RTCM bytes>\r\n
...
```

The inspected `0.42.1` code still writes chunk-framed data for v2 but does not
advertise the body as chunked. Add this header before the blank line:

```http
Transfer-Encoding: chunked
```

Recommended ESP32 v2 request:

```http
POST /BASE HTTP/1.1
Host: caster.example.com
User-Agent: NTRIP ESP32-ETH-NTRIP/App Version 0.42.1
Authorization: Basic base64(BASE:sourcePassword)
Ntrip-Version: Ntrip/2.0
Transfer-Encoding: chunked
Content-Type: gnss/data
Connection: close

```

The ESP32 v2 data writes can stay as chunked:

```text
<hex length>\r\n
<RTCM bytes>\r\n
```

On graceful v2 disconnect, optionally send the HTTP chunk terminator:

```text
0\r\n
\r\n
```

That is not required for continuous streaming, but it is correct chunked
transfer behavior.

## Current Caster Issues

The current `AgOpenNtripCaster.Server/Services/NTRIP/NtripServerService.cs`
needs changes before it is compatible with the ESP32 source side.

Current issues:

1. It routes only `SOURCE` and `GET`; it does not route source `POST`.
2. The `SOURCE` parser expects `SOURCE password mountpoint`, but ESP32 v1 sends
   `SOURCE password /mountpoint` and extra headers.
3. It does not normalize a leading slash on source mountpoints.
4. It sends source success as plain `OK\r\n`. ESP32 accepts `ICY 200`,
   `HTTP/1.1 200`, `HTTP/1.0 200`, or `200 OK`; plain `OK` is not enough.
5. It does not consume source headers before reading the RTCM body. For ESP32
   v1 this means `Source-Agent: ...` can be read as source data.
6. It uses `StreamReader` for request parsing and `NetworkStream` for binary
   body reads. This can lose body bytes if the reader buffers past the header.
7. It reads source data as raw bytes only; it does not decode chunked v2 source
   bodies.
8. Source authentication is internally inconsistent: `MountPoint.SourcePassword`
   is collected and stored, but `NtripAuthenticationService.AuthenticateSourceAsync`
   verifies the owning user's `SourcePassword` instead.
9. Protocol responses are often built using `StringBuilder.AppendLine()`. On
   Rocky/Linux this produces LF-only line endings. NTRIP responses and
   source-table lines should use CRLF.

The client/rover side is closer: it already accepts `GET /mount`, Basic auth,
and streams raw `SharedRtcmBuffer.Data` to clients. Keep that raw behavior.

## Required Caster Changes

### 1. Replace Request Parsing With A Byte-Level NTRIP Header Parser

Do not parse the first line with `StreamReader` and then read binary data from
`NetworkStream`. Implement a small byte-level parser that reads exactly through
the header terminator and does not prefetch body bytes.

Target behavior:

- Read bytes from `NetworkStream` until `\r\n\r\n` or `\n\n`.
- Enforce a header size limit, for example 8192 bytes.
- Decode only the header as ASCII.
- Parse request line and headers case-insensitively.
- Leave all body bytes in the network stream.

Recommended parsed model:

```csharp
private sealed record NtripRequest(
    string Method,
    string Path,
    string? HttpVersion,
    Dictionary<string, string> Headers,
    string? SourcePassword,
    string? BasicUsername,
    string? BasicPassword);
```

Routing should become:

```text
SOURCE -> source upload, NTRIP v1
POST   -> source upload, NTRIP v2
GET /  -> sourcetable
GET /X -> rover/client stream
```

Keep parsing tolerant enough for:

```text
SOURCE password /BASE
SOURCE password /BASE HTTP/1.0
SOURCE password BASE
POST /BASE HTTP/1.1
GET /BASE HTTP/1.0
GET /BASE HTTP/1.1
```

Normalize mountpoints in one helper:

```csharp
private static string NormalizeMountPoint(string value)
{
    value = value.Trim();
    if (value.StartsWith('/')) value = value[1..];

    var queryIndex = value.IndexOf('?');
    if (queryIndex >= 0) value = value[..queryIndex];

    return value;
}
```

Then validate with the same character set the ESP32 and AgOpenGPS can use:

```text
A-Z a-z 0-9 _ . -
```

### 2. Support ESP32 NTRIP v2 Source POST

For v2 source upload, accept:

```http
POST /BASE HTTP/1.1
Authorization: Basic base64(BASE:sourcePassword)
Ntrip-Version: Ntrip/2.0
Transfer-Encoding: chunked
```

The caster should:

1. Extract mountpoint from the URL path.
2. Extract source password from Basic auth password.
3. Optionally require Basic username to equal the mountpoint.
4. Require `Ntrip-Version: Ntrip/2.0`.
5. Require `Transfer-Encoding: chunked` for this ESP32 v2 path.
6. Authenticate.
7. Send an HTTP 200 source success response.
8. Decode chunks and broadcast only the chunk payload bytes.

Recommended v2 success response:

```http
HTTP/1.1 200 OK
Ntrip-Version: Ntrip/2.0
Server: AgOpenNtripCaster/1.0
Connection: close

```

Use CRLF exactly:

```csharp
private const string CrLf = "\r\n";
```

Do not use `AppendLine()` for protocol responses unless you force CRLF.

### 3. Keep ESP32 NTRIP v1 Source As A Fallback

The caster should also support the ESP32 v1 source request:

```http
SOURCE sourcePassword /BASE
Source-Agent: NTRIP ESP32-ETH-NTRIP/App Version 0.42.1

```

For v1:

- Parse source password from the request line.
- Parse mountpoint from the request line.
- Consume and ignore the remaining headers before source body starts.
- Normalize `/BASE` to `BASE`.
- Broadcast raw bytes after the header block.
- Do not forward `Source-Agent` or blank header lines as RTCM.

Recommended v1 source success response:

```http
ICY 200 OK

```

This also satisfies the ESP32 response checker.

### 4. Decode Chunked Source Bodies Before Broadcasting

For v2 source upload, create a chunk decoder. It must handle:

- Hex chunk-size line.
- Optional chunk extensions after `;`.
- Chunk data split across TCP packets.
- The CRLF after chunk data.
- `0\r\n\r\n` as clean end of stream.

Only this should enter the existing RTCM parser and client broadcast path:

```text
<RTCM payload bytes>
```

Never broadcast:

```text
<hex length>\r\n
\r\n
0\r\n\r\n
```

Recommended structure:

```csharp
private async Task HandleSourceStreamRawAsync(...);
private async Task HandleSourceStreamChunkedAsync(...);
private async Task BroadcastRtcmAsync(string sourceId, string mountPointName, byte[] data, int count, ...);
```

Move the existing RTCM parse and `SharedRtcmBuffer` broadcast logic into
`BroadcastRtcmAsync()` so raw v1 and chunked v2 share the same downstream path.

### 5. Fix Source Authentication Semantics

Choose one source-password model and make it consistent.

Recommended for this private full-stack setup:

- Mountpoint identifies the source.
- `MountPoint.SourcePassword` is the source password for that mountpoint.
- ESP32 v2 Basic username is the mountpoint name, for example `BASE`.
- ESP32 v2 Basic password is `MountPoint.SourcePassword`.
- ESP32 v1 `SOURCE` password is `MountPoint.SourcePassword`.

Minimum code change:

- Update `NtripAuthenticationService.AuthenticateSourceAsync()` to verify
  against `mountPoint.SourcePassword`.
- If keeping plaintext temporarily, compare using a constant-time comparison.
- Prefer a later migration to store a hashed per-mountpoint source password.

Current code stores `MountPoint.SourcePassword` in `MountPointService`, but
source authentication verifies the owner's user-wide source password. That will
confuse setup unless it is deliberately documented and exposed in the UI.

### 6. Enforce One Active Source Per Mountpoint

`ConnectionPool.RegisterSource()` currently limits total source count but does
not reject a second active source for the same mountpoint.

Add a per-mountpoint conflict check:

```text
if an active source already exists for BASE, reject the new source
```

Return a clear NTRIP error:

- v2: `HTTP/1.1 409 Conflict\r\n\r\n` or `HTTP/1.1 503 Service Unavailable\r\n\r\n`
- v1: `ERROR - Mount Point Taken\r\n`

For a private setup this prevents accidental duplicate ESP32 connections from
creating unpredictable stream ownership.

### 7. Keep AgOpenGPS Client Streams Raw

Do not chunk rover/client responses.

Current streaming in `StreamRtcmDataAsync()` writes:

```csharp
await stream.WriteAsync(sharedBuffer.Data, cancellationToken);
await stream.FlushAsync(cancellationToken);
```

That is the correct downstream behavior for AgOpenGPS.

Keep the client success response minimal and CRLF-terminated:

```http
ICY 200 OK
Server: AgOpenNtripCaster/1.0
Content-Type: gnss/data

```

Do not add `Transfer-Encoding: chunked`.

AgOpenGPS will likely pass the initial ASCII response header to the GPS output
path. Most receivers ignore this harmless non-RTCM prefix. Keeping the header
short minimizes noise.

### 8. Make Sourcetable Output AgOpenGPS-Friendly

AgOpenGPS source table fetch looks for `STR` lines and reads these fields:

- `STR[1]`: mountpoint
- `STR[3]`: format
- `STR[6]`: navigation system
- `STR[9]`: latitude
- `STR[10]`: longitude

Keep generating valid `STR` entries with at least those fields.

Critical change: force CRLF line endings in the entire sourcetable response.
Do not rely on `StringBuilder.AppendLine()` on Linux.

Recommended response body:

```text
SOURCETABLE 200 OK\r\n
CAS;...\r\n
NET;...\r\n
STR;BASE;...\r\n
ENDSOURCETABLE\r\n
```

`HTTP/1.1 200 OK` plus an NTRIP 2.0 sourcetable body may also work, but
`SOURCETABLE 200 OK` is the safer NTRIP-style first line for older clients.

Only including connected mountpoints is acceptable for AgOpenGPS operation.
If you want AgOpenGPS source selection before the base station is online, list
active configured mountpoints instead of only connected sources.

### 9. Update ESP32 v2 Request Header

In `<path-to-ESP32-ETH-NTRIP>/src/network/ntrip.cpp`,
inside the v2 `POST` request, add:

```c
"Transfer-Encoding: chunked\r\n"
"Content-Type: gnss/data\r\n"
```

The v2 request should become:

```c
bytesWritten = snprintf(serverBuffer, NTRIP_SERVER_BUFFER_SIZE,
    "POST /%s HTTP/1.1\r\n"
    "Host: %s\r\n"
    "User-Agent: NTRIP %s/App Version %s\r\n"
    "Authorization: Basic %s\r\n"
    "Ntrip-Version: Ntrip/2.0\r\n"
    "Transfer-Encoding: chunked\r\n"
    "Content-Type: gnss/data\r\n"
    "Connection: close\r\n\r\n",
    mnt, host,
    settings["ntrip_sName"].as<const char*>(),
    FIRMWARE_VERSION,
    base64Auth.c_str()
);
```

Keep the immediate `client.flush()` and `client2.flush()` calls added in
`0.42.1`; those are useful for real-time correction delivery.

### 10. Configuration Values To Use

Recommended mountpoint:

```text
BASE
```

ESP32 base station:

```text
Caster host: <VPS DNS or IP>
Caster port: 2101
Mountpoint: BASE
NTRIP version: 2
Username: BASE
Password: <mountpoint source password>
```

AgOpenGPS 6.8.4:

```text
NTRIP enabled: yes
Caster IP/DNS: <VPS DNS or IP>
Caster port: 2101
Mount: BASE
Username: <AgOpenNtripCaster user allowed to access BASE>
Password: <that user's login password>
HTTP: 1.0 or 1.1 should both work
Use TCP: unchecked
GGA interval: default 10 seconds is fine
```

If mountpoint client authentication is disabled in the caster, AgOpenGPS still
sends Basic auth. The current caster code still requires a valid username and
password before it checks `RequireClientAuthentication`, so configure a real
caster user either way unless you intentionally change that behavior.

## Test Plan

Add integration tests or a small protocol test harness around the NTRIP TCP
listener. These tests should be runnable before deploying to Hetzner.

### Test 1: AgOpenGPS Sourcetable Fetch

Input:

```http
GET / HTTP/1.0\r\n
User-Agent: NTRIP iter.dk\r\n
Accept: */*\r\n
Connection: close\r\n
\r\n
```

Expected:

- Response uses CRLF.
- Response contains at least one valid `STR;BASE;...` line when source is
  connected, or when configured mountpoints are intentionally shown offline.
- AgOpenGPS can parse the mountpoint list.

### Test 2: AgOpenGPS Rover Stream

Input:

```http
GET /BASE HTTP/1.1\r\n
User-Agent: NTRIP AgOpenGPSClient/6.4\r\n
Authorization: Basic <valid rover credentials>\r\n
Accept: */*\r\n
Connection: close\r\n
\r\n
```

Expected:

- Caster returns `ICY 200 OK`.
- No `Transfer-Encoding: chunked` response header.
- After the response header, bytes sent to the client are raw RTCM only.

### Test 3: ESP32 v2 Source Upload

Input:

```http
POST /BASE HTTP/1.1\r\n
Host: localhost\r\n
User-Agent: NTRIP ESP32-ETH-NTRIP/App Version 0.42.1\r\n
Authorization: Basic <BASE:sourcePassword>\r\n
Ntrip-Version: Ntrip/2.0\r\n
Transfer-Encoding: chunked\r\n
Content-Type: gnss/data\r\n
Connection: close\r\n
\r\n
```

Then send at least two chunks, including a split TCP write:

```text
3\r\n
\xd3\x00\x00\r\n
5\r\n
abcde\r\n
```

Expected:

- Caster returns `HTTP/1.1 200 OK`.
- Source is registered on mountpoint `BASE`.
- Client connected to `GET /BASE` receives only `\xd3\x00\x00abcde`, not the
  chunk headers.

### Test 4: ESP32 v1 Fallback Source Upload

Input:

```http
SOURCE sourcePassword /BASE\r\n
Source-Agent: NTRIP ESP32-ETH-NTRIP/App Version 0.42.1\r\n
\r\n
<raw RTCM bytes>
```

Expected:

- Caster returns `ICY 200 OK`.
- Source is registered on mountpoint `BASE`, not `/BASE`.
- Client connected to `GET /BASE` receives only raw RTCM bytes.
- `Source-Agent` is not forwarded as data.

### Test 5: Authentication Failures

Check:

- Wrong source password is rejected.
- Wrong AgOpenGPS username/password is rejected.
- Source upload to unknown mountpoint is rejected.
- Duplicate source upload to an already-active mountpoint is rejected.

## Deployment Notes For Rocky Linux 10.1

Rocky Linux 10.1 is fine for this stack. Run the caster using Docker Compose
and publish TCP port 2101.

At the Hetzner firewall and on the VPS firewall, allow:

```text
TCP 2101 from the ESP32 base station network and AgOpenGPS tractor network
TCP 80/443 only if using the web UI and TLS
```

For a private caster, restrict `2101/tcp` by source IP if the ESP32 and tractor
have stable public IPs or connect through a VPN.

## Final Recommended Direction

Use NTRIP v2 between ESP32 and caster:

```text
ESP32 -> POST /BASE, chunked body -> caster
```

Use normal AgOpenGPS NTRIP client behavior between AgOpenGPS and caster:

```text
AgOpenGPS -> GET /BASE -> raw RTCM stream
```

The caster is the translation boundary:

```text
decode source chunking, authenticate, then publish raw RTCM
```

Do not try to make AgOpenGPS consume NTRIP v2 chunked output. It does not need
that, and it is the component we are intentionally not modifying.
