# Mango

Mango is a private inference network proposal that routes requests among existing model servers over Tailscale. Windows is the first peer platform; clients may use any operating system.

Start with the [eight-phase implementation plan](docs/plan/00-shared-foundation.md). Each phase has its own document; the foundation contains the shared runtime and ownership contracts. The [requirements index](docs/requirements/README.md) and [recorded decisions](docs/requirements/09-recorded-decisions.md) preserve the product baseline and all 29 questionnaire responses.

The [original HTML questionnaire](docs/requirements/mango-questionnaire.html) and [submitted JSON](docs/requirements/decisions/2026-11-11-answers.json) are retained against their original baseline for reference and import compatibility.

Status: requirements and implementation plan recorded. Ordinary forgotten-password recovery preserves inference keys; full reset remains separate. No Mango application implementation has begun.

The repository contains requirements, planning, and the historical questionnaire tooling. Runtime state, credentials, logs, models, and generated application builds do not belong in Git. No remote repository or hosted service is required by this plan.
