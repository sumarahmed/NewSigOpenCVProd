# Business Documentation

## Business Purpose

The signature verification solution helps document operations teams triage scanned forms by comparing each expected form signature against the correct enrolled reference signature set.

It supports faster review, clearer audit evidence, and better exception handling. It does not prove legal identity or replace final human/forensic judgment for high-risk disputes.

## Key Capabilities

- Verify one or more expected signatures on a form.
- Support applicant, witness, and authorised signature roles.
- Compare against multiple reference samples for the same role/party.
- Search all PDF pages, with later-page preference for signed forms.
- Use known zones, OCR/MSDI layout anchors, box detection, and ink-region fallback.
- Show the actual form signature crop side by side with the best reference.
- Show the business mapping between role, party ID, reference set, and expected signer.
- Export reviewer decisions from the review queue.
- Use reviewer feedback to generate threshold and reference-maintenance recommendations.
- Generate daily, weekly, and monthly business statistics.
- Generate reference usage and case drill-down reports.

## Business Meaning Of A Match

A `Matched` decision means the scanned ink is visually consistent with the supplied reference signature set under the configured thresholds.

It does not mean:

- the signer’s identity is legally proven
- the document is not fraudulent
- the signature was made live
- the signature was made by the owner of the reference

The system should be used as operational decision support.

## Role And Party Mapping

Each signature role should map to a specific business party/reference set.

Example:

```text
Document: Customer123.pdf
Role: applicant_signature
Party ID: 0032
Reference set: SIGNER_0032_ENGLISH
```

The engine compares the applicant signature only against that mapped reference set. It should not search all enrolled references for a visually similar signature because that can accidentally match the wrong person.

In synthetic testing, the review UI also shows:

- expected signer
- actual signer
- expected class
- ground truth link

These are for test analysis. In production, the business system should provide party/reference mapping from TotalAgility or the customer master/reference system.

## Review Queue Workflow

The review queue is generated as:

```text
C:\Temp\SignatureVerification\BusinessReports\review-queue.html
```

Each row shows:

- document link
- role
- mapping
- engine decision and confidence
- form signature crop
- best reference signature
- reviewer outcome controls
- reason, reviewer, notes
- row-level save

Reviewer outcomes:

- `Accepted by reviewer`
- `Rejected by reviewer`
- `Needs second review`

Reason codes:

- `Signature match acceptable`
- `Signature mismatch`
- `Missing signature`
- `Wrong field detected`
- `Poor scan quality`
- `Reference outdated`
- `Reference missing`

## Feedback Tuning Workflow

Reviewer feedback is exported to `reviewer-outcomes.csv`.

The feedback tuning runner consumes that CSV and generates:

- `feedback-tuning-report.html`
- `feedback-tuning-summary.json`
- `feedback-tuned-options.json`
- `feedback-threshold-sweep.csv`
- `reference-maintenance-actions.csv`

The feedback loop is intentionally controlled. It recommends changes; it does not silently update production thresholds or reference libraries.

## Business Reports

The business report package includes:

- `signature-business-report.html`
- `review-queue.html`
- `reference-registry.html`
- `daily-summary.csv`
- `weekly-summary.csv`
- `monthly-summary.csv`
- `reference-summary.csv`
- `reference-cases.csv`
- `review-queue.csv`
- `reviewer-outcomes-template.csv`
- `batch-manifest.json`

Reports help answer:

- How many documents and signature cases were processed?
- How many matched, required review, failed, or had no signature?
- Which references are frequently used?
- Which references are associated with low confidence or not-matched outcomes?
- Which documents need human review?
- Which reviewer outcomes suggest threshold or reference maintenance changes?

## Recommended Production Roles

- Operations reviewer: reviews side-by-side cases and records outcome/reason.
- Business owner: approves threshold changes and reference maintenance actions.
- System administrator: maintains folders, Ghostscript path, options, and reference inputs.
- Developer/integrator: maintains TotalAgility API calls and mapping metadata.
- Auditor/compliance reviewer: reviews audit output, reviewer outcomes, and change approvals.

## Customer-Ready Feature List

- Deterministic signature comparison engine.
- Multi-signature role support.
- Multiple reference samples per role.
- PDF and image input support.
- Known-zone and OCR-anchor detection.
- Full-page fallback detection.
- Later-page preference for PDF signing pages.
- Structured TotalAgility wrapper.
- Custom threshold support.
- Signature role mode support.
- Business mapping metadata support.
- Audit-rich JSON result.
- Per-document output isolation.
- Debug image support.
- Business reports and visual review queue.
- Reviewer outcome capture.
- Feedback-based tuning recommendations.
- Synthetic benchmark dataset and report.

## Production Readiness Notes

Before production rollout, validate on representative customer documents and reference signatures. Synthetic data is useful for engineering regression checks but cannot establish customer false accept or false reject rates.

Recommended acceptance evidence:

- real customer holdout test set
- documented threshold selection
- reviewer feedback sample
- false accept and false reject analysis
- reference enrollment governance
- operational logging/audit retention plan
- TotalAgility integration test evidence

