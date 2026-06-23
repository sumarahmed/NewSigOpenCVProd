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

## Test

```powershell
dotnet .\StaticSignatureVerification.Tests\bin\Release\net8.0\StaticSignatureVerification.Tests.dll
```

Or:

```powershell
dotnet run --project .\StaticSignatureVerification.Tests -c Release
```

## Run Customer Local Folder Test

Prepare:

```text
C:\Temp\SignatureVerification\Input
C:\Temp\SignatureVerification\ReferenceSignatures
C:\Temp\SignatureVerification\Output
```

Run:

```powershell
dotnet run --project .\StaticSignatureVerification.ConsoleTest -c Release
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

### Unexpected English/Chinese Reference Display

The engine compares the form signature against the expected mapped reference set, not against every reference. Check the review queue Mapping column:

- `Expected`
- `Actual` for synthetic benchmark only
- `Ref set`
- `ground truth`

If production mapping is wrong, fix the `signatureMappings`/reference set data from TotalAgility or the reference system.

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
- Review queue opens and shows form/reference images.
- Mapping column shows party/reference context.
- Ghostscript path is configured for PDF environments.
- Thresholds are reviewed and approved.
- Reviewer feedback process is documented.
- Logs/audit retention approach is approved.
- Customer data is excluded from source control.

