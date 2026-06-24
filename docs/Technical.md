# Technical Documentation

## Purpose

StaticSignatureVerification is a deterministic .NET 8 signature verification solution for scanned documents. It compares detected form signature ink against the expected reference signature set for the relevant role or party.

It is visual similarity software, not biometric identity proofing. It does not use pressure, speed, stroke timing, cloud AI, paid OCR, or commercial signature verification services.

## Projects

- `StaticSignatureVerification.Core`: document/image detection, preprocessing, signature detection, feature extraction, scoring, decisions, JSON models, audit output.
- `StaticSignatureVerification.PdfRendering`: PDF rendering abstraction and Ghostscript command-line renderer.
- `StaticSignatureVerification.TotalAgilityWrapper`: string-only TotalAgility-compatible wrapper API.
- `StaticSignatureVerification.ConsoleTest`: local folder and manual CLI runner.
- `StaticSignatureVerification.Reporting`: business reports, visual review queue, reviewer outcome export, feedback tuning.
- `StaticSignatureVerification.Benchmark`: synthetic English/Chinese handwriting dataset generator and benchmark runner.
- `StaticSignatureVerification.Storage`: database storage contracts and shared database DTOs.
- `StaticSignatureVerification.Production`: production utilities for reference quality scoring, encrypted artifact storage, readiness checks, and export packaging.
- `StaticSignatureVerification.Operations`: administration CLI for reference lifecycle, case management, retention, purge, export, and setup checks.
- `StaticSignatureVerification.Api`: authenticated REST API service for verification and production workflows.
- `StaticSignatureVerification.Tests`: self-running unit/integration test harness.
- `StaticSignatureVerification.Regression`: named 20-case core regression pack that produces Markdown, CSV, JSON, and synthetic test assets.

## Application Service Layer

The production orchestration seam is `VerificationApplicationService` in `StaticSignatureVerification.Core`.

It accepts a `SignatureVerificationRequest` and delegates to the deterministic engine while keeping the public JSON contract stable. The TotalAgility wrapper now calls this service instead of constructing and coordinating the pipeline directly.

The engine pipeline is exposed through unit-testable interfaces:

- `IDocumentInputDetector`
- `IOcrLayoutParser`
- `ISignatureDetector`
- `ISignaturePreprocessor`
- `IFeatureExtractor`
- `ISimilarityScorer`
- `IDecisionEngine`
- `IReasoningBuilder`
- `IDebugImageWriter`
- `IResultBuilder`

Default concrete implementations preserve the existing algorithm. Tests can inject fakes or custom profiles without changing the public wrapper.

Shared infrastructure services:

- `SignatureJsonOptions`: one camelCase JSON contract for compact and indented output.
- `EnvironmentSignatureVerificationConfigService`: reads runtime root, database connection, Ghostscript path, and storage mode from environment variables.
- `IVerificationLogger`: records sanitized internal pipeline events, warnings, and errors without logging Base64 payloads.
- `VerificationProfiles`: named detection, preprocessing, and scoring constants that replace hidden magic numbers.

## Processing Pipeline

1. Parse `optionsJson` and normalize thresholds, detection settings, role mode, known zones, and PDF rendering settings.
2. Parse `referenceSignaturesJson`, including optional business mapping metadata.
3. Detect input type as PDF or image.
4. Render PDF pages if needed.
5. Parse optional OCR/MSDI layout JSON.
6. Preprocess reference images into normalized binary/skeleton features.
7. Detect candidate signature regions by priority:
   - known zones
   - OCR anchors
   - form box detection
   - handwriting-like ink regions
8. Compare each candidate to the reference images for that exact signature set using normalized comparison metrics.
9. Assign non-overlapping candidates to expected signature roles.
10. Check copied-signature risk across detected roles.
11. Return result JSON with decisions, confidence, audit metrics, debug image paths, mapping metadata, warnings, and errors.

## Matching Scope

The engine does not search every reference image globally. It compares a detected form signature only against the reference images supplied for that signature role/reference set.

Example: if `applicant_signature` maps to `partyId = 0032` and `referenceSetId = SIGNER_0032_ENGLISH`, the applicant form signature is compared only to that reference set. A Chinese-looking form signature will not be searched against all Chinese references; it should be rejected or reviewed against the expected party reference.

## Reference Contract

