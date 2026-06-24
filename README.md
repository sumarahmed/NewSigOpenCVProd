# StaticSignatureVerification

StaticSignatureVerification is a deterministic C#/.NET 8 engine for comparing scanned handwritten signature images visually against one or more reference signature images. It is intended for document workflow triage and audit support.

It is not biometric identity verification. It does not use pressure, speed, stroke timing, tablet data, cloud APIs, LLMs, paid OCR, paid SDKs, Parascript, Adobe, DocuSign, Azure OpenAI, or commercial signature products. A match means the scanned ink is visually consistent with a reference image under the configured thresholds; it does not prove identity.

## Architecture

- `StaticSignatureVerification.Core`: input detection, image preprocessing, layered signature detection, feature extraction, scoring, decisions, reasoning text, and JSON models.
- `StaticSignatureVerification.PdfRendering`: replaceable PDF rendering interfaces and `GhostscriptCommandLinePdfRenderer`.
- `StaticSignatureVerification.TotalAgilityWrapper`: string-only wrapper for Tungsten/Kofax TotalAgility integration.
- `StaticSignatureVerification.ConsoleTest`: CLI harness for PDFs/images/Base64 plus OCR, references, options, debug output, and JSON results.
- `StaticSignatureVerification.Storage`: SQL Server storage contracts and repositories for results, review outcomes, reference lifecycle, cases, audit, retention, and export metadata.
- `StaticSignatureVerification.Production`: production utilities for reference quality scoring, encrypted artifact storage, readiness checks, and export packaging.
- `StaticSignatureVerification.Operations`: operations CLI for reference enrollment, approval, duplicate scans, case management, retention, purge, DR export, and setup checks.
- `StaticSignatureVerification.Api`: authenticated REST API service for verification and production workflows.
- `StaticSignatureVerification.Tests`: self-running unit test harness using synthetic images.

## Dependencies And Licensing

The core image engine uses OpenCvSharp and System.Text.Json. PDF rendering is intentionally isolated behind `IPdfPageRenderer`.

Ghostscript is AGPL/commercial licensed. This solution does not bundle Ghostscript binaries. If you distribute this engine to customers or package it with Ghostscript, perform licensing review or replace the renderer with another approved renderer. Configure an installed Ghostscript executable path at runtime.

## Build

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path .).Path
dotnet restore .\StaticSignatureVerification.sln
dotnet build .\StaticSignatureVerification.sln -c Release
```

## Run Tests

```powershell
dotnet run --project .\StaticSignatureVerification.Tests\StaticSignatureVerification.Tests.csproj -c Release
```

## Production Operations

One-click Windows install for a target server:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-SignatureVerification.ps1 `
  -StorageMode Database `
  -CreateStartupTask
```

The installer publishes the API and operations tools, initializes or upgrades SQL, creates runtime folders, writes production API configuration, generates an API key when one is not supplied, and optionally registers a Windows startup task. It can detect Ghostscript and can install it only when explicitly requested with `-InstallGhostscript`.

Run the prerequisite checker:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- check-prereqs
```

Run the setup wizard:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard
```

The setup wizard asks whether the install should run as `Hybrid` or `Database`.

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Hybrid
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- setup-wizard --storageMode Database
```

Use `Hybrid` when files remain on disk and SQL stores results/workflow records. Use `Database` when documents, reference images, debug artifacts, results, reviewer outcomes, cases, and audit records should be stored in SQL.

Common production workflow commands:

```powershell
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- enroll-reference --imageFile <path> --referenceSetId <id> --referenceId <id> --signatureId applicant_signature
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- approve-reference --referenceId <id> --actor <user>
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- scan-duplicates --threshold 92
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- create-case --caseNumber CASE-1001 --priority High
dotnet run --project .\StaticSignatureVerification.Operations -c Release -- export-dr --outputFolder C:\Temp\SignatureVerification\Exports
```

Run the API service:

```powershell
$env:SIGNATURE_VERIFICATION_DB_CONNECTION = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;"
$env:SIGNATURE_API_KEYS = "<strong-random-key>:Administrator,ReferenceApprover,Reviewer,Auditor,Verifier"
dotnet run --project .\StaticSignatureVerification.Api -c Release -- --urls http://127.0.0.1:5117
```

Secured API calls require `X-API-Key`. Bootstrap keys can be supplied with `SIGNATURE_API_KEYS` or `SignatureVerification:ApiKeys`; DB-managed keys can also be created through the Admin UI and are stored hashed in `ssv.ApiKeyRegistry`. Use `X-Request-ID` for caller traceability.

Use `POST /api/v1/verify` for hybrid calls that include `referenceSignaturesJson`. Use `POST /api/v1/verify-db-native` for DB-native calls where approved reference images are loaded from SQL by role/party/reference-set mapping.

Open the administrative UI after the API is running:

```text
http://127.0.0.1:5117/admin
```

The UI uses the configured `X-API-Key` and provides operational views for readiness, storage mode, purge settings, database counts, latest documents, references, case queue, retention policies and purge runs, API keys, users/roles, templates/zones, threshold profiles, backup/export package requests, service status, and audit events.

## Documentation

Detailed documentation is split by audience:

- [Technical Documentation](docs/Technical.md)
- [Business Documentation](docs/Business.md)
- [Administration Guide](docs/Administration.md)
- [Operations Runbook](docs/Operations.md)
- [SQL Database Reference](docs/SQL-Database.md)
- [Production Readiness Review](docs/Production-Readiness-Review.md)

Keep these documents updated when API contracts, configuration, reporting, or operational behavior changes. See [docs/README.md](docs/README.md) for the maintenance checklist.

## Console Usage

### Simple Local Folder Mode

Create these folders:

```powershell
New-Item -ItemType Directory -Force `
  "C:\Temp\SignatureVerification\Input", `
  "C:\Temp\SignatureVerification\Output", `
  "C:\Temp\SignatureVerification\ReferenceSignatures"
```

