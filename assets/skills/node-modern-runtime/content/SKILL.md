---
name: node-modern-runtime
description: Write modern Node.js — ESM modules, async/await and streams, the built-in test runner, disciplined error handling, and env-based config.
---

# Node Modern Runtime

Use when writing Node.js scripts or services.

- Use ESM (`import`/`export`, `"type": "module"`). Prefer Web-standard APIs already in Node: `fetch`, `URL`, `AbortController`, `structuredClone`.
- Prefer `async`/`await`; never leave a dangling promise. Handle rejections and add a top-level `process.on('unhandledRejection')` guard.
- For large data use streams or async iterators — don't buffer whole payloads in memory.
- Tests: the built-in `node:test` + `node:assert` runner needs no dependency (`node --test`).
- Read config from the environment; validate it at startup and fail fast. Never hardcode secrets.
- Wrap and rethrow errors with context via the `cause` option; reserve `process.exit` for CLIs, not libraries.