`referenceSignaturesJson` contains one or more `referenceSets`.

```json
{
  "referenceSets": [
    {
      "signatureId": "applicant_signature",
      "displayName": "Applicant Signature",
      "partyId": "0032",
      "partyName": "Applicant Name",
      "referenceSetId": "SIGNER_0032_ENGLISH",
      "expectedSignerId": "SIGNER_0032_ENGLISH",
      "expectedLabels": ["Applicant Signature"],
      "referenceImages": [
        {
          "referenceId": "applicant_0032_ref_1",
          "sourceFileName": "ref_01.png",
          "sourceFilePath": "C:\\Temp\\SignatureVerification\\ReferenceSignatures\\applicant_signature\\Customer123.png",
          "imageBase64": "..."
        }
      ]
    }
  ]
}
```

Multiple reference images are supported per role/reference set through `referenceImages[]`.

## Mapping Metadata

Mapping metadata links form roles to business parties/reference sets. It can be embedded on each `referenceSets[]` item or passed through the structured TotalAgility request as `signatureMappings` or `signatureMappingsJson`.

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

The result echoes this under `signatureResults[].mapping`, so reports can show exactly why a role was compared to a particular reference set.

Synthetic benchmark results additionally include:

- `actualSignerId`
- `expectedClass`
- `source = SyntheticGroundTruth`

These are test-only truth fields and should not be expected from production unless the caller supplies them.

## Storage Modes

The runtime supports two storage modes.

| Mode | Contract |
| --- | --- |
| `Hybrid` | Documents, reference images, debug images, and reports may remain on disk. SQL stores result records, reviewer outcomes, reference lifecycle metadata, audit, cases, retention, and export metadata. |
| `Database` | Incoming documents, approved reference images, result records, debug artifacts, reviewer outcomes, audit, and workflow data are stored in SQL. |

The selected mode is stored in `ssv.SystemSetting` as `StorageMode`. The setup wizard can prompt for it interactively or accept `--storageMode Hybrid` / `--storageMode Database`.

## TotalAgility API

Preferred entry point:

```csharp
var wrapper = new SignatureVerificationWrapper();
string resultJson = wrapper.VerifySignaturesRequest(requestJson);
```

Required fields:

- `documentBase64`
- `referenceSignaturesJson`

Optional fields:

- `correlationId`
- `ocrLayoutJson`
- `optionsJson`
- `matchedThreshold`
- `probableMatchThreshold`
- `reviewRequiredThreshold`
- `signatureRoleMode`
- `signatureMappings`
- `signatureMappingsJson`

Example:

```json
{
  "correlationId": "TA-CASE-12345",
  "documentBase64": "...",
  "ocrLayoutJson": "{...}",
  "referenceSignaturesJson": "{...}",
  "signatureMappings": [
    {
      "signatureId": "applicant_signature",
      "partyId": "0032",
      "referenceSetId": "SIGNER_0032_ENGLISH",
      "source": "TotalAgility"
    }
  ],
  "matchedThreshold": "79",
  "probableMatchThreshold": "69",
  "reviewRequiredThreshold": "50",
  "signatureRoleMode": "ApplicantWitness"
}
```

## Result Contract

Top-level fields include:

- `documentResultId`
- `engineVersion`
- `inputDocumentType`
- `overallDecision`
- `overallConfidence`
- `signatureCountExpected`
- `signatureCountDetected`
- `signatureResults[]`
- `operationalAudit`
- `warnings[]`
- `errors[]`

Each `signatureResults[]` item includes:

- role fields: `signatureId`, `displayName`
- business mapping: `mapping`
- decision fields: `decision`, `confidence`, `isMatched`, `reviewRequired`, `signatureDetected`
- detection fields: `detectedRegion`, `candidateRegions`
- reference fields: `bestReferenceId`, `audit.referenceComparisons[]`
- score details: `audit.scoreBreakdown`
- preprocessing/debug details: `audit.preprocessing`, `audit.debugImages`

## Decisions

Default thresholds:

- `Matched`: confidence >= 85
- `ProbableMatch`: confidence >= 70, review required
- `ReviewRequired`: confidence >= 55
- `NotMatched`: confidence < 55
- `NoSignatureDetected`: signature was expected but no usable ink was detected
- `InsufficientQuality`: signature exists but quality is too poor for an automated decision

