# Operations Runbook

## Build

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path .).Path
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = Join-Path (Resolve-Path .).Path ".nuget-packages"
dotnet build .\StaticSignatureVerification.sln -c Release --no-restore --verbosity minimal -m:1
```

If packages have not been restored:

```powershell
dotnet restore .\StaticSignatureVerification.sln
```

## One-Click Server Install

Use the installer script on a target Windows server:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-SignatureVerification.ps1 `
  -InstallRoot "C:\ProgramData\Tungsten\StaticSignatureVerification" `
  -RuntimeRoot "C:\Temp\SignatureVerification" `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -DatabaseName "SignatureVerification" `
  -StorageMode Database `
  -CreateStartupTask
```

The installer:

- checks .NET, ASP.NET Core, SQL LocalDB when LocalDB is used, and Ghostscript presence
- can install missing .NET/SQL dependencies through `winget` when `-InstallMissingDependencies` is supplied
- can install Ghostscript through `winget` only when `-InstallGhostscript` is supplied
- publishes the API and operations tool
- applies all `database\*.sql` migrations
- creates runtime folders and `database-connection.txt`
- writes production API configuration
- creates a generated API key in `secrets\api-key.txt` when `-ApiKey` is not supplied
- writes the database connection string to `secrets\database-connection.txt`
- optionally registers the `StaticSignatureVerificationApi` Windows startup task

The installer ACL-restricts generated secret files to Administrators, SYSTEM, and the installing user. Generated launcher scripts read secrets at runtime instead of embedding raw API keys or connection strings. Ghostscript is detected but not silently bundled because its licensing must be reviewed before redistribution.

After install, start manually if a startup task was not registered:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\ProgramData\Tungsten\StaticSignatureVerification\scripts\Start-SignatureVerificationApi.ps1"
```

Open:

```text
http://127.0.0.1:5117/admin
```

## Test

```powershell
dotnet .\StaticSignatureVerification.Tests\bin\Release\net8.0\StaticSignatureVerification.Tests.dll
```

Or:

```powershell
dotnet run --project .\StaticSignatureVerification.Tests -c Release
```

## Run Core Regression Pack

Run the named 20-case core regression pack after code, threshold, preprocessing, scoring, PDF, mapping, or wrapper changes:

```powershell
dotnet run --project .\StaticSignatureVerification.Regression\StaticSignatureVerification.Regression.csproj -c Release -- --output=.verification\regression-pack
```

Expected current result:

```text
20 passed / 20 total
```

Outputs:

- `.verification\regression-pack\regression-report.md`
- `.verification\regression-pack\regression-results.csv`
- `.verification\regression-pack\regression-results.json`
- `.verification\regression-pack\assets`

The pack includes:

- `TC001_GenuineSignature_Match`
- `TC002_DifferentSigner_NotMatch`
- `TC003_EmptySignatureBox_NoSignatureFound`
- `TC004_MultipleSignatures_BothMatched`
- `TC005_MultipleSignatures_OneMatchedOneRejected`
- `TC006_RotatedPage_DetectedAndMatched`
- `TC007_SkewedPage_DetectedAndMatched`
- `TC008_StampNearSignature_NotDetectedAsSignature`
- `TC009_HandwritingNearSignature_NotFalsePositive`
- `TC010_LowResolution_Review`
- `TC011_CroppedSignature_ReviewOrReject`
- `TC012_InvalidBase64_ReturnsErrorJson`
- `TC013_CorruptPdf_ReturnsErrorJson`
- `TC014_LargePdf_WithinLimits`
- `TC015_NoReference_ReturnsErrorJson`
- `TC016_TwoReferencesCorrectMapping`
- `TC017_WrongReference_NotMatched`
- `TC018_ReusedCopiedSignature_HighRiskFlag`
- `TC019_BoxBorderRemovedCorrectly`
- `TC020_TotalAgilityMethod_ReturnsValidJsonString`

## Run Customer Local Folder Test

Prepare:

