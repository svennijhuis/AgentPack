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

### Hierarchical groups (`language/topic`)

Group labels are hierarchical. A top-level id (`csharp`) may have slash-delimited subgroups (`csharp/review`, `csharp/testing`). Filtering matches a label exactly **or** as a path prefix at a `/` boundary:

```bash
agentpack add -g csharp          # csharp AND every csharp/* subgroup
agentpack add -g csharp/review   # only the csharp/review subgroup
agentpack groups                 # the tree, with an asset count per label
```

Only the **top-level** id is declared in `catalog.yaml`; subgroups are implicit — any `csharp/<topic>` an asset tags itself with is valid as long as `csharp` is a declared group. This keeps `catalog.yaml` small while letting a busy language be sliced by topic. An asset that applies to several languages simply lists the subgroup under each, e.g. a cross-stack review skill uses `[react/review, node/review, typescript/review, csharp/review]`.

| Label on an asset | Selected by |
|---|---|
| `csharp` | `-g csharp` |
| `csharp/review` | `-g csharp` and `-g csharp/review` |
| `csharp/api` | `-g csharp` and `-g csharp/api` |

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

Profile installs are recorded as ownership edges such as `profile:backend`, not as direct requests. If a selected asset is an agent, its automatic dependencies also receive their normal `agent:<id>` edges. Shared dependencies remain installed while any direct request, agent, or profile still owns them. Removing an agent removes only its agent edge; unreferenced automatic dependencies become visible in `agentpack prune` and are not deleted until that separate, previewable operation runs.

## Which one do I want?

| Need | Use |
|---|---|
| Label assets by area/domain | group |
| Filter list/add | group |
| Standard toolkit per team, one command | profile |
| "Everyone on team X gets hook Y" | profile (assets or a group the hook is in) |