Thresholds are configurable in `optionsJson` and via wrapper parameters.

Important: threshold changes should not be used to compensate for wrong-signer behavior. The engine includes structural reject gates so obvious mismatches can become `NotMatched` even when raw aggregate similarity would otherwise land in the review band.

## Scoring

The engine extracts deterministic visual features:

- geometry
- ink density
- density grids
- contour metrics
- skeleton metrics
- structural pixel similarity
- connected components
- quality indicators

The scorer also applies an audited structural mismatch penalty when shape-family signals strongly disagree. This reduces borderline review decisions for obvious cross-style mismatches, such as a Chinese-style form signature against an English reference set.

Current scoring controls also include:

- normalized binary metrics for both query and reference signatures, so known-zone crops and standalone reference images are compared on the same canvas basis
- hard structural reject caps for clear wrong-signer/reference cases where geometry, density grid, contour, and structural evidence disagree
- rotation/skew tolerant score flooring when structural, grid, contour, and skeleton evidence indicate the same signature despite page or scan angle

These controls are profile-driven through `VerificationProfiles.Scoring`.

## Copied Signature Risk

After role assignment, the engine compares detected query signatures across different expected roles/signers. If two different roles contain a near-identical detected signature pattern, the engine adds:

```text
REUSED_COPIED_SIGNATURE_HIGH_RISK
```

The affected signature results are forced to `ReviewRequired`. This is intended to flag reused or copied signature images, for example the same ink pattern appearing for both applicant and witness roles.

## Regression Pack

Run the core regression pack:

```powershell
dotnet run --project .\StaticSignatureVerification.Regression\StaticSignatureVerification.Regression.csproj -c Release -- --output=.verification\regression-pack
```

The pack currently contains `TC001` through `TC020`, including genuine match, different signer, missing signature, multi-signature mapping, rotated/skewed pages, stamp/handwriting false-positive controls, low-resolution/cropped signatures, invalid Base64, corrupt PDF, large PDF, missing reference, wrong reference, copied-signature risk, box-border removal, and TotalAgility JSON validation.

Expected current result:

```text
20 passed / 20 total
```

Generated artifacts:

- `.verification\regression-pack\regression-report.md`
- `.verification\regression-pack\regression-results.csv`
- `.verification\regression-pack\regression-results.json`
- `.verification\regression-pack\assets`

## PDF Rendering

PDF rendering is isolated behind `IPdfPageRenderer`. The included renderer calls Ghostscript command-line (`gswin64c.exe`) and does not bundle Ghostscript.

Important options:

- `pdfRendering.ghostscriptExecutablePath`
- `pdfRendering.maxPages`
- `pdfRendering.timeoutSeconds`
- `dpi`
- `pageIndex`

Local folder mode clears fixed `pageIndex` for PDF testing so all pages can be searched by default.

## Reports And Feedback

`StaticSignatureVerification.Reporting` reads result JSON files recursively and generates:

- business summary report
- review queue
- reference registry
- period summary CSVs
- case-level CSVs
- reviewer outcome template
- feedback tuning report

Reviewer feedback is controlled and auditable. The system creates recommendations and action queues; it does not silently overwrite production thresholds or enroll new reference images.

## Database Storage

The database migration adds a SQL Server storage layer beside the existing file workflow. File processing remains available, and completed `result.json` files can be imported into SQL for backfill. The console/local folder runner can also write directly to SQL at runtime when a database connection string is supplied.

For a DBA-friendly reference with all tables, views, migrations, connection strings, and verification queries, see [SQL Database Reference](SQL-Database.md).

Schema file:

```text
database\001_initial_schema.sql
```

Main SQL objects:

- `ssv.VerificationDocument`: one row per processed document result.
- `ssv.SignatureCase`: one row per expected/detected signature role.
- `ssv.ReferenceComparison`: audited candidate reference comparisons.
- `ssv.DebugArtifact`: reserved for persisted debug image metadata.
- `ssv.ReviewerOutcome`: reviewer decisions and feedback.
- `ssv.ThresholdProfile`: approved threshold profiles.
- `ssv.vwReviewQueue`: SQL-backed review queue projection.

Production workflow SQL objects:

- `ssv.SchemaMigration`: migration ledger for applied database scripts.
- `ssv.ReferenceSubject`: party/customer identity for reference enrollment.
- `ssv.ReferenceSetRegistry`: approved, draft, retired, and replaced reference sets.
- `ssv.ReferenceImageRegistry`: individual reference images, quality score, approval status, hash, storage URI, and active dates.
- `ssv.ReferenceApprovalEvent`: approval, replacement, rejection, and retirement audit trail.
- `ssv.ReferenceSimilarityAlert`: duplicate/wrong-person reference alerts.
- `ssv.FormTemplate` and `ssv.FormTemplateZone`: versioned form templates and known signature zones.
- `ssv.ResultGovernanceSnapshot`: engine/template/threshold/reference versions used for a result.
- `ssv.ReviewCase` and `ssv.ReviewCaseEvent`: case assignment, status, SLA, escalation, and event history.
- `ssv.SecurityRole` and `ssv.SecurityPrincipalRole`: database-backed RBAC foundation.
- `ssv.AuditEvent`: structured operational/security audit events.
- `ssv.StorageObject`: encrypted storage metadata for documents, references, debug images, and reports.
- `ssv.RetentionPolicy`, `ssv.PurgeRun`, and `ssv.PurgeRunItem`: retention and purge governance.
- `ssv.ExportPackage`: disaster recovery/export package tracking.
- `ssv.vwApprovedReferenceImage`: only approved reference images eligible for matching.
- `ssv.vwCaseManagementQueue`: active case management queue projection.

LocalDB default:

```text
Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;
```

Storage contracts are in:

```text
StaticSignatureVerification.Storage\StorageContracts.cs
StaticSignatureVerification.Storage\JsonVerificationResultMapper.cs
StaticSignatureVerification.Storage\SqlVerificationResultStore.cs
```

The storage project uses `Microsoft.Data.SqlClient`. The core verification engine remains storage-agnostic; the console runner converts result JSON into storage records and persists them through `SqlVerificationResultStore` only when configured.

Database migrations are applied in filename order from:

```text
database\*.sql
```

The initializer records each applied file in `ssv.SchemaMigration` and safely skips already-applied migrations.

## Production API And Workflow Layer

The production API is a minimal ASP.NET Core service with API-key authentication and role checks. It provides:

- verification endpoint backed by `SignatureVerificationWrapper`
- DB-native verification endpoint that loads approved references from SQL
- result persistence through `SqlVerificationResultStore`
- reference enrollment, approval, rejection, retirement, and duplicate scanning
- review case create/assign/complete/escalate/cancel
- reviewer outcome persistence
- retention purge trigger
- readiness endpoint
- case dashboard endpoint
- browser-based Admin UI for settings, retention, API keys, users/roles, templates/zones, thresholds, backup/export requests, service status, and audit

API keys can be loaded from process configuration (`SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`) and from `ssv.ApiKeyRegistry`. DB-managed keys are compared by SHA-256 hash, can expire or be revoked, and update `LastUsedUtc` when used.

Retention policy updates in the Admin UI are based on existing rows in `ssv.RetentionPolicy`; free-text policy creation is intentionally blocked in the UI and guarded by the API to reduce typo-created policies.

The operations CLI uses the same production/storage layer, so local administration and API behavior share the same database contracts.

Use these verification contracts:

| Endpoint | Storage mode | Reference source |
| --- | --- | --- |
| `POST /api/v1/verify` | `Hybrid` or explicit caller-managed reference payloads | Request field `referenceSignaturesJson` |
| `POST /api/v1/verify-db-native` | `Database` | Approved SQL rows in `ssv.vwApprovedReferenceImageNative` |

DB-native request example:

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

In DB-native mode, reference enrollment stores image bytes in `ssv.ReferenceImageBlob`. Verification stores input document bytes in `ssv.DocumentBlob` and debug image bytes in `ssv.DebugArtifactBlob`.

The API may create temporary debug image files during processing because the core engine writes debug images as files. After DB-native verification, those bytes are stored in SQL, debug artifact paths are rewritten as `sql://` URIs, and the temporary debug folder is removed.

Reference enrollment quality scoring checks image size, ink density, sharpness, average hash, and SHA-256. Failed references are stored as rejected and are not approved for matching. Approved references are exposed through `ssv.vwApprovedReferenceImage`.

Encrypted local reference storage uses Windows DPAPI through `SecureArtifactStore` when encryption is enabled. Storage metadata is tracked in SQL with hash and storage URI fields.
