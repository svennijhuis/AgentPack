---
name: csharp-aspnetcore
description: Build ASP.NET Core web APIs — minimal APIs or controllers, dependency injection, EF Core, and xUnit integration tests.
---

# C# ASP.NET Core

Use when building or reviewing an ASP.NET Core service.

- Minimal APIs for small, focused services; controllers when you need conventions, filters, or many related routes. Group related endpoints with `MapGroup`.
- Register dependencies with the right lifetime: singleton (stateless/shared), scoped (per request — e.g. `DbContext`), transient. Never inject a scoped service into a singleton.
- Validate input (DataAnnotations or a validator) and return typed `Results` / `ProblemDetails` — don't surface raw exceptions.
- EF Core: one `DbContext` per request (scoped), `async` queries, project to DTOs with `Select`, `AsNoTracking` for read-only queries, and keep migrations in source control.
- Bind config with `IOptions<T>`; read secrets from user-secrets / env / Key Vault, never from `appsettings.json`.
- Test with `WebApplicationFactory<T>` + xUnit for real end-to-end coverage, and unit-test services in isolation.