```text
C:\Temp\SignatureVerification\Input
C:\Temp\SignatureVerification\ReferenceSignatures
C:\Temp\SignatureVerification\Output
```

To use a different local root without changing code:

```powershell
$env:SIGNATURE_VERIFICATION_ROOT = "D:\SignatureVerification"
```

The console harness, operations tool, and API readiness endpoint read this through the shared runtime config service.

Run:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest -c Release
```

Run local folder mode and write each processed result directly to SQL:

```powershell
$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
dotnet run --project .\StaticSignatureVerification.ConsoleTest -c Release
```

Or pass the connection string explicitly for a single document:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest -c Release -- `
  --documentPdfFile C:\Temp\SignatureVerification\Input\Customer123.pdf `
  --referencesFolder C:\Temp\SignatureVerification\ReferenceSignatures `
  --outputJsonFile C:\Temp\SignatureVerification\Output\Customer123\result.json `
  --databaseConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
```

Generate reports:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -c Release -- `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports `
  --title "Signature Verification Business Report"
```

Open:

```text
C:\Temp\SignatureVerification\BusinessReports\review-queue.html
```

## Run Synthetic Benchmark

Generate and run:

```powershell
dotnet run --project .\StaticSignatureVerification.Benchmark -c Release -- `
  generate-run `
  --root C:\Temp\SignatureVerification\SyntheticBenchmark `
  --documents 500 `
  --signers 50 `
  --references 3 `
  --clean
```

Run existing benchmark after code changes:

```powershell
dotnet run --project .\StaticSignatureVerification.Benchmark -c Release -- `
  run `
  --root C:\Temp\SignatureVerification\SyntheticBenchmark
```

Generate business report from benchmark results:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -c Release -- `
  --input C:\Temp\SignatureVerification\SyntheticBenchmark\Results `
  --output C:\Temp\SignatureVerification\BusinessReports `
  --title "Signature Verification Business Report"
```

## Reporting Health Check

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -c Release -- `
  --health-check `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports
```

Expected output:

- `reporting-health-check.html`
- console status `Pass`

## Database Migration Operations

Default local development connection string:

Full database details are documented in [SQL Database Reference](SQL-Database.md).

```text
Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;
```

Initialize the database:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Initialize-Database.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -DatabaseName "SignatureVerification"
```

The initializer applies all files under `database\*.sql` in order and records them in `ssv.SchemaMigration`. It is safe to rerun; already-applied migrations are skipped.

Import existing result JSON files when you need to backfill SQL from older file-based runs:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Import-VerificationResultsToDatabase.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -InputFolder "C:\Temp\SignatureVerification\Output"
```

Import synthetic benchmark results instead:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Import-VerificationResultsToDatabase.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -InputFolder "C:\Temp\SignatureVerification\SyntheticBenchmark\Results"
```

Generate the SQL-backed review queue:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Export-DatabaseReviewQueue.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -OutputFolder "C:\Temp\SignatureVerification\BusinessReports"
```

Outputs:

- `C:\Temp\SignatureVerification\BusinessReports\db-review-queue.html`
- `C:\Temp\SignatureVerification\BusinessReports\db-review-queue.csv`

Verify row counts:

```sql
SELECT MigrationId, AppliedUtc FROM ssv.SchemaMigration ORDER BY MigrationId;
SELECT COUNT(*) FROM ssv.VerificationDocument;
SELECT COUNT(*) FROM ssv.SignatureCase;
SELECT COUNT(*) FROM ssv.ReferenceComparison;
SELECT COUNT(*) FROM ssv.vwReviewQueue;
```

Verify production workflow objects:

```sql
SELECT COUNT(*) FROM ssv.ReferenceSetRegistry;
SELECT COUNT(*) FROM ssv.ReferenceImageRegistry;
SELECT COUNT(*) FROM ssv.FormTemplate;
SELECT COUNT(*) FROM ssv.ReviewCase;
SELECT COUNT(*) FROM ssv.AuditEvent;
SELECT COUNT(*) FROM ssv.RetentionPolicy;
SELECT COUNT(*) FROM ssv.ExportPackage;
```

