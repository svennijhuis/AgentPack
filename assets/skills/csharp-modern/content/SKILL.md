---
name: csharp-modern
description: Write idiomatic modern C# — records, pattern matching, nullable reference types, async/await, and expressive LINQ.
---

# C# Modern

Use when writing or reviewing modern C# (.NET). This is the actionable how-to companion to the always-on `dotnet-conventions` rules — apply the patterns here, don't restate the conventions.

- Enable nullable reference types and let the compiler prove null-safety. Avoid the null-forgiving `!` except at a boundary you have proven safe.
- Use `record` / `record struct` for immutable data and `with` for non-destructive updates. Reserve classes for identity and behavior.
- Prefer pattern matching — `switch` expressions and property / `and` / `or` / `not` patterns — over `if`/cast chains.
- `async`/`await` all the way: return `Task`/`ValueTask`, never `async void` (except event handlers), thread a `CancellationToken`, and never `.Result` / `.Wait()`.
- Use file-scoped namespaces, `var` when the type is obvious, target-typed `new`, and collection expressions (`[..]`).
- Use LINQ for transformations, but materialize once (`ToList`) when a sequence is enumerated repeatedly, and watch for hidden N+1 in EF Core queries.
