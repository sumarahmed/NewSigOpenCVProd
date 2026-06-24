# SQL Database Reference

## Database

Default database name:

```text
SignatureVerification
```

Default local development connection string:

```text
Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;
```

For the initializer, connect without `Initial Catalog` so it can create the database:

```text
Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;
```

The current LocalDB instance does not support the SSMS-generated `Encrypt=True;TrustServerCertificate=False` connection string. Use the LocalDB string above for local development. Use production-approved encryption settings for shared SQL Server environments.

## Initialize Or Upgrade

Run all migrations:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Initialize-Database.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -DatabaseName "SignatureVerification"
```

Migration files live in:

```text
database\001_initial_schema.sql
database\002_production_workflow_schema.sql
database\003_workflow_schema_fixups.sql
database\004_database_native_storage.sql
database\005_admin_retention_security.sql
database\006_seed_verifier_role.sql
```

Applied migrations are tracked in:

```text
ssv.SchemaMigration
```

The initializer is safe to rerun. Already-applied migrations are skipped.

## Storage Modes

The solution supports two operational storage modes. The selected mode is stored in `ssv.SystemSetting` as `StorageMode`.

| Mode | Intended use | What is stored in SQL | What remains on disk |
| --- | --- | --- | --- |
| `Hybrid` | Gradual migration and existing local-folder operations | Result records, reviewer outcomes, reference lifecycle metadata, audit, case management, retention/export metadata | Input files, reference image files, debug images, reports |
| `Database` | Fully DB-native production flow | Input document bytes, approved reference image bytes, result records, debug artifacts, reviewer outcomes, audit, cases, retention/export metadata | Temporary runtime files only when a renderer or report generator needs them |

Configure the setup wizard interactively:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard
```

