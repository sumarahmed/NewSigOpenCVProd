# Signature Verification Documentation

This folder contains the maintained documentation set for the signature verification solution.

- [Technical Documentation](Technical.md): architecture, APIs, data contracts, matching pipeline, and extension points.
- [Business Documentation](Business.md): business capabilities, production workflows, reference enrollment, case management, reports, review queue, feedback tuning, and customer value.
- [Administration Guide](Administration.md): configuration, folder setup, thresholds, role/reference mapping, Ghostscript, database storage, API keys, and environment settings.
- [Operations Runbook](Operations.md): build/run commands, operations CLI, production API, health checks, monitoring, troubleshooting, cleanup, and release checklist.
- [SQL Database Reference](SQL-Database.md): connection strings, database name, migrations, tables, views, and verification queries.
- [Production Readiness Review](Production-Readiness-Review.md): current implementation health, verified checks, resolved items, and remaining production controls.

## Documentation Maintenance

Update these docs whenever behavior, configuration, API contracts, reporting output, or operational steps change.

- API or JSON contract changed: update `Technical.md`, `Administration.md`, and sample payloads.
- Business workflow/report changed: update `Business.md` and `Operations.md` if run steps changed.
- Threshold, role, reference, PDF, or folder behavior changed: update `Administration.md`.
- New command, report, error code, or troubleshooting path added: update `Operations.md`.
- Major user-facing feature added: update this index and the root `README.md`.
- Production workflow changed: update `Business.md`, `Operations.md`, `Administration.md`, and `Technical.md`.
- SQL schema, migrations, or database operations changed: update `SQL-Database.md`.
- Storage mode, DB-native API, or install-time storage behavior changed: update `README.md`, `Administration.md`, `Operations.md`, `Technical.md`, `Business.md`, and `SQL-Database.md`.
- Production readiness status changed: update `Production-Readiness-Review.md` and cross-check the release checklist in `Operations.md`.