Put one or more scanned documents in this folder or any subfolder below it:

```text
C:\Temp\SignatureVerification\Input
```

Supported document files are `.pdf`, `.png`, `.jpg`, `.jpeg`, `.tif`, `.tiff`, `.bmp`, `.base64`, `.b64`, or `documentBase64.txt`. In local folder mode, PDFs search up to 100 pages by default and clear any fixed `pageIndex` from `options.json`, so signatures on later pages are included.

Put references in:

```text
C:\Temp\SignatureVerification\ReferenceSignatures
```

Use either:

- `reference-signatures.json` for advanced/manual mapping
- or plain reference image files whose filename exactly matches the input document filename
- or subfolders, where each subfolder becomes a separate signature set, but the reference image filename must still exactly match the input document filename

Examples:

```text
Input\Customer123.pdf
ReferenceSignatures\Customer123.png
```

```text
Input\Customer123.pdf
ReferenceSignatures\applicant_signature\Customer123.png
ReferenceSignatures\witness_signature\Customer123.png
```

The app will not compare `Customer123.pdf` against unrelated reference files such as `OtherCustomer.png`.

Optional files in `Input`:

- `options.json`
- any `*ocr*.json`, `*msdi*.json`, or `*layout*.json`
- `ghostscript-path.txt` containing the full path to `gswin64c.exe` for PDF tests

For signed PDFs, the detector also prefers later-page candidates when confidence is comparable because signature blocks are often near the end of the document. Override this with:

```json
{
  "pdfRendering": {
    "maxPages": 25
  },
  "detection": {
    "preferLaterPages": false
  }
}
```

Then run with no arguments:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest
```

Each input file gets its own output folder, with subfolder names folded into the output name, so results are not overwritten:

```text
C:\Temp\SignatureVerification\Output\Customer123\result.json
C:\Temp\SignatureVerification\Output\GeneratedSamples_SyntheticEnglishHandwritten\result.json
```

Debug images are written to:

```text
C:\Temp\SignatureVerification\Output\Customer123\debug
```

The audit JSON includes the actual generated reference filename in each comparison:

```json
{
  "referenceId": "signature_ref_1",
  "referenceFileName": "Customer123.png",
  "referenceFilePath": "C:\\Temp\\SignatureVerification\\ReferenceSignatures\\Customer123.png"
}
```

Thresholds can be customized in `Input\options.json`:

```json
{
  "thresholds": {
    "matched": 90,
    "probableMatch": 78,
    "reviewRequired": 60
  }
}
```

Expected signature roles can also be configured in `Input\options.json`. Use `Auto` to verify the matching reference sets found for the input file, or use a fixed mode when the form type is known:

```json
{
  "signatureRoles": {
    "mode": "Auto"
  }
}
```

Supported fixed modes are `ApplicantOnly`, `ApplicantWitness`, and `ApplicantWitnessAuthorised`. `ApplicantWitnessAuthorised` expects applicant, witness, and authorised signature reference sets.

### Manual CLI Mode

PDF input:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest -- `
  --documentPdfFile "C:\Input\form.pdf" `
  --ocrJsonFile "C:\Input\msdi-layout.json" `
  --referencesJsonFile "C:\Input\reference-signatures.json" `
  --optionsJsonFile "C:\Input\options.json" `
  --outputJsonFile "C:\Output\result.json" `
  --debugOutputFolder "C:\Output\debug" `
  --ghostscriptPath "C:\Program Files\gs\gs10.xx.x\bin\gswin64c.exe" `
  --dpi 300
```

Image input:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest -- `
  --documentImageFile "C:\Input\form.png" `
  --referencesJsonFile "C:\Input\reference-signatures.json" `
  --optionsJsonFile "C:\Input\options.json" `
  --outputJsonFile "C:\Output\result.json"
