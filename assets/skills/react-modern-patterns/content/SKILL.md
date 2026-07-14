---
name: react-modern-patterns
description: Write idiomatic modern React — choose Server vs Client Components, follow the Rules of Hooks, and prefer composition over useEffect.
---

# React Modern Patterns

Use when building or reviewing React components.

- Default to Server Components. Add `"use client"` only when you need state, effects, refs, or browser APIs — and keep the client boundary as low in the tree as possible.
- Rules of Hooks: call hooks unconditionally at the top level, never in loops, conditions, or nested functions. Custom hooks start with `use`.
- Reach for `useEffect` only to synchronize with an external system. NOT for deriving state (compute during render), responding to a user action (do it in the handler), or resetting state when a prop changes (use `key`).
- Prefer composition and passing `children` over prop-drilling and boolean configuration flags.
- Lift state only as far as it must go; colocate otherwise. Reach for `useMemo`/`useCallback`/`memo` only after measuring a real performance problem.
- Keys must be stable and unique — never the array index for a list that can reorder or grow.