Or set the mode explicitly:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Database
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Hybrid
```

Check the current mode:

```sql
SELECT SettingValue
FROM ssv.SystemSetting
WHERE SettingKey = 'StorageMode';
```

## Configure The App To Write To SQL

Local folder mode can write directly to SQL with:

```powershell
$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
dotnet run --project .\StaticSignatureVerification.ConsoleTest -c Release
```

Or use one of:

```text
C:\Temp\SignatureVerification\Input\database-connection.txt
C:\Temp\SignatureVerification\database-connection.txt
--databaseConnectionString
SIGNATURE_VERIFICATION_DB_CONNECTION
```

For full DB-native verification, use `POST /api/v1/verify-db-native`. That endpoint reads approved references from SQL and stores the incoming document bytes, result, and debug artifacts in SQL.

Do not send `referenceSignaturesJson` to the DB-native endpoint. Send document bytes and the business mappings that identify the role, party, and reference set.

The API uses:

```text
SIGNATURE_VERIFICATION_DB_CONNECTION
```

## Core Result Tables

| Object | Purpose |
| --- | --- |
| `ssv.VerificationDocument` | One row per processed document result. Stores document IDs, source/result paths, overall decision, confidence, counts, duration, errors, and full result JSON. |
| `ssv.SignatureCase` | One row per signature role/case. Stores decision, confidence, detected region, mapping, best reference, warnings, and full signature result JSON. |
| `ssv.ReferenceComparison` | All audited comparisons between detected signature and candidate references. |
| `ssv.DebugArtifact` | Paths to debug images generated for a signature case. |
| `ssv.ReviewerOutcome` | Human reviewer outcome, reason, reviewer, notes, and timestamp. |
| `ssv.ThresholdProfile` | Approved threshold profiles. |
| `ssv.vwReviewQueue` | SQL-backed review queue view. |

Signature warnings such as `NO_SIGNATURE_DETECTED`, `INSUFFICIENT_SIGNATURE_QUALITY`, and `REUSED_COPIED_SIGNATURE_HIGH_RISK` are preserved in the signature result JSON and warning fields for review/audit reporting.

## Production Workflow Tables

| Object | Purpose |
| --- | --- |
| `ssv.ReferenceSubject` | Party/customer identity for reference enrollment. |
| `ssv.ReferenceSetRegistry` | Reference set lifecycle: draft, pending approval, approved, rejected, retired. |
| `ssv.ReferenceImageRegistry` | Individual reference image versions, quality score, approval status, hash, storage URI, active dates. |
| `ssv.ReferenceApprovalEvent` | Audit trail for reference add/approve/reject/retire actions. |
| `ssv.ReferenceSimilarityAlert` | Duplicate or wrong-person reference alerts. |
| `ssv.FormTemplate` | Versioned form/document templates. |
| `ssv.FormTemplateZone` | Known signature zones per form template version. |
| `ssv.ResultGovernanceSnapshot` | Engine/template/threshold/reference versions used for a result. |
| `ssv.ReviewCase` | Review case assignment, priority, status, SLA, branch, customer, document type. |
| `ssv.ReviewCaseEvent` | Case event history. |
| `ssv.SecurityRole` | Role definitions. |
| `ssv.SecurityPrincipalRole` | Principal-to-role assignments. |
| `ssv.ApiKeyRegistry` | Hashed DB-managed API keys, roles, expiry, revocation, and usage tracking. |
| `ssv.AuditEvent` | Structured security and operational audit events. |
| `ssv.StorageObject` | Storage metadata for documents, references, debug images, reports, hashes, encryption flags, retention dates. |
| `ssv.RetentionPolicy` | Retention policy definitions. |
| `ssv.PurgeRun` | Purge execution summary. |
| `ssv.PurgeRunItem` | Purge execution details. |
| `ssv.ExportPackage` | Disaster recovery/export package tracking. |

## DB-Native Blob Tables

| Object | Purpose |
| --- | --- |
| `ssv.SystemSetting` | Runtime settings such as `StorageMode`. |
| `ssv.DocumentBlob` | Original document bytes for DB-native verification. Linked to `ssv.VerificationDocument` after processing. |
| `ssv.ReferenceImageBlob` | Approved reference image bytes for DB-native matching. Linked to reference registry rows by `ReferenceImageRegistryId`. |
| `ssv.DebugArtifactBlob` | Debug image bytes captured from processing runs. |
| `ssv.ReportBlob` | Reserved for generated report payloads that should be retained in SQL. |
| `ssv.vwApprovedReferenceImageNative` | Approved SQL-stored references eligible for DB-native matching. |

Default settings installed by migration `005_admin_retention_security`:

| SettingKey | Default |
| --- | --- |
| `FilePurgeEnabled` | `false` |
| `DatabaseBlobPurgeEnabled` | `true` |
| `BackupExportFolder` | empty |

Default retention policies installed by the same migration:

| PolicyName | TargetObjectType | RetentionDays |
| --- | --- | --- |
| `DefaultStorageObjectRetention` | `StorageObject` | 2555 |
| `DefaultDocumentBlobRetention` | `DocumentBlob` | 2555 |
| `DefaultDebugArtifactBlobRetention` | `DebugArtifactBlob` | 90 |
| `DefaultReportBlobRetention` | `ReportBlob` | 2555 |

## DB-Native Verification Endpoint

Endpoint:

```text
POST /api/v1/verify-db-native
```

Required headers:

```text
X-API-Key: <configured key>
X-Request-ID: <caller request id>
```

Request shape:

```json
{
  "documentName": "Customer123.pdf",
  "contentType": "application/pdf",
  "documentBase64": "...",
  "signatureMappings": [
    {
      "signatureId": "applicant_signature",
      "partyId": "0032",
      "partyName": "Applicant Name",
      "referenceSetId": "SIGNER_0032",
      "expectedSignerId": "SIGNER_0032"
    }
  ],
  "optionsJson": "{...}",
  "ocrLayoutJson": "{...}"
}
```

The endpoint:

1. Verifies `StorageMode = Database`.
2. Saves the incoming document to `ssv.DocumentBlob`.
3. Builds the reference payload from approved rows in `ssv.vwApprovedReferenceImageNative`.
4. Runs the deterministic verification engine.
5. Saves result records to `ssv.VerificationDocument`, `ssv.SignatureCase`, and `ssv.ReferenceComparison`.
6. Links the document blob to the result.
7. Saves generated debug artifacts to `ssv.DebugArtifactBlob`.
8. Writes an audit event.

Temporary debug files may be created while the engine runs, because the core debug writer emits image files. In DB-native API verification those files are copied into `ssv.DebugArtifactBlob`, the debug artifact paths are changed to `sql://` URIs, and the temporary debug folder is removed.

## Views

| View | Purpose |
| --- | --- |
| `ssv.vwReviewQueue` | Cases requiring review or error handling. |
| `ssv.vwApprovedReferenceImage` | Approved reference images eligible for matching. |
| `ssv.vwCaseManagementQueue` | Active case management queue. |

## Useful Verification Queries

Migration status:

```sql
SELECT MigrationId, FileName, AppliedUtc
FROM ssv.SchemaMigration
ORDER BY MigrationId;
```

Core result counts:

```sql
SELECT COUNT(*) AS Documents FROM ssv.VerificationDocument;
SELECT COUNT(*) AS SignatureCases FROM ssv.SignatureCase;
SELECT COUNT(*) AS ReferenceComparisons FROM ssv.ReferenceComparison;
SELECT COUNT(*) AS ReviewQueueRows FROM ssv.vwReviewQueue;
```

Production workflow counts:

```sql
SELECT COUNT(*) AS ReferenceSets FROM ssv.ReferenceSetRegistry;
SELECT COUNT(*) AS ReferenceImages FROM ssv.ReferenceImageRegistry;
SELECT COUNT(*) AS ReferenceEvents FROM ssv.ReferenceApprovalEvent;
SELECT COUNT(*) AS SimilarityAlerts FROM ssv.ReferenceSimilarityAlert;
SELECT COUNT(*) AS ReviewCases FROM ssv.ReviewCase;
SELECT COUNT(*) AS ReviewCaseEvents FROM ssv.ReviewCaseEvent;
SELECT COUNT(*) AS RetentionPolicies FROM ssv.RetentionPolicy;
SELECT COUNT(*) AS PurgeRuns FROM ssv.PurgeRun;
SELECT COUNT(*) AS ExportPackages FROM ssv.ExportPackage;
SELECT COUNT(*) AS AuditEvents FROM ssv.AuditEvent;
SELECT COUNT(*) AS ApiKeys FROM ssv.ApiKeyRegistry;
```

