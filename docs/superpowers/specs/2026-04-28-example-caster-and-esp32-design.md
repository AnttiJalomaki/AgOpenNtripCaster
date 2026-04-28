# Example Caster And ESP32 0.42.2 Design

## Goal

Deploy a private NTRIP caster on `xxxxxx.xxxxxxxxx.com:2101` for mountpoint
`demo-mount`, with AgOpenGPS 6.8.4 as rover and the ESP32-ETH-NTRIP base
station as source.

## Fixed Deployment Contract

- Host: `xxxxxx.xxxxxxxxx.com`
- VPS: `caster-host-1`, Rocky Linux 10.1, public IP `xxx.xxx.xxx.xxx`
- NTRIP port: `2101`
- Mountpoint: `demo-mount`
- Rover username: `demo-user`
- Rover password: configured privately on the VPS
- Source password: configured privately on the VPS

The caster must accept `/demo-mount` and `demo-mount` as the same mountpoint.

## Caster Protocol Design

The caster will parse NTRIP TCP requests at byte level until the header
terminator, then route:

- `SOURCE <password> /demo-mount` as NTRIP v1 source upload.
- `POST /demo-mount HTTP/1.1` as NTRIP v2 source upload.
- `GET /` as sourcetable fetch.
- `GET /demo-mount` as rover stream.

Source upload bodies are binary. The parser must not use a `StreamReader` that
can prefetch body bytes before binary reading begins. V1 source bodies are raw
RTCM after the header block. V2 source bodies are chunked RTCM payloads and the
caster must decode chunks before broadcasting. Rover output stays raw RTCM after
a short NTRIP success header; it must not use HTTP chunked transfer encoding.

All NTRIP protocol responses and sourcetable lines use CRLF (`\r\n`) so
AgOpenGPS on Windows can parse source tables reliably.

## Authentication Design

The private setup uses one mountpoint-level source password:
`MountPoint.SourcePassword`. The source authenticates with either:

- V1 `SOURCE <source-password> /demo-mount`
- V2 Basic auth with the mountpoint source password; username may be either
  `demo-mount` or `demo-user` for tolerance.

The rover authenticates as a normal caster user:
`demo-user:<rover-password>`, then requests `/demo-mount`.

## ESP32 0.42.2 Design

The ESP32 firmware will be bumped to `0.42.2`. Its NTRIP v2 source request will
include:

- `Transfer-Encoding: chunked`
- `Content-Type: gnss/data`

Its existing chunk-framed data writes remain unchanged. On clean disconnect,
the firmware may send `0\r\n\r\n` if the disconnect path is straightforward.
The caster will still tolerate 0.42.1-style chunked bodies that omit the
`Transfer-Encoding` header.

## Deployment Design

The Rocky VPS will run the caster using Docker Compose. The deployment will:

- Install Docker and the Compose plugin if absent.
- Clone or update the patched caster fork on the VPS.
- Create production environment variables for PostgreSQL, JWT, admin user, CORS,
  and NTRIP port.
- Start PostgreSQL, backend, frontend, and nginx containers.
- Seed the `demo-user` user and `demo-mount` mountpoint directly after the
  backend is healthy.
- Open/listen on public TCP port `2101`.

## Verification

Before deployment, local verification must cover:

- Caster build or targeted test harness for NTRIP parsing/chunk decoding.
- ESP32 firmware version/header change inspection.

After deployment, VPS verification must cover:

- DNS resolves `xxxxxx.xxxxxxxxx.com` to `xxx.xxx.xxx.xxx`.
- `GET /` on port `2101` returns a CRLF sourcetable containing `STR;demo-mount`.
- A v2 source `POST /demo-mount` with chunked test bytes is accepted.
- A rover `GET /demo-mount` with Basic auth receives an `ICY 200 OK` response.
