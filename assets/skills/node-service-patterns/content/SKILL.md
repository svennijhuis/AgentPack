---
name: node-service-patterns
description: Structure Node HTTP services (Express/Fastify) — clear layering, input validation, centralized errors, and graceful shutdown.
---

# Node Service Patterns

Use when building or reviewing a Node HTTP service.

- Layer routes -> handlers -> services -> data access. Keep framework types out of the service layer so it stays unit-testable.
- Validate and parse input at the edge with a schema (zod/valibot); treat everything past the boundary as typed and trusted.
- One centralized error handler: handlers throw typed errors, the middleware maps them to status codes. Never leak stack traces to clients.
- Prefer Fastify (or Express 5) so async errors propagate correctly instead of hanging the request.
- Graceful shutdown: on SIGTERM/SIGINT stop accepting connections, drain in-flight requests, close DB pools, then exit.
- Expose `/health` (liveness) and `/ready` (readiness). Log structured JSON with a per-request correlation id.
