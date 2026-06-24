# Production Readiness Review

Last reviewed: 2026-06-25.

## Verified Locally

- Solution build passed in Release after stopping the stale local API process that was locking API output DLLs.
- Build emitted `NU1900` warnings only because the restricted local network could not reach `https://api.nuget.org/v3/index.json` for package vulnerability metadata.
- Self-running test harness passed all listed checks.
- Core regression pack passed `20 / 20` named cases and generated `.verification\regression-pack\regression-report.md`, `.csv`, and `.json` outputs.
- Local Admin API responded on `http://127.0.0.1:5117/admin`.
- Admin service status previously reported `ok` and database status reported `ok`; the latest API smoke check confirmed `/admin` HTTP `200`.
- Admin UI layout checks showed no page/header overflow after the API key role and retention form fixes.
- Operations `check-prereqs` from the normal user context reported runtime-folder write failures under `C:\Temp\SignatureVerification` and LocalDB automatic instance failure. This is an environment/hosting issue rather than a compile error; production deployment must set runtime-folder ACLs and use a managed SQL Server service instead of relying on user-scoped LocalDB.

## Current Implementation Status

| Area | Status | Notes |
| --- | --- | --- |
| Core verification engine | Implemented | Deterministic OpenCV-based image comparison with JSON contracts, normalized scoring, structural reject gates, copied-signature risk warning, synthetic tests, and the 20-case regression pack. |
| TotalAgility wrapper | Implemented | Structured request path validates required integration fields. |
| SQL persistence | Implemented | Result records, signature cases, comparisons, reviewer outcomes, workflow objects, and DB-native blobs. |
| DB-native verification | Implemented | `POST /api/v1/verify-db-native` stores document/debug blobs and loads approved SQL references. |
| Installer | Implemented | `scripts\Install-SignatureVerification.ps1` publishes API/operations, applies migrations, writes config, creates runtime folders, and can register a startup task. |
| Admin UI | Implemented and expanded | Includes overview, settings, retention, API keys, users/roles, templates/zones, thresholds, backup/export, service status, and audit. |
| API key management | Implemented | Bootstrap config keys plus DB-managed hashed keys. Generated secrets are shown once. |
| Retention | Implemented | Default policies installed, Admin UI uses policy dropdown, and purge covers filesystem metadata plus DB-native `DocumentBlob`, `DebugArtifactBlob`, and `ReportBlob`. |
| DR/export metadata | Partially implemented | Export package requests and operations export exist; full DR still requires environment backup/restore controls outside the app. |
| Customer sampling | Open by user acknowledgement | Real customer holdout documents are still needed to establish false accept/false reject performance. |

## Core Regression Pack

The regression project is:

```text
StaticSignatureVerification.Regression
```

Run it with:

```powershell
dotnet run --project .\StaticSignatureVerification.Regression\StaticSignatureVerification.Regression.csproj -c Release -- --output=.verification\regression-pack
```

The current pack covers:

- genuine match and wrong-signer rejection
- empty signature region detection
- multiple signatures and correct role/reference mapping
- rotated and skewed signatures
- stamp/handwriting false-positive controls
- low-resolution and cropped-signature review/reject behavior
- invalid Base64 and corrupt PDF error JSON
- large multi-page PDF page mapping
- missing reference error JSON
- reused/copied signature high-risk warning
- box-border removal
- TotalAgility wrapper valid JSON output

Current local result: `20 passed / 20 total`.

## Important Production Gaps

1. LocalDB is suitable for local development only. Production should use managed/shared SQL Server with approved encryption, backup, monitoring, service account, and maintenance configuration.
2. SQL and filesystem replication are not sufficient DR controls by themselves. Add independent point-in-time backups, immutable/offline protection, restore drills, config/secret backup, RPO/RTO, and documented recovery ownership.
3. Admin UI backup/export currently records package requests and metadata. A production deployment still needs a scheduled export/backup job and evidence that restore works.
4. DB-managed API keys are now supported, but production should store generated secrets in an approved vault and define rotation/revocation procedures.
5. Threshold profiles and form templates can be administered, and synthetic regression currently passes. Production threshold approval still needs customer-labelled validation data.
6. Service hosting on `http://127.0.0.1:5117` is a local/development profile. Production should host behind IIS, Windows service, reverse proxy, or an approved service manager with TLS on `443`.
7. Runtime folders must grant write access to the service identity for `Input`, `Output`, `ReferenceSignatures`, `ReferenceStore`, report/export folders, and transient debug storage.

## Database Migrations

Current migrations:

- `001_initial_schema.sql`
- `002_production_workflow_schema.sql`
- `003_workflow_schema_fixups.sql`
- `004_database_native_storage.sql`
- `005_admin_retention_security.sql`
- `006_seed_verifier_role.sql`

Migration `005` adds DB-managed API keys, default retention policies, and purge/export settings. Migration `006` ensures the `Verifier` role is present for existing databases.

## Operational Recommendations

- Use the installer for controlled server deployment.
- Run `scripts\Initialize-Database.ps1` during deployment and verify `ssv.SchemaMigration`.
- Use the Admin UI for day-two operations rather than manual table edits.
- Keep retention policy names controlled; update days/status through the Admin UI dropdown.
- Run purge initially with file deletion disabled, verify `ssv.PurgeRun` and `ssv.PurgeRunItem`, then enable file purge after approval.
- Use `GET /api/v1/admin/service-status`, `/health`, and `/api/v1/readiness` in monitoring.
- Keep customer documents, Base64 payloads, debug artifacts, and API secrets out of logs and source control.
- Treat installer `secrets\api-key.txt`, `secrets\database-connection.txt`, debug image folders, DB-native blob tables, result JSON, and review exports as sensitive data.
