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
- `StaticSignatureVerification.Tests`: self-running regression test harness.

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
8. Compare each candidate to the reference images for that exact signature set.
9. Assign non-overlapping candidates to expected signature roles.
10. Return result JSON with decisions, confidence, audit metrics, debug image paths, mapping metadata, warnings, and errors.

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

