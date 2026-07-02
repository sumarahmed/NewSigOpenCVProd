# Production Readiness Review

Last reviewed: 2026-06-25.
Critical-findings remediation pass: 2026-07-02 (see "Retention/Purge Correctness Fixes (2026-07-02)" below).

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
| Retention | Implemented | Default policies installed, Admin UI has a "Run purge now" control (dry run or real), and purge covers `ssv.StorageObject`-registered files (debug images, Hybrid-mode reference images), DB-native `DocumentBlob`, `DebugArtifactBlob`, and `ReportBlob`, and — once an administrator activates the policy — `VerificationDocument` and its cascaded case/audit metadata. Dry run now performs zero mutations (previously it still deleted database blobs). |
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
8. `DefaultVerificationDocumentRetention` (migration `007`) is installed **inactive**. Case/document audit metadata (`VerificationDocument` and everything that cascades from it) is retained indefinitely until an administrator reviews the required retention window with Legal/Compliance and activates the policy through the Admin UI or `POST /api/v1/admin/retention`.
9. `ssv.StorageObject` now only tracks files written after this fix was deployed (debug images from `/api/v1/verify`, and Hybrid-mode reference images). Files written by earlier versions of the application are not retroactively registered and will not be found by purge; a one-time backfill script would be needed to bring pre-existing installations under retention coverage.

## Retention/Purge Correctness Fixes (2026-07-02)

A source-level review found three release-blocking defects in the retention/purge subsystem, all now fixed and covered by the existing 20-case regression pack (still `20 passed / 20 total`) plus the unit test suite:

- **Dry run previously still deleted data.** `deleteFiles=false` only skipped the filesystem delete; the SQL `DELETE` against `DocumentBlob`, `DebugArtifactBlob`, and `ReportBlob` ran unconditionally either way. `RunRetentionPurgeAsync` now takes an explicit `dryRun` parameter that, when set, performs **no** mutation of any kind — filesystem or database — and only records what would have been purged (`PurgeRunItem.Status = 'WouldPurge'`, `PurgeRun.Status = 'CompletedDryRun'`). Exposed via `POST /api/v1/retention/purge` (`dryRun` in the request body), `POST /api/v1/admin/retention/purge?dryRun=true`, the Operations CLI (`purge --dryRun true`), and a "Run purge now" control in the Admin UI Retention tab with an explicit dry-run/real mode selector.
- **Filesystem purge was dead code.** `ssv.StorageObject` — the table purge uses to find on-disk files — was never written to by any code path, so the filesystem-purge leg always found zero candidates. The API and Operations CLI now call `RegisterStorageObjectAsync` at the two points where the application writes real files to disk: debug images from `/api/v1/verify` (skipped for `/api/v1/verify-db-native`, whose debug files are already deleted synchronously after being copied into `DebugArtifactBlob`), and Hybrid-mode reference image enrollment (both the API and Operations CLI `enroll-reference` command).
- **Case/document audit metadata had no retention path at all.** `VerificationDocument`, and everything that cascades from it on delete (`SignatureCase`, `ReferenceComparison`, `DebugArtifact`, `ReviewerOutcome`, `ReviewCase`, `ResultGovernanceSnapshot`), was retained forever regardless of every other purge setting. Migration `007_document_metadata_retention.sql` registers a `VerificationDocument` retention policy (installed **inactive** — see gap #8 above) and `RunRetentionPurgeAsync` now purges eligible documents by `CreatedUtc` when that policy is active.
- As a secondary correctness fix in the same code path, `PurgeRunItem.StorageObjectId` is now populated for `StorageObject`-sourced candidates (it was previously always `NULL`, which broke forensic traceability of exactly which storage object a purge run removed).

## Database Migrations

Current migrations:

- `001_initial_schema.sql`
- `002_production_workflow_schema.sql`
- `003_workflow_schema_fixups.sql`
- `004_database_native_storage.sql`
- `005_admin_retention_security.sql`
- `006_seed_verifier_role.sql`
- `007_document_metadata_retention.sql`

Migration `005` adds DB-managed API keys, default retention policies, and purge/export settings. Migration `006` ensures the `Verifier` role is present for existing databases. Migration `007` registers an inactive `VerificationDocument` retention policy (see "Retention/Purge Correctness Fixes" below).

## Operational Recommendations

- Use the installer for controlled server deployment.
- Run `scripts\Initialize-Database.ps1` during deployment and verify `ssv.SchemaMigration`.
- Use the Admin UI for day-two operations rather than manual table edits.
- Keep retention policy names controlled; update days/status through the Admin UI dropdown.
- Run purge with the Admin UI's "Dry run" mode first (or `dryRun: true` on the API/CLI) — this now makes zero changes to files or database rows — verify `ssv.PurgeRun` and `ssv.PurgeRunItem`, then run again in real mode after approval.
- Before relying on retention for compliance purposes, review and activate the `DefaultVerificationDocumentRetention` policy with Legal/Compliance; it ships inactive.
- Use `GET /api/v1/admin/service-status`, `/health`, and `/api/v1/readiness` in monitoring.
- Keep customer documents, Base64 payloads, debug artifacts, and API secrets out of logs and source control.
- Treat installer `secrets\api-key.txt`, `secrets\database-connection.txt`, debug image folders, DB-native blob tables, result JSON, and review exports as sensitive data.