```

## TotalAgility Wrapper

```csharp
var wrapper = new SignatureVerificationWrapper();
string json = wrapper.VerifySignatures(
    documentBase64,
    ocrLayoutJson,
    referenceSignaturesJson,
    optionsJson);
```

The wrapper always returns JSON and catches raw exceptions. The convenience overload `VerifySignature(documentBase64, reference1Base64, reference2Base64)` creates a single expected signature set.

For production TotalAgility integration, prefer the structured request entry point. It validates required fields, propagates a correlation ID into the result audit, and keeps threshold and role parameters explicit:

```csharp
string json = wrapper.VerifySignaturesRequest(requestJson);
```

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
      "partyName": "Applicant Name",
      "referenceSetId": "SIGNER_0032_ENGLISH",
      "expectedSignerId": "SIGNER_0032_ENGLISH",
      "source": "TotalAgility"
    }
  ],
  "optionsJson": "{...}",
  "matchedThreshold": "79",
  "probableMatchThreshold": "69",
  "reviewRequiredThreshold": "50",
  "signatureRoleMode": "ApplicantWitness"
}
```

`signatureMappings` is optional but strongly recommended. It tells the result and review UI which business party/reference set each form role was verified against. You can pass it as a JSON array/object in `signatureMappings`, or as a string in `signatureMappingsJson`. The same mapping can also be embedded directly inside each `referenceSets[]` entry:

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
          "imageBase64": "..."
        }
      ]
    }
  ]
}
```

The result JSON echoes this under each `signatureResults[].mapping`, so downstream reports can show: role `applicant_signature` mapped to party/reference set `0032`, and only that enrolled reference set was used for matching.

Every result includes an `operationalAudit` object with sanitized runtime metadata: correlation ID, UTC start/end, duration, status, error code, page/reference/signature counts, warning/error counts, and whether debug images were written. This is intended for production logging and reconciliation without storing raw document or signature bytes in logs.

Thresholds can be passed in `optionsJson`:

```json
{
  "thresholds": {
    "matched": 90,
    "probableMatch": 78,
    "reviewRequired": 60
  }
}
```

Or by using the explicit string-parameter overload:

```csharp
string json = wrapper.VerifySignaturesWithThresholds(
    documentBase64,
    ocrLayoutJson,
    referenceSignaturesJson,
    optionsJson,
    "90",
    "78",
    "60");
```

For the single-signature convenience method:

```csharp
string json = wrapper.VerifySignatureWithThresholds(
    documentBase64,
    reference1Base64,
    reference2Base64,
    "90",
    "78",
    "60");
```

The same `optionsJson` parameter can configure expected form roles:

```json
{
  "signatureRoles": {
    "mode": "ApplicantWitness"
  }
}
```

Or pass the role mode as a string parameter:

```csharp
string json = wrapper.VerifySignaturesWithSignatureRoleMode(
    documentBase64,
    ocrLayoutJson,
    referenceSignaturesJson,
    optionsJson,
    "ApplicantWitness");
```

## OCR/MSDI Layout JSON

The engine never calls OCR services. If the caller already has Azure Document Intelligence or MSDI Layout JSON, pass it as `ocrLayoutJson`. The parser accepts common `analyzeResult.pages[].lines[]` and `words[]` structures with `content`, `text`, `polygon`, or `boundingPolygon`, and converts normalized, inch, or pixel-like coordinates to rendered image pixels.

## Known Zones

Known zones are the most reliable detection source. Configure them in `optionsJson`:

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

Detection priority is known zones, OCR anchors, box detection, then handwriting-like ink region fallback.

The detector scores a larger candidate pool before trimming to the configured maximum. Tune this with `detection.candidatePoolMultiplier` when forms contain multiple nearby handwritten regions.

## Thresholds And Audit

Default decisions:

- `Matched`: confidence >= 85
- `ProbableMatch`: confidence >= 70 and requires review
- `ReviewRequired`: confidence >= 55
- `NotMatched`: confidence < 55
- `NoSignatureDetected` or `InsufficientQuality`: requires review

Audit JSON includes ink density, geometry, density grids, contour metrics, skeleton metrics, connected components, quality flags, candidate regions, preprocessing details, and all reference comparisons.

## Synthetic Benchmark Dataset

The benchmark project can generate fictional English and Chinese handwritten-style signatures, synthetic form images, per-document ground truth, OCR layout JSON, verifier results, and CSV/JSON/HTML reports.

Generate and run the default 500-document benchmark:

```powershell
dotnet run --project .\StaticSignatureVerification.Benchmark -c Release -- generate-run --root C:\Temp\SignatureVerification\SyntheticBenchmark --documents 500 --signers 50 --references 3 --clean
```

Run an existing generated dataset again after changing the engine:

```powershell
dotnet run --project .\StaticSignatureVerification.Benchmark -c Release -- run --root C:\Temp\SignatureVerification\SyntheticBenchmark
```

Generated folders:

```text
SyntheticBenchmark\Input
SyntheticBenchmark\ReferenceSignatures
SyntheticBenchmark\GroundTruth
SyntheticBenchmark\OcrLayout
SyntheticBenchmark\Results
SyntheticBenchmark\Report
```

Report files:

```text
SyntheticBenchmark\Report\benchmark-report.html
SyntheticBenchmark\Report\benchmark-results.csv
SyntheticBenchmark\Report\benchmark-summary.json
```

Use this dataset to stress-test detection, role assignment, missing signatures, mismatches, wrong-field signatures, poor-quality signatures, and threshold settings. Synthetic results are useful for engineering and calibration, but they are not a substitute for measuring false accept and false reject rates on real production documents.

The benchmark summary also reports document processing latency metrics: average, p50, p95, and total processing milliseconds. Use these numbers as regression signals; production sizing still requires real scanned documents, real PDF rendering, and the expected TotalAgility concurrency level.

## Business Reporting

`StaticSignatureVerification.Reporting` generates a business-ready HTML report plus CSV exports from verifier result JSON files. It summarizes daily, weekly, and monthly activity and includes drill-down tables for reference usage and individual reference cases.

Generate a report from local console output:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -- `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports `
  --title "Signature Verification Business Report"
```

