# Catalog Repository Setup

The catalog is a software supply-chain boundary. `agentpack submit` always creates a proposal branch, but the repository must enforce that branches cannot bypass review.

## Required protection for `main`

Create a GitHub ruleset or branch-protection rule for the default branch with:

- require a pull request before merging;
- require at least one approval;
- require review from CODEOWNERS;
- dismiss stale approvals when new commits are pushed;
- require conversation resolution;
- require the catalog validation and test status checks;
- block force pushes and branch deletion;
- apply the rule to administrators and maintainers too.

Repository settings are the enforcement layer. The committed `.github/CODEOWNERS` file only routes reviews; it cannot enable branch protection by itself.

## Recommended repository split

Use two repositories with separate release lifecycles:

```text
AgentPack
  CLI source, tests, provider adapters, NuGet releases

AgentPackCatalog
  catalog.yaml, catalog.lock.yaml, assets/, catalog CI
```

Migration sequence:

1. Create `AgentPackCatalog` with `main` protected as above.
2. Move `catalog.yaml`, `catalog.lock.yaml`, `assets/`, catalog validation CI, and CODEOWNERS.
3. Change `AgentPackDefaults.OfficialCatalogUrl` to the new repository URL.
4. Publish one AgentPack NuGet release containing that URL change.
5. From then on, catalog PRs ship independently and require no NuGet release.

Steps 3 and 4 set the default for everyone. To point at a different catalog without a
release, set `AGENTPACK_DEFAULT_CATALOG_URL`, or `AGENTPACK_DISABLE_DEFAULT_CATALOG=1`
to remove the built-in catalog entirely. Individual users select a catalog with
`agentpack catalog use <git-url>`.

## Catalog CI

Every pull request must run:

```bash
agentpack catalog validate
agentpack catalog lock --check
agentpack catalog verify-external
```

Only merge when all checks and required reviews pass.
