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
| Debug image output | local runner creates per-document `Output\<document>\debug`; can also set `saveDebugImages` and `debugOutputFolder` in `options.json` | `optionsJson.saveDebugImages` and `optionsJson.debugOutputFolder` | `DebugImageWriter` in `StaticSignatureVerification.Core\SignatureVerificationCore.cs` |
| Business report source/output | Reporting command `--input` and `--output` | Not used by wrapper; reports consume result JSON files | `StaticSignatureVerification.Reporting\Program.cs` |
| Feedback tuning | Reporting command `--feedback-tune`, `--reviewer-outcomes`, `--current-options` | Exported reviewer CSV from review UI | `FeedbackTuningReport` in `StaticSignatureVerification.Reporting\Program.cs` |
| Synthetic benchmark size | Benchmark command `--documents`, `--signers`, `--references`, `--seed` | Not applicable | `StaticSignatureVerification.Benchmark\Program.cs` |

### Most Common Local Changes

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