DB-native storage counts:

```sql
SELECT SettingValue AS StorageMode
FROM ssv.SystemSetting
WHERE SettingKey = 'StorageMode';

SELECT COUNT(*) AS DocumentBlobs FROM ssv.DocumentBlob;
SELECT COUNT(*) AS ReferenceImageBlobs FROM ssv.ReferenceImageBlob;
SELECT COUNT(*) AS DebugArtifactBlobs FROM ssv.DebugArtifactBlob;
SELECT COUNT(*) AS ApprovedNativeReferences FROM ssv.vwApprovedReferenceImageNative;
```

Latest processed documents:

```sql
SELECT TOP (20)
    EventUtc,
    DocumentName,
    DocumentResultId,
    OverallDecision,
    OverallConfidence,
    SignatureCountExpected,
    SignatureCountDetected,
    ResultPath,
    SourceDocumentPath
FROM ssv.VerificationDocument
ORDER BY UpdatedUtc DESC;
```

Review queue:

```sql
SELECT TOP (100)
    EventUtc,
    DocumentName,
    SignatureId,
    PartyId,
    ReferenceSetId,
    Decision,
    Confidence,
    BestReferenceFileName,
    SourceDocumentPath,
    ResultPath
FROM ssv.vwReviewQueue
ORDER BY EventUtc DESC;
```

Approved references:

```sql
SELECT
    ReferenceSetId,
    SignatureId,
    ExternalPartyId,
    PartyName,
    ReferenceId,
    QualityStatus,
    QualityScore,
    StorageUri
FROM ssv.vwApprovedReferenceImage
ORDER BY ReferenceSetId, ReferenceId;
```

Case dashboard data:

```sql
SELECT *
FROM ssv.vwCaseManagementQueue
ORDER BY Priority DESC, DueUtc, CreatedUtc DESC;
```

Audit events:

```sql
SELECT TOP (100)
    EventUtc,
    EventType,
    Severity,
    Actor,
    EntityType,
    EntityId,
    Message
FROM ssv.AuditEvent
ORDER BY EventUtc DESC;
```

## Backfill Existing JSON Results

Import file-based result JSON into SQL:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Import-VerificationResultsToDatabase.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -InputFolder "C:\Temp\SignatureVerification\Output"
```

Import synthetic benchmark results:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Import-VerificationResultsToDatabase.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -InputFolder "C:\Temp\SignatureVerification\SyntheticBenchmark\Results"
```

## Generate SQL Review Queue Report

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Export-DatabaseReviewQueue.ps1 `
  -ConnectionString "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;" `
  -OutputFolder "C:\Temp\SignatureVerification\BusinessReports"
```

Outputs:

```text
C:\Temp\SignatureVerification\BusinessReports\db-review-queue.html
C:\Temp\SignatureVerification\BusinessReports\db-review-queue.csv
```

## Seeded Roles

The migration seeds:

```text
Administrator
ReferenceApprover
Reviewer
Auditor
Verifier
```

If no bootstrap key is supplied through `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`, the API starts with a warning and authorization depends on valid DB-managed keys in `ssv.ApiKeyRegistry`. After bootstrap, administrators can create DB-managed keys; these are stored by SHA-256 hash and the raw key is shown only once.

## Purge Coverage

Retention purge is implemented in `SqlProductionWorkflowStore.RunRetentionPurgeAsync`.

- `StorageObject` policies select file/object metadata by explicit `RetainUntilUtc` or policy age.
- `DocumentBlob`, `DebugArtifactBlob`, and `ReportBlob` policies delete DB-native blobs by explicit `RetainUntilUtc` or policy age.
- `FilePurgeEnabled` controls whether eligible filesystem objects are physically deleted by the Admin UI purge path.
- `DatabaseBlobPurgeEnabled=false` disables DB-native blob deletion.
- Purge activity is recorded in `ssv.PurgeRun` and `ssv.PurgeRunItem`.

## Production Notes

- Keep customer document binaries, Base64 payloads, and reference images out of logs.
- Treat `ResultJson`, `SignatureResultJson`, DB-native blob tables, debug artifacts, and review/export outputs as sensitive because they can contain document names, party/reference identifiers, signatures, paths, and extracted evidence.
- Use production-approved SQL encryption settings outside LocalDB.
- Back up the `SignatureVerification` database with the customer-approved SQL backup process. SQL and filesystem replication are useful, but they do not replace point-in-time backups, immutable/offline backup protection, restore testing, config/secret backup, and a documented RPO/RTO runbook.
- Treat `ssv.AuditEvent`, `ssv.ReferenceApprovalEvent`, `ssv.ReviewerOutcome`, and `ssv.PurgeRun` as audit records.
- Use `ssv.vwApprovedReferenceImage` when building a future DB-backed reference provider, so unapproved references cannot be used for matching.
