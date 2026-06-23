# Signature Verification Documentation

This folder contains the maintained documentation set for the signature verification solution.

- [Technical Documentation](Technical.md): architecture, APIs, data contracts, matching pipeline, and extension points.
- [Business Documentation](Business.md): business capabilities, user workflows, reports, review queue, feedback tuning, and customer value.
- [Administration Guide](Administration.md): configuration, folder setup, thresholds, role/reference mapping, Ghostscript, and environment settings.
- [Operations Runbook](Operations.md): build/run commands, health checks, monitoring, troubleshooting, cleanup, and release checklist.

## Documentation Maintenance

Update these docs whenever behavior, configuration, API contracts, reporting output, or operational steps change.

- API or JSON contract changed: update `Technical.md`, `Administration.md`, and sample payloads.
- Business workflow/report changed: update `Business.md` and `Operations.md` if run steps changed.
- Threshold, role, reference, PDF, or folder behavior changed: update `Administration.md`.
- New command, report, error code, or troubleshooting path added: update `Operations.md`.
- Major user-facing feature added: update this index and the root `README.md`.

