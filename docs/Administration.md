# Administration Guide

## Local Folder Layout

Default local folders:

```text
C:\Temp\SignatureVerification\Input
C:\Temp\SignatureVerification\Output
C:\Temp\SignatureVerification\ReferenceSignatures
C:\Temp\SignatureVerification\BusinessReports
```

Create folders:

```powershell
New-Item -ItemType Directory -Force `
  C:\Temp\SignatureVerification\Input, `
  C:\Temp\SignatureVerification\Output, `
  C:\Temp\SignatureVerification\ReferenceSignatures, `
  C:\Temp\SignatureVerification\BusinessReports
```

Put customer documents in `Input`, references in `ReferenceSignatures`, and generated results in `Output`.

Do not place real customer data inside:

```text
C:\Temp\SignatureVerification\SyntheticBenchmark
```

That folder is only for generated synthetic test data.

## Where To Change Settings

Use this section as the quick map for common administration changes.

| Setting | Local folder test location | TotalAgility/API location | Code/default location |
| --- | --- | --- | --- |
| Match thresholds | `C:\Temp\SignatureVerification\Input\options.json` under `thresholds` | `optionsJson.thresholds`, or wrapper fields `matchedThreshold`, `probableMatchThreshold`, `reviewRequiredThreshold` | `StaticSignatureVerification.Core\SignatureVerificationCore.cs`, class `ScoreThresholds` |
| Expected roles | `Input\options.json` under `signatureRoles.mode` | `optionsJson.signatureRoles.mode`, or wrapper field `signatureRoleMode` | `SignatureRoleOptions` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Known signature zones | `Input\options.json` under `knownZones[]` | `optionsJson.knownZones[]` | `KnownSignatureZone` and `SignatureDetector` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Detection behavior | `Input\options.json` under `detection` | `optionsJson.detection` | `DetectionOptions` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| PDF rendering | `Input\options.json` under `pdfRendering` | `optionsJson.pdfRendering` | `PdfRenderingOptions` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Ghostscript path | `Input\ghostscript-path.txt`, or `Input\options.json` under `pdfRendering.ghostscriptExecutablePath` | `optionsJson.pdfRendering.ghostscriptExecutablePath` | `StaticSignatureVerification.PdfRendering\GhostscriptCommandLinePdfRenderer.cs` |
| Reference images | `C:\Temp\SignatureVerification\ReferenceSignatures`, or `ReferenceSignatures\reference-signatures.json` | `referenceSignaturesJson.referenceSets[].referenceImages[]` | `ReferenceSignatureInput` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Party/reference mapping | `ReferenceSignatures\reference-signatures.json` on `referenceSets[]` | `signatureMappings`, `signatureMappingsJson`, or `referenceSignaturesJson.referenceSets[]` | `SignatureMapping` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| OCR/MSDI layout | `Input` file matching `*ocr*.json`, `*msdi*.json`, or `*layout*.json` | `ocrLayoutJson` | `OcrLayoutParser` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Debug image output | local runner creates per-document `Output\<document>\debug`; can also set `saveDebugImages` and `debugOutputFolder` in `options.json` | `optionsJson.saveDebugImages` and `optionsJson.debugOutputFolder` | `DebugImageWriter` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs`. Files are always written under a per-request subfolder named for the verification's `documentResultId`, so concurrent requests reusing the same `debugOutputFolder` (a common caller pattern) never overwrite each other's images. |
| Business report source/output | Reporting command `--input` and `--output` | Not used by wrapper; reports consume result JSON files | `StaticSignatureVerification.Reporting\Program.cs` |
| Feedback tuning | Reporting command `--feedback-tune`, `--reviewer-outcomes`, `--current-options` | Exported reviewer CSV from review UI | `FeedbackTuningReport` in `StaticSignatureVerification.Reporting\Program.cs` |
| Synthetic benchmark size | Benchmark command `--documents`, `--signers`, `--references`, `--seed` | Not applicable | `StaticSignatureVerification.Benchmark\Program.cs` |
| Database storage mode | setup wizard `--storageMode Hybrid` or `--storageMode Database`; current value in `ssv.SystemSetting` | Use `/api/v1/verify` for hybrid requests and `/api/v1/verify-db-native` for full DB-native requests | `database\004_database_native_storage.sql`, `StaticSignatureVerification.Storage\DatabaseNativeContracts.cs` |
| Database storage scripts | `scripts\Initialize-Database.ps1`, `scripts\Import-VerificationResultsToDatabase.ps1`, `scripts\Export-DatabaseReviewQueue.ps1` | API service uses `SIGNATURE_VERIFICATION_DB_CONNECTION` | `database\*.sql`, `StaticSignatureVerification.Storage` |
| Runtime DB connection | `Input\database-connection.txt`, `C:\Temp\SignatureVerification\database-connection.txt`, environment variable `SIGNATURE_VERIFICATION_DB_CONNECTION`, or CLI `--databaseConnectionString` | Wrapper hosts should inject the same connection string into the configured storage adapter | `StaticSignatureVerification.Storage\SqlVerificationResultStore.cs` |
| Runtime root folder | environment variable `SIGNATURE_VERIFICATION_ROOT`, or setup/CLI `--root` where supported | API readiness and local tools read from the shared runtime config service | `EnvironmentSignatureVerificationConfigService` in `StaticSignatureVerification.Core` |
| Runtime storage mode override | environment variable `SIGNATURE_STORAGE_MODE`, setup wizard, or SQL `ssv.SystemSetting` depending on host | API DB-native verification requires SQL `StorageMode = Database` | `EnvironmentSignatureVerificationConfigService`, `SqlDatabaseNativeStore` |

## Administrative UI

The API hosts an administrative UI at:

```text
/admin
```

The UI uses the same `X-API-Key` role model as the API. Administrator keys can change storage mode and retention policies. Auditor/reviewer/reference approver roles can view the areas their API roles allow.

Current UI areas:

- overview, readiness, database counts, latest documents
- settings, storage mode, file purge, and database blob purge
- reference registry
- case queue
- retention policies, selected from `ssv.RetentionPolicy` to avoid typo-created policies
- API key creation/revocation using hashed DB-managed keys in `ssv.ApiKeyRegistry`
- security roles and principal role assignments
- form templates and signature zones
- threshold profiles
- backup/export package requests
- service status
- audit events

API key authentication supports both configured process keys from `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys` and DB-managed keys in `ssv.ApiKeyRegistry`. DB-managed keys are stored as SHA-256 hashes; the raw generated key is shown only once at creation time. The Admin UI keeps the entered key in browser session storage, not durable local storage.

Retention policy names come from `ssv.RetentionPolicy`. The Admin UI presents them as a dropdown and treats target object type as read-only for updates. Create intentional new policy names through controlled database migration or the operations CLI, then use the UI to maintain days and active/inactive state.

### Most Common Local Changes

Runtime root override:

```powershell
$env:SIGNATURE_VERIFICATION_ROOT = "D:\SignatureVerification"
```

When set, local tools and API readiness use folders below that root instead of the default `C:\Temp\SignatureVerification`.

Storage mode during setup:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Hybrid
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Database
```