## Reviewer Feedback Operations

1. Open `review-queue.html`.
2. Review side-by-side form/reference images.
3. Select reviewer outcome, reason, reviewer, and notes.
4. Use row-level **Save** while working.
5. Use **Export Reviewer Outcomes** to download CSV.
6. Run feedback tuning.

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -c Release -- `
  --feedback-tune `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports `
  --reviewer-outcomes C:\Temp\SignatureVerification\BusinessReports\reviewer-outcomes.csv `
  --current-options C:\Temp\SignatureVerification\Input\options.json
```

Review outputs:

- `feedback-tuning-report.html`
- `feedback-tuned-options.json`
- `feedback-threshold-sweep.csv`
- `reference-maintenance-actions.csv`

Do not auto-promote these recommendations. Validate and approve first.

## Production Operations CLI

The production operations tool is:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- <command>
```

Common connection option:

```powershell
--connectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
```

Reference enrollment with quality gate and encrypted local storage:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- `
  enroll-reference `
  --imageFile C:\Temp\SignatureVerification\ReferenceSignatures\applicant_signature\Customer123.png `
  --referenceSetId CUSTOMER123_APPLICANT `
  --referenceId CUSTOMER123_APPLICANT_REF1 `
  --signatureId applicant_signature `
  --partyId CUSTOMER123 `
  --partyName "Customer 123" `
  --actor AdminUser `
  --encrypt true
```

Approve, reject, or retire a reference:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- approve-reference --referenceId CUSTOMER123_APPLICANT_REF1 --actor ApproverUser
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- reject-reference --referenceId CUSTOMER123_APPLICANT_REF1 --actor ApproverUser --reason PoorQuality
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- retire-reference --referenceId CUSTOMER123_APPLICANT_REF1 --actor ApproverUser --reason Replaced
```

Scan for duplicate or wrong-person references:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- scan-duplicates --threshold 92
```

Case management:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- create-case --caseNumber CASE-1001 --priority High --assignedTo Reviewer1
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- assign-case --caseNumber CASE-1001 --assignedTo Reviewer2 --actor LeadReviewer
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- complete-case --caseNumber CASE-1001 --actor Reviewer2
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- case-dashboard --output C:\Temp\SignatureVerification\BusinessReports\case-management-dashboard.html
```

Retention, purge, and disaster recovery export:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- set-retention --policyName DebugArtifacts90Days --targetObjectType DebugArtifact --retentionDays 90
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- purge --dryRun true
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- purge --deleteFiles false
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- export-dr --outputFolder C:\Temp\SignatureVerification\Exports
```

`--dryRun true` performs no filesystem or database mutation at all and only reports what would be purged — run this before the first real purge in a new environment. `--deleteFiles false` (the default) still purges eligible database rows and blobs for real; it only skips deleting the corresponding on-disk file.

Readiness and setup:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- check-prereqs
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard
```

The setup wizard asks for the storage mode when run interactively:

- `Hybrid`: file/folder artifacts plus SQL metadata and workflow records.
- `Database`: document bytes, reference image bytes, results, debug artifacts, reviewer outcomes, cases, and audit records are stored in SQL.

Set the mode explicitly for scripted installs:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Hybrid
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Database
```

The selected value is stored in SQL:

```sql
SELECT SettingValue
FROM ssv.SystemSetting
WHERE SettingKey = 'StorageMode';
```

## Production API Service

Run the API:

```powershell
$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
$env:SIGNATURE_API_KEYS = "<strong-random-key>:Administrator,ReferenceApprover,Reviewer,Auditor,Verifier"
dotnet run --project .\StaticSignatureVerification.Api -c Release -- --urls http://127.0.0.1:5117
```

Every secured endpoint requires:

```text
X-API-Key: <configured key>
X-Request-ID: <caller request id>
```

If no bootstrap key is supplied through `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`, the API starts with a warning and authorization depends on valid DB-managed keys in `ssv.ApiKeyRegistry`. For a fresh install, provide a bootstrap key or use the installer-generated key so the first administrator can sign in and create/revoke DB-managed keys.

