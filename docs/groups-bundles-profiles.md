# Groups & Profiles

Two grouping concepts, with one job each. (Bundles existed in 0.1.0 and were removed — they duplicated profiles. See [breaking-changes.md](breaking-changes.md).)

## Groups — labels for discovery

Groups are metadata. They answer "what exists for my area?" and power filtering:

```bash
agentpack list -g backend
agentpack add -g security
```

```yaml
# catalog.yaml
groups:
  - id: backend
    name: Backend
  - id: api
    name: API
    status: deprecated       # deprecating a group requires both fields:
    replacedBy: backend
    removeAfter: "2026-12-31"
```

Groups have **no install semantics** beyond filtering. An asset lists the groups it belongs to in its own manifest.

## Profiles — what a team installs

A profile is the installable unit: "backend team gets these assets on these providers."

```yaml
# catalog.yaml
profiles:
  - id: backend
    name: Backend Team
    providers: [codex, claude]   # optional; omitted = detect from the repo
    groups: [backend]            # include every asset labeled backend...
    assets: [grill-me]           # ...plus these specific ones
```

```bash
agentpack profile plan backend    # see what it would do
agentpack profile apply backend   # install it
```

Onboarding a new team member is one command. Profile changes are catalog PRs, so a team's toolkit is versioned and reviewed like everything else.

## Which one do I want?

| Need | Use |
|---|---|
| Label assets by area/domain | group |
| Filter list/add | group |
| Standard toolkit per team, one command | profile |
| "Everyone on team X gets hook Y" | profile (assets or a group the hook is in) |