Use `Hybrid` when existing file folders remain the operational source of documents, references, debug images, and reports while SQL stores results and workflow records. Use `Database` when the application should store document bytes, reference image bytes, results, debug artifacts, reviewer decisions, cases, and audit records in SQL.

Check the selected mode:

```sql
SELECT SettingValue
FROM ssv.SystemSetting
WHERE SettingKey = 'StorageMode';
```

Thresholds:

```text
C:\Temp\SignatureVerification\Input\options.json
```

```json
{
  "thresholds": {
    "matched": 85,
    "probableMatch": 70,
    "reviewRequired": 55
  }
}
```

Ghostscript:

```text
C:\Temp\SignatureVerification\Input\ghostscript-path.txt
```

Example file content:

```text
C:\Program Files\gs\gs10.xx.x\bin\gswin64c.exe
```

Signature roles:

```json
{
  "signatureRoles": {
    "mode": "ApplicantWitness"
  }
}
```

Known zones:

```json
{
  "knownZones": [
    {
      "signatureId": "applicant_signature",
      "pageIndex": 0,
      "x": 100,
      "y": 1200,
      "width": 900,
      "height": 300,
      "coordinateSystem": "pixels"
    }
  ]
}
```

### Most Common TotalAgility Changes

Thresholds can be passed as wrapper fields:

```json
{
  "matchedThreshold": "85",
  "probableMatchThreshold": "70",
  "reviewRequiredThreshold": "55"
}
```

Or inside `optionsJson`:

```json
{
  "thresholds": {
    "matched": 85,
    "probableMatch": 70,
    "reviewRequired": 55
  }
}
```

Party/reference mapping should be passed with the structured request:

```json
{
  "signatureMappings": [
    {
      "signatureId": "applicant_signature",
      "partyId": "0032",
      "partyName": "Applicant Name",
      "referenceSetId": "SIGNER_0032_ENGLISH",
      "expectedSignerId": "SIGNER_0032_ENGLISH",
      "source": "TotalAgility"
    }
  ]
}
```

### When To Change Code Defaults

Prefer runtime configuration through `options.json` or the TotalAgility request. Change code defaults only when you want the shipped baseline behavior to change for every environment.

Primary default classes:

- `ScoreThresholds`: default match/review thresholds.
- `DetectionOptions`: candidate detection behavior.
- `PdfRenderingOptions`: PDF rendering defaults.
- `SignatureRoleOptions`: role names, role modes, and default role labels.
- `ScoreWeights`: scoring weight distribution.
- `VerificationProfiles.Scoring`: structural reject limits, rotation/skew tolerant scoring, copied-signature comparison sensitivity, and other algorithm profile constants.

Do not lower thresholds simply to force more automatic matches. Wrong-signer and wrong-reference behavior is controlled by structural reject gates in the scorer. Threshold changes should be validated with the regression pack and with customer-labelled holdout documents.

## Regression Administration

Run the named core regression pack after changing thresholds, scoring profile defaults, detection behavior, preprocessing, TotalAgility mapping, PDF rendering, or reference handling:

```powershell
dotnet run --project .\StaticSignatureVerification.Regression\StaticSignatureVerification.Regression.csproj -c Release -- --output=.verification\regression-pack
```

Expected current result:

```text
20 passed / 20 total
```

Review:

```text
.verification\regression-pack\regression-report.md
.verification\regression-pack\regression-results.csv
```

The regression pack uses synthetic assets and proves core behavior is stable. It does not replace customer sampling for threshold approval.

## Reference Folder Rules

In simple local folder mode, reference image filenames must match the input document filename.

Single signature:

```text
Input\Customer123.pdf
ReferenceSignatures\Customer123.png
```

Multiple roles:

```text
Input\Customer123.pdf
ReferenceSignatures\applicant_signature\Customer123.png
ReferenceSignatures\witness_signature\Customer123.png
ReferenceSignatures\authorised_signature\Customer123.png
```

The app does not compare `Customer123.pdf` with unrelated files such as `OtherCustomer.png`.

For advanced mapping, use:

```text
ReferenceSignatures\reference-signatures.json
```

## Multiple References Per Role

Use `referenceImages[]` to supply multiple samples for the same role/party.

```json
{
  "referenceSets": [
    {
      "signatureId": "applicant_signature",
      "partyId": "0032",
      "referenceSetId": "SIGNER_0032",
      "referenceImages": [
        { "referenceId": "applicant_0032_ref_1", "imageBase64": "..." },
        { "referenceId": "applicant_0032_ref_2", "imageBase64": "..." },
        { "referenceId": "applicant_0032_ref_3", "imageBase64": "..." }
      ]
    }
  ]
}
```

The engine selects the best-scoring reference image within that role/reference set and records all comparisons in the audit.

## Business Mapping Configuration

Mapping tells the engine and reports which business party/reference set a signature role belongs to.

Recommended fields:

- `signatureId`
- `partyId`
- `partyName`
- `referenceSetId`
- `expectedSignerId`
- `source`

Mapping may be embedded in `referenceSets[]`:

```json
{
  "signatureId": "applicant_signature",
  "displayName": "Applicant Signature",
  "partyId": "0032",
  "partyName": "Applicant Name",
  "referenceSetId": "SIGNER_0032_ENGLISH",
  "expectedSignerId": "SIGNER_0032_ENGLISH"
}
```

Or passed separately to the TotalAgility structured request:

```json
{
  "signatureMappings": [
    {
      "signatureId": "applicant_signature",
      "partyId": "0032",
      "referenceSetId": "SIGNER_0032_ENGLISH",
      "source": "TotalAgility"
    }
  ]
}
```

## Options File

In local folder mode, place this file in `Input`:

```text
C:\Temp\SignatureVerification\Input\options.json
```

Common settings:

```json
{
  "inputDocumentType": "Auto",
  "dpi": 300,
  "thresholds": {
    "matched": 85,
    "probableMatch": 70,
    "reviewRequired": 55
  },
  "signatureRoles": {
    "mode": "Auto"
  },
  "detection": {
    "useKnownZones": true,
    "useOcrAnchors": true,
    "useBoxDetection": true,
    "useInkRegionDetection": true,
    "preferLaterPages": true
  }
}
```

