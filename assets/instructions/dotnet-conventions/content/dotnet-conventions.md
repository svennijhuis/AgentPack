# .NET repository conventions

- Read `global.json`, solution files, project files, central package files, and CI configuration before selecting an SDK or package version.
- Preserve repository-specific formatting and nullable, analyzer, and warning policies.
- Make upgrades in reviewable increments. Do not combine unrelated refactors with a framework migration.
- Restore, build, and test with the SDK selected by the repository. Report any validation that could not run.
- Treat generated files and lock files according to the repository's existing policy.
