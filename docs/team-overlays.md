# Team Overlays

Different teams can have different conventions without forking the company catalog.

Add repo-local overlays in:

```text
.agentpack/catalog.yaml
.agentpack/assets/<kind>/<id>/agentpack.yaml
.agentpack/assets/<kind>/<id>/content/...
```

The loader applies:

1. central organization catalog
2. repo-local `.agentpack/catalog.yaml` + `.agentpack/assets/`

Overlay IDs replace matching central IDs. New IDs are added.

Good uses:

- team-specific prompts, skills, hooks
- infra conventions
- local profiles

Example:

```bash
agentpack new prompts platform-infra-review --group platform
# then move the generated assets/ folder under .agentpack/assets/
```

```yaml
# .agentpack/assets/prompts/platform-infra-review/agentpack.yaml
name: Platform Infra Review
version: 1.0.0
description: Review checklist for platform infra changes.
groups: [platform, infra]
# providers omitted = available for all providers
status: experimental
channel: internal
```

Id and kind are inferred from the folder path; content lives in `content/`, exactly like the central catalog.

Avoid:

- redefining central security hooks (overlay overrides central ids — reviewers should reject this)
- bypassing blocked assets
- copying the full central catalog into a repo