## Signature Role Modes

Supported modes:

- `Auto`
- `ApplicantOnly`
- `ApplicantWitness`
- `ApplicantWitnessAuthorised`

Use `Auto` when reference sets are discovered from the local reference folder. Use a fixed mode when the form type is known and all expected roles should be enforced.

## Known Zones

Known zones are the strongest way to detect signatures on fixed forms.

```json
{
  "knownZones": [
    {
      "signatureId": "applicant_signature",
      "pageIndex": 0,
      "x": 100,
      "y": 1200,
      "width": 900,
      "height": 300,
      "coordinateSystem": "pixels"
    }
  ]
}
```

Supported coordinate systems include pixels, normalized/relative/percent, inches, and points.

## Threshold Administration

Default thresholds:

```json
{
  "thresholds": {
    "matched": 85,
    "probableMatch": 70,
    "reviewRequired": 55
  }
}
```

Guidance:

- Raise `matched` to reduce automatic accepts.
- Lower `matched` to increase automatic accepts only after false accept validation.
- Raise `reviewRequired` to make low-confidence cases become `NotMatched`.
- Lower `reviewRequired` to send more low-confidence cases to human review.
- Use `feedback-tuned-options.json` as a recommendation only; validate before production use.

## PDF And Ghostscript

Install Ghostscript separately. The solution does not bundle it.

Configure path with one of:

- `Input\ghostscript-path.txt`
- `options.json` under `pdfRendering.ghostscriptExecutablePath`
- command line `--ghostscriptPath`
- local auto-detection under `C:\Program Files\gs`

Example:

```json
{
  "pdfRendering": {
    "renderer": "GhostscriptCommandLine",
    "ghostscriptExecutablePath": "C:\\Program Files\\gs\\gs10.xx.x\\bin\\gswin64c.exe",
    "maxPages": 100,
    "timeoutSeconds": 60
  }
}
```

## Output Folders

Each input document receives a separate output folder to avoid overwrites:

```text
Output\Customer123\result.json
Output\Customer123\debug
```

Debug images are written only when enabled.

## Database Administration

SQL storage is available at runtime for the console/local folder runner and as a backfill path for older result JSON files. The application can still produce result JSON files, but when a database connection string is supplied it also writes the same result, signature cases, reference comparisons, and debug artifact paths directly into SQL.

For the full SQL reference, including database name, connection strings, tables, views, migrations, and verification queries, see [SQL Database Reference](SQL-Database.md).

Default database:

```text
SignatureVerification
```

Default local development connection string:

```text
Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;
```

Main administration files:

```text
database\001_initial_schema.sql
database\002_production_workflow_schema.sql
database\003_workflow_schema_fixups.sql
database\004_database_native_storage.sql
database\005_admin_retention_security.sql
database\006_seed_verifier_role.sql
scripts\Initialize-Database.ps1
scripts\Import-VerificationResultsToDatabase.ps1
scripts\Export-DatabaseReviewQueue.ps1
StaticSignatureVerification.Storage\StorageContracts.cs
StaticSignatureVerification.Storage\SqlVerificationResultStore.cs
StaticSignatureVerification.Storage\SqlProductionWorkflowStore.cs
```

Configure runtime database writes with one of:

```text
C:\Temp\SignatureVerification\Input\database-connection.txt
C:\Temp\SignatureVerification\database-connection.txt
SIGNATURE_VERIFICATION_DB_CONNECTION
--databaseConnectionString
```

Use `Encrypt=False;TrustServerCertificate=True` for the current LocalDB instance. Use production-approved encryption settings for shared SQL Server environments.

LocalDB is a development convenience and is not a production database host. Production deployments should use a managed/shared SQL Server instance, a fixed service identity, tested connectivity from the API host, and approved SQL encryption/backup settings.

Migration status is tracked in:

```text
ssv.SchemaMigration
```

Production workflow tables are now available for:

- reference enrollment, approval, retirement, replacement, and duplicate alerts
- versioned form templates and signature zones
- reviewer case assignment, SLA, escalation, and case events
- security roles and role assignments
- structured audit events
- hashed API key registry
- encrypted storage metadata
- default retention policies, purge settings, and purge runs
- disaster recovery/export package tracking

## Production Workflow Administration

Use `StaticSignatureVerification.Operations` for business administration tasks that should not be performed by manually editing files or database rows.

Reference lifecycle:

