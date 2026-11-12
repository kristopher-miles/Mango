# Mango requirements

Mango combines independently hosted inference services into one private network. Any enrolled peer can submit work, eligible peers can serve it, and the user can manage the shared model catalog from any node. The network should stay inexpensive while idle and yield compute promptly to the person using a contributing machine.

This is requirements revision 2, incorporating the questionnaire returned on November 11, 2026. It records the requested product, necessary implications, research findings, and choices that still need an answer. It does not select an implementation stack, prescribe a development sequence, or authorize implementation.

## Reading order

Start with [Recorded decisions](09-recorded-decisions.md) and the revised subject documents. The [original questionnaire](mango-questionnaire.html) remains frozen to its original baseline so the submitted JSON can still be imported; it is a historical artifact, not the revised specification.

| Document | Covers |
| --- | --- |
| [Product scope](00-product-scope.md) | Goals, terminology, boundaries, performance targets, and settled interpretations |
| [Network and security](01-network-and-security.md) | Tailscale, joining, identities, credentials, replication, and public access |
| [Providers and models](02-providers-and-models.md) | Adapters, protocols, discovery, aliases, context limits, and benchmarks |
| [Resources and scheduling](03-resources-and-scheduling.md) | Activity response, memory safety, queues, priorities, routing, and affinity |
| [Interface and operations](04-interface-and-operations.md) | GUI, settings ownership, logs, aggregation, retention, and updates |
| [Future best model routing](05-future-best-routing.md) | The deferred decision-model feature and information it will need |
| [Acceptance and coverage](06-acceptance-and-coverage.md) | Observable scenarios and coverage of the original specification |
| [Research and recommendations](07-research-and-recommendations.md) | Primary sources, verified constraints, and resulting recommendations |
| [Original questionnaire](08-open-decisions.md) | Historical questions and proposals |
| [Recorded decisions](09-recorded-decisions.md) | All submitted answers, accepted policy, and remaining details |

## Requirement status

Each numbered requirement has one of these statuses. A proposal does not become a commitment merely because another document describes its consequences.

| Status | Meaning |
| --- | --- |
| **C — Confirmed** | Directly requested behavior, including accepted questionnaire recommendations. |
| **I — Inferred** | Necessary consequence or clear interpretation of the request. Listed so it can be corrected. |
| **P — Proposed** | A recommended addition, default, or resolution. Its decision number points into the question document. |
| **F — Future** | Explicitly deferred behavior. Record its requirements now; do not implement it yet. |

Identifiers such as `NET-01` and `QUEUE-04` are stable references for later decisions and acceptance checks. Related requirements are grouped by subject rather than development stage. When decisions are answered, update the affected requirements and acceptance scenarios before writing the implementation plan.

## Current decision status

All 29 questions received responses. The network uses trusted single-owner peers, lightweight eventual reconciliation, Windows-first delivery, the scarce-placement queue exception, and the updated logging and retry policies. Ordinary password recovery preserves issued keys; full reset is a separate operation. The [eight-phase implementation plan](../plan/00-shared-foundation.md) now records the implementation sequence and shared design.

Research retains its original date and recommendations as historical context; the owner’s recorded decisions and revised subject requirements take precedence. Performance figures are targets to qualify, not achieved results. Planning is complete; application implementation has not begun.
