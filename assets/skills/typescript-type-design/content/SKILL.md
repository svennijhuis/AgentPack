---
name: typescript-type-design
description: Design reusable TypeScript types — generics with constraints, utility types, branded types, and inference-first APIs.
---

# TypeScript Type Design

Use when designing shared types or a typed library API.

- Constrain generics (`<T extends ...>`) and let call sites infer `T` — avoid forcing callers to pass type arguments explicitly.
- Compose with built-in utility types (`Pick`, `Omit`, `Partial`, `Record`, `ReturnType`) before hand-rolling equivalents.
- Use branded/opaque types (`type UserId = string & { readonly __brand: unique symbol }`) to stop look-alike primitives being mixed up.
- Prefer `type` for unions, functions, and mapped types; `interface` for extensible object contracts.
- Keep conditional and mapped-type cleverness shallow. If the type is unreadable, the API is too clever — simplify it.
- Export the types your callers need and no more; don't leak internal helper types.
