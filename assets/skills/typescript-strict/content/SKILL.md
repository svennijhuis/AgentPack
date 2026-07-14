---
name: typescript-strict
description: Write type-safe TypeScript under strict mode — eliminate any, model states with discriminated unions, and narrow deliberately.
---

# TypeScript Strict

Use when writing or tightening TypeScript.

- Turn on `strict` (and `noUncheckedIndexedAccess`). Treat `any` as a bug — reach for `unknown` plus narrowing at boundaries.
- Model mutually exclusive states as discriminated unions with a literal tag, not bags of optional fields. Make illegal states unrepresentable.
- Prefer inference. Annotate function inputs and public boundaries; let return types infer unless you want a fixed contract.
- Use `satisfies` to check a value against a type without widening the value.
- Narrow with type guards, `in`, and discriminant checks; avoid `as` casts (they silence the checker). Never `as any`.
- A type error is the spec failing — fix the type or the code, don't reach for `@ts-ignore`.