Core endpoints:

- `GET /health`
- `GET /api/v1/readiness`
- `POST /api/v1/verify`
- `POST /api/v1/verify-db-native`
- `POST /api/v1/references/enroll`
- `POST /api/v1/references/{referenceId}/approve`
- `POST /api/v1/references/{referenceId}/reject`
- `POST /api/v1/references/{referenceId}/retire`
- `POST /api/v1/references/scan-duplicates`
- `POST /api/v1/review-cases`
- `POST /api/v1/review-cases/{caseNumber}/{action}`
- `POST /api/v1/reviewer-outcomes`
- `POST /api/v1/retention/purge`
- `GET /dashboard/cases`
- `GET /admin`
- `GET /api/v1/admin/summary`
- `GET /api/v1/admin/settings`
- `PUT /api/v1/admin/settings/storage-mode`
- `PUT /api/v1/admin/settings/purge`
- `POST /api/v1/admin/retention/purge`
- `GET /api/v1/admin/api-keys`
- `POST /api/v1/admin/api-keys`
- `POST /api/v1/admin/api-keys/{apiKeyId}/revoke`
- `GET /api/v1/admin/roles`
- `GET /api/v1/admin/users`
- `POST /api/v1/admin/users/roles`
- `POST /api/v1/admin/users/roles/{securityPrincipalRoleId}/revoke`
- `GET /api/v1/admin/templates`
- `POST /api/v1/admin/templates`
- `GET /api/v1/admin/templates/{formTemplateId}/zones`
- `POST /api/v1/admin/templates/{formTemplateId}/zones`
- `GET /api/v1/admin/threshold-profiles`
- `POST /api/v1/admin/threshold-profiles`
- `GET /api/v1/admin/export-packages`
- `POST /api/v1/admin/export-packages`
- `GET /api/v1/admin/service-status`
- `GET /api/v1/admin/references`
- `GET /api/v1/admin/cases`
- `GET /api/v1/admin/retention`
- `POST /api/v1/admin/retention`
- `GET /api/v1/admin/audit`

The API writes structured JSON logs to stdout and stores operational audit events in SQL. Do not configure logs to capture document Base64 or reference image Base64.

Use `POST /api/v1/verify` for hybrid integrations where the caller provides `referenceSignaturesJson`. Use `POST /api/v1/verify-db-native` when `StorageMode = Database`; that endpoint accepts document bytes and mapping IDs, then loads approved reference images from SQL.

Admin API key notes:

- Bootstrap keys are configured with `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`.
- DB-managed keys are created through the Admin UI/API and stored in `ssv.ApiKeyRegistry` as hashes.
- The generated `ssv_...` value is the secret. The key name is only a label.
- The raw secret is shown once; store it in the approved secret vault.
- The Admin UI stores the entered key only in browser session storage.

Admin retention notes:

- Retention policy names come from `ssv.RetentionPolicy`.
- The Admin UI uses a dropdown for policy names and read-only target object type to avoid typo-created policies.
- File purge and DB-native blob purge are controlled separately in Admin Settings.
- `/api/v1/admin/retention/purge` follows those settings and takes `dryRun` as a query string parameter (`?dryRun=true`); `/api/v1/retention/purge` follows the request body (`{"deleteFiles": false, "dryRun": true}`).
- `dryRun: true` on either endpoint performs no filesystem or database mutation — use it to preview a purge before running it for real. The Admin UI Retention tab exposes this as a mode selector next to its "Run purge now" button.

## Operational Monitoring

Track from result JSON and reports:

- document count
- signature case count
- `overallDecision`
- signature decision counts
- `errors[].code`
- `operationalAudit.durationMs`
- `operationalAudit.status`
- `operationalAudit.pagesLoaded`
- `operationalAudit.referenceImageCount`
- `signatureCountExpected`
- `signatureCountDetected`
- review queue size
- references with high not-matched/review counts

