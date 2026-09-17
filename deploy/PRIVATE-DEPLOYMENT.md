# Private caster deployment

Use `docker-compose.private.yml` for a caster reachable only through Tailscale.
Set a DNS-only A record to the server's Tailscale IPv4 address. Clients and the
base station need a working route to that tailnet. Normal Cloudflare proxying
cannot carry the raw NTRIP TCP service on port 2101.

For Internet-connected base stations and tractors, set `NTRIP_PUBLIC_IP` to the
server's public IPv4 address and point the DNS-only caster A record there.
Add `-f docker-compose.public-ntrip.yml` after the private Compose file in
all deployment and systemd commands. This overlay adds the public listener
while retaining the Tailscale listener.
Require authentication on every mountpoint. This exposes only NTRIP port 2101;
the administration UI remains bound to `TAILSCALE_IP` and is accessed using
that address or the device's MagicDNS hostname. Traditional NTRIP connections
send credentials and corrections without TLS; use Tailscale when the client
supports it, or add a TLS listener for clients that support NTRIP TLS.

Build the backend and web images from a reviewed commit using
`build-private-images.sh --tag COMMIT`, then transfer or push both images.
The build needs current Node 24 and .NET 10 LTS base images. Rebuild to apply
runtime and application dependency updates; host package updates do not update
running container images.

Create a root-readable `.env` with `TAILSCALE_IP`, `APP_TAG`, `DB_PASSWORD`,
`JWT_SECRET`, `ADMIN_EMAIL`, `ADMIN_PASSWORD` and `CORS_ORIGINS`. Keep the original
DB and JWT secrets when migrating. For a new installation generate unique
random secrets, for example with `openssl rand -base64 48`.
`REGISTRATION_ENABLED` defaults to false; an administrator can create users.

Create `logs`, `backups`, and `data-protection-keys`, owned by UID/GID 1654
(the .NET container's app user). Keep data-protection keys and database dumps
private. Preserve existing ASP.NET data-protection keys during migration.
Enable Docker's SELinux support on enforcing hosts; the private Compose file
labels its bind mounts and runs the backend without root or capabilities.

Start with:

```sh
docker compose -f docker-compose.private.yml up -d --wait
```

Use a systemd unit ordered after Docker and Tailscale, and wait until the
configured address is present before Compose starts. Verify this after reboot.
Keep root and public SSH available until a fresh non-root administrator login
and sudo work over Tailscale; then apply the host's lockdown policy.

The private web UI uses HTTP inside the encrypted Tailscale tunnel. Do not
publish the administration UI on public interfaces. If public web access is needed,
configure HTTPS and re-evaluate authentication and network exposure first.

Before cutover, make a PostgreSQL custom-format dump, save the existing
configuration and keys, restore into a separate volume, and test admin login,
anonymous access rejection, sourcetable, source upload, and authenticated rover
RTCM delivery. Stop writes to the old database for the final dump. Keep the old
server and backups available until the tractor has been tested.

Security regression checks:

```sh
dotnet test AgOpenNtripCaster.Server.Tests -c Release
dotnet list AgOpenNtripCaster.Server package --vulnerable --include-transitive
cd AgOpenNtripCaster.Client
npm ci --legacy-peer-deps
npm audit
npm run build
```

The live hub requires an Admin or ReadOnly token and accepts no client broadcast
methods. NTRIP headers have a 10-second total deadline; open sockets are capped
at 256 globally and 32 per address (configurable with `NTRIP_MAX_CONNECTIONS`
and `NTRIP_MAX_CONNECTIONS_PER_IP`). Web authentication has bounded request
budgets. These controls complement the tailnet boundary.