- `enroll-reference`: stores the image, scores quality, records party/reference metadata, and creates approval audit.
- `approve-reference`: marks the latest reference image approved and eligible for matching.
- `reject-reference`: rejects an enrolled reference image.
- `retire-reference`: retires an approved or obsolete reference.
- `scan-duplicates`: compares enrolled references and opens similarity alerts.

Case management:

- `create-case`
- `assign-case`
- `complete-case`
- `escalate-case`
- `cancel-case`
- `case-dashboard`

Retention and export:

- `set-retention`
- `purge` (add `--dryRun true` to preview a purge with zero mutation before running it for real)
- `export-dr`

The Admin UI Retention tab has a matching "Run purge now" control with a dry-run/real mode selector, backed by `POST /api/v1/admin/retention/purge?dryRun=true|false`.

Default production retention policies installed by migrations `005_admin_retention_security` and `007_document_metadata_retention`:

| Policy | Target object type | Days | Active by default? |
| --- | --- | --- | --- |
| `DefaultStorageObjectRetention` | `StorageObject` | 2555 | Yes |
| `DefaultDocumentBlobRetention` | `DocumentBlob` | 2555 | Yes |
| `DefaultDebugArtifactBlobRetention` | `DebugArtifactBlob` | 90 | Yes |
| `DefaultReportBlobRetention` | `ReportBlob` | 2555 | Yes |
| `DefaultVerificationDocumentRetention` | `VerificationDocument` | 2555 | **No** — review with Legal/Compliance and activate explicitly before this deletes case/document audit history |

The legacy/local `DebugArtifacts90Days` policy may also exist when created through the operations CLI.

`ssv.StorageObject` is populated automatically for debug images written by `POST /api/v1/verify` and for Hybrid-mode reference image enrollment; it is not retroactively populated for files created by earlier versions of the application.

Security:

- API authentication is API-key based. Bootstrap keys come from `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`; DB-managed keys are stored hashed in `ssv.ApiKeyRegistry`.
- API keys map to roles: `Administrator`, `ReferenceApprover`, `Reviewer`, `Auditor`, `Verifier`.
- An invalid or missing key returns `401 UNAUTHORIZED`. If the database-backed key store itself is unreachable, the API returns `503 AUTH_SERVICE_UNAVAILABLE` instead — this is deliberately distinct from a real authorization denial, and the underlying failure is logged server-side.
- API logs are structured JSON and must remain payload-safe.
- Reference image storage uses Windows DPAPI when `--encrypt true`.
- Installer-generated API keys and database connection strings are written under the install `secrets` folder and ACL-restricted to Administrators, SYSTEM, and the installing user.
- Treat debug images, DB-native document/reference/debug blobs, review exports, and generated result JSON as sensitive operational data.

## Request & Image Size Limits

Defense-in-depth limits, enforced at three layers so oversized/malicious payloads are rejected with a clear error rather than causing uncontrolled memory/CPU use:

| Layer | Limit | Where enforced |
| --- | --- | --- |
| HTTP request body | 100 MB | Kestrel (`MaxRequestBodySize`), `StaticSignatureVerification.Api/Program.cs` |
| Decoded document | 50 MB | `SignatureVerificationCore.Verify` — returns `DOCUMENT_TOO_LARGE`; covers both `/api/v1/verify` and `/api/v1/verify-db-native` |
| Decoded reference image | 15 MB | Inline references in `PreprocessReferences` (skipped with a warning) and `ReferenceQualityAnalyzer` (used by `/api/v1/references/enroll` and the Operations `enroll-reference` command) |
| Decoded image pixel count | 100 megapixels | `DocumentInputDetector.DecodeImageBytes` — returns `IMAGE_DIMENSIONS_TOO_LARGE`; guards against a small-byte-count image that decompresses to an enormous pixel grid, which a byte-size cap alone would not catch |

## Cleaning Synthetic Data

To remove generated synthetic data:

```powershell
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\SyntheticBenchmark
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\BusinessReports
```

Then run customer tests from:

```text
C:\Temp\SignatureVerification\Input
C:\Temp\SignatureVerification\ReferenceSignatures
```

## Security And Data Handling

- Do not commit customer documents, references, output JSON, debug images, or reviewer exports.
- Do not log full Base64 document or reference payloads.
- Restrict access to `Input`, `Output`, `ReferenceSignatures`, and `BusinessReports`.
- Treat reviewer outcome CSVs as operational records.
- Review Ghostscript licensing before distributing or bundling it.
