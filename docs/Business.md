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
- Enroll, approve, reject, retire, and replace reference signatures with audit history.
- Reject poor-quality reference signatures before they can be used.
- Detect duplicate or suspiciously similar references across parties.
- Manage review cases with assignment, priority, SLA, escalation, and completion tracking.
- Run an authenticated production API service with API-key roles.
- Use retention, purge, encrypted storage metadata, and disaster-recovery export workflows.
- Choose hybrid storage or full DB-native storage during installation.

## Business Meaning Of A Match

A `Matched` decision means the scanned ink is visually consistent with the supplied reference signature set under the configured thresholds.

It does not mean:

- the signer’s identity is legally proven
- the document is not fraudulent
- the signature was made live
- the signature was made by the owner of the reference

The system should be used as operational decision support.

## Storage Mode Choice

During installation, choose one storage mode:

| Mode | Business fit |
| --- | --- |
| `Hybrid` | Best for transition projects where customer documents and reference images are still managed in folders, while SQL stores results, audit, reviewer decisions, and workflow data. |
| `Database` | Best for production operations that need documents, references, debug evidence, reviewer decisions, cases, and audit records managed in SQL instead of loose files. |

DB-native mode gives cleaner governance for retention, backup, disaster recovery, access control, and audit because the processing evidence is stored with the workflow records.

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

Reviewer outcomes can also be stored in SQL through the production API/operations storage layer, so review decisions are no longer limited to browser local storage or CSV files.

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

## Reference Enrollment Workflow

Production reference signatures should be enrolled through the controlled workflow:

1. Add the reference image.
2. Run quality scoring.
3. Store the reference artifact, optionally encrypted.
4. Record party ID, reference set, role, language, and source metadata.
5. Approve or reject the reference.
6. Use only approved references for matching.
7. Retire or replace references when they become outdated.

The workflow records who acted, when, why, and what changed.

## Case Management Workflow

Cases can be created for review-required, not-matched, missing-signature, quality, or business exception scenarios.

Supported case management capabilities:

- priority
- assignment
- due dates/SLA
- branch/customer/document type metadata
- escalation
- completion
- case event history
- case management dashboard

## Security And Compliance Workflow

Production hardening capabilities now include:

- API-key authentication
- role-based access groups for administrator, reference approver, reviewer, auditor, and verifier
- structured audit events
- encrypted local reference artifact storage using Windows DPAPI
- retention policy records
- purge run audit records
- disaster-recovery export packages
- payload-safe logging guidance

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
- Reviewer outcome database support.
- Reference enrollment and approval workflow.
- Reference quality gate.
- Duplicate/wrong-person reference alert workflow.
- Case management workflow and dashboard.
- Authenticated production API service.
- Prerequisite checker and setup wizard.
- Structured audit logging.
- Retention and purge governance.
- Disaster-recovery export package workflow.
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
- disaster recovery evidence: SQL backups, filesystem/config/secret recovery, restore drill results, and approved RPO/RTO

Replication of SQL and filesystem storage is helpful for availability, but it is not enough by itself. Replication can copy accidental deletion, corruption, or ransomware-encrypted data. Production approval should include independent point-in-time backups, restore drills, protected backup copies, documented ownership, monitoring, and a recovery runbook.
