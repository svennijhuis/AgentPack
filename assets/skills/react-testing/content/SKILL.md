---
name: react-testing
description: Test React components with Testing Library — query by accessible role/label and assert user-visible behavior, not implementation details.
---

# React Testing

Use when writing or reviewing React component tests.

- Query priority: `getByRole` > `getByLabelText` > `getByText` > `getByTestId` (last resort). Roles mirror what users and assistive tech perceive.
- Assert behavior a user can observe, not internal state or component names. Avoid whole-tree snapshot tests — they fail on noise and assert nothing meaningful.
- Drive interactions with `userEvent`, not raw `fireEvent`, so events match real browser sequences.
- Use `findBy*` / `waitFor` for async UI; never an arbitrary `setTimeout`.
- Mock at the network boundary (e.g. MSW), not by stubbing child components.
- One behavior per test, and the test name states that behavior.