## Common Errors

### `GHOSTSCRIPT_PATH_MISSING`

Cause: PDF input requires Ghostscript path.

Fix:

- install Ghostscript
- set `Input\ghostscript-path.txt`
- or set `pdfRendering.ghostscriptExecutablePath`
- or pass `--ghostscriptPath`

### `INVALID_BASE64_DOCUMENT`

Cause: wrapper received invalid document Base64.

Fix:

- verify document encoding
- remove data URI prefixes
- confirm payload is not truncated

### `REFERENCE_SIGNATURE_MISSING`

Cause: no usable reference images were supplied.

Fix:

- verify `ReferenceSignatures` folder
- verify filename matches input document in local folder mode
- verify `reference-signatures.json`
- verify `imageBase64` values decode to images

### `NoSignatureDetected`

Cause: expected signature role was configured, but no usable ink was found.

Fix:

- confirm form has a signature
- inspect debug images
- check known zone coordinates
- check PDF page rendering and `maxPages`
- check OCR anchor labels

### `REUSED_COPIED_SIGNATURE_HIGH_RISK`

Cause: two different expected roles/signers contain a near-identical detected signature pattern.

Fix:

- review the document manually
- confirm the same signature image was not copied into multiple boxes
- verify role-to-party mapping is correct
- keep the case in review or reject according to business policy

### Unexpected `NotMatched` For A Different Signer

This is expected when structural reject gates identify clear wrong-signer or wrong-reference evidence. Do not lower thresholds to override this without customer-labelled validation.

### Unexpected English/Chinese Reference Display

The engine compares the form signature against the expected mapped reference set, not against every reference. Check the review queue Mapping column:

- `Expected`
- `Actual` for synthetic benchmark only
- `Ref set`
- `ground truth`

If production mapping is wrong, fix the `signatureMappings`/reference set data from TotalAgility or the reference system.

### Runtime Folder Access Denied

Cause: the service identity or operator account cannot write to the configured runtime root. The prerequisite checker tests write access by creating temporary `.write-test-*` files.

Fix:

- confirm `SIGNATURE_VERIFICATION_ROOT` points to the intended runtime folder
- grant the API service identity modify rights to `Input`, `Output`, `ReferenceSignatures`, `ReferenceStore`, report/export folders, and transient debug folders
- rerun `check-prereqs`

### LocalDB Automatic Instance Failure

Cause: LocalDB is per-user and depends on user-profile registry state. It can fail when run from a different elevation context, service account, corrupted user instance registry, or locked profile.

Fix:

- for local development, recreate/repair the LocalDB instance and rerun database initialization
- run local API and database tools under the same user/elevation context
- for production, use a real SQL Server instance or approved managed SQL service instead of LocalDB

## Cleanup

Clean synthetic data:

```powershell
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\SyntheticBenchmark
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\BusinessReports
```

Clean customer outputs only:

```powershell
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\Output
Remove-Item -Recurse -Force C:\Temp\SignatureVerification\BusinessReports
```

Do not delete `Input` or `ReferenceSignatures` unless you intend to remove source test data.

## Release Checklist

- Build succeeds.
- Tests pass.
- TotalAgility structured request test passes.
- Local folder run succeeds on sample customer-like files.
- Report generation succeeds.
- Database initialization and import succeed when SQL storage is enabled.
- Database migration ledger shows all expected migrations applied.
- Operations CLI workflows pass: readiness, reference enrollment, approval, duplicate scan, case management, retention, purge, export.
- API `/health`, `/api/v1/readiness`, and `/api/v1/verify` pass with API-key authentication.
- Review queue opens and shows form/reference images.
- Mapping column shows party/reference context.
- Ghostscript path is configured for PDF environments.
- Thresholds are reviewed and approved.
- Reviewer feedback process is documented.
- Logs/audit retention approach is approved.
- SQL and filesystem replication are configured where required, and independent backups, restore drills, RPO/RTO, config/secret backup, and service recovery runbooks are approved.
- Customer data is excluded from source control.
