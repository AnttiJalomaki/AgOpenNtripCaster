# Local development

Use the .NET 10 SDK, Node.js 24 LTS, and PostgreSQL 16. The production deployment
is described in [Private deployment](../deploy/PRIVATE-DEPLOYMENT.md).

Create an ignored `.env` in `AgOpenNtripCaster.Server` with the database connection
and unique credentials. `JWT_SECRET` must contain at least 32 bytes; generate it
with `openssl rand -hex 48`. There is no built-in signing key or administrator
password. Example variable names (replace every placeholder):

```dotenv
DB_HOST=localhost
DB_PORT=5432
DB_NAME=ntripcaster
DB_USER=ntripuser
DB_PASSWORD=<your PostgreSQL password>
JWT_SECRET=<generated random signing secret>
Admin__Email=admin@example.invalid
Admin__Password=<unique initial administrator password>
DATA_PROTECTION_PATH=.local/data-protection-keys
CORS_ORIGINS=http://localhost:5173
REGISTRATION_ENABLED=false
```

The PostgreSQL database must already exist and the configured role must own its
schema. The application applies its EF migrations and creates the initial
administrator only when no users exist. Keep `.env` and data-protection keys
out of Git.

Run the backend from its project directory so DotNetEnv finds `.env`:

```sh
cd AgOpenNtripCaster.Server
dotnet run
```

In another terminal:

```sh
cd AgOpenNtripCaster.Client
npm ci --legacy-peer-deps
npm run dev
```

The tracked frontend `.env.development` selects the local API endpoint. Production
uses the same-origin `/api` endpoint through nginx. The UI can manage users while
self-registration is disabled; NTRIP clients authenticate with their username and
password, whereas web login uses their email address.

Run the regression suite and build before deploying:

```sh
dotnet test AgOpenNtripCaster.Server.Tests -c Release
dotnet list AgOpenNtripCaster.Server package --vulnerable --include-transitive
cd AgOpenNtripCaster.Client
npm audit
npm run build
```

The live telemetry hub is reserved for Admin and ReadOnly roles. Ordinary users
can manage their own permitted resources through authenticated API endpoints.