Generate a report from the synthetic benchmark results:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -- `
  --input C:\Temp\SignatureVerification\SyntheticBenchmark\Results `
  --output C:\Temp\SignatureVerification\BusinessReports
```

Report outputs:

```text
BusinessReports\signature-business-report.html
BusinessReports\review-queue.html
BusinessReports\reference-registry.html
BusinessReports\reporting-health-check.html
BusinessReports\batch-manifest.json
BusinessReports\daily-summary.csv
BusinessReports\weekly-summary.csv
BusinessReports\monthly-summary.csv
BusinessReports\reference-summary.csv
BusinessReports\reference-cases.csv
BusinessReports\review-queue.csv
BusinessReports\reviewer-outcomes-template.csv
```

Run the reporting health check:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -- `
  --health-check `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports
```

The review queue is a static HTML workbench for human reviewers. It lists cases that need attention and can export reviewer outcomes to CSV. The reference registry highlights reference signatures used by the engine and flags references that may need review because of low confidence or not-matched outcomes.

### Reviewer Feedback Tuning

`review-queue.html` saves reviewer choices row-by-row in the browser as the reviewer works. Use **Save** on a line for an explicit save, or simply change the reviewer fields and then use **Export Reviewer Outcomes**. The export creates `reviewer-outcomes.csv` with the case IDs, engine decision, confidence, source/debug paths, reference path, reviewer outcome, reason, reviewer name, notes, and save time.

Feed that CSV back into the reporting project to create tuning and maintenance recommendations:

```powershell
dotnet run --project .\StaticSignatureVerification.Reporting -- `
  --feedback-tune `
  --input C:\Temp\SignatureVerification\Output `
  --output C:\Temp\SignatureVerification\BusinessReports `
  --reviewer-outcomes C:\Temp\SignatureVerification\BusinessReports\reviewer-outcomes.csv `
  --current-options C:\Temp\SignatureVerification\Input\options.json
```

Feedback tuning outputs:

```text
BusinessReports\feedback-tuning-report.html
BusinessReports\feedback-tuning-summary.json
BusinessReports\feedback-tuned-options.json
BusinessReports\feedback-threshold-sweep.csv
BusinessReports\reference-maintenance-actions.csv
```

The tuning report only uses reviewer-labelled rows from the supplied CSV. It does not invent production accuracy numbers. `feedback-tuned-options.json` is a generated recommendation file; validate it on a holdout set before using it in production. Reference actions are queued for review instead of silently copying signatures into the reference library.

## Security Notes

- The engine does not execute arbitrary shell commands.
- Ghostscript is invoked only via the configured executable path with `UseShellExecute = false`.
- PDF rendering enforces max page count and timeout.
- Temp files are created under controlled folders and cleaned up unless debug retention is enabled.
- Full Base64 input is not logged.
- Returned error JSON uses safe messages and does not include stack traces.
- Debug images are written only when `saveDebugImages` is true.

## Limitations

This MVP uses deterministic OpenCV approximations. It is practical for scanned form triage, but not a substitute for forensic review, biometric verification, or identity proofing. Highly compressed scans, overlapping printed text, stamps, low DPI, severe skew, and incomplete crops can reduce confidence.

Future extensions can add an optional ONNX/Siamese visual similarity model behind a separate interface while keeping deterministic scoring and audit output available.
