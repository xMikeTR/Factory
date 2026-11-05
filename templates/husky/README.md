# Husky hooks template

For a **product repo** (the .NET API, the React UI), not for this one. Copy these files into the product repo's
`.husky/` folder (keep the `_/` folder and the executable bits) and run `npx husky` so `core.hooksPath` points there.

| Hook | What it does |
|---|---|
| `pre-commit` | `lint-staged` on the staged files |
| `post-checkout`, `post-merge`, `post-rewrite` (rebase) | `npm i` when `package.json` or `package-lock.json` changed between the two refs |

## The lockfile guard

If `npm i` rewrites `package-lock.json` when `package.json` did **not** change, that is drift. The hook puts the
lockfile back and exits non-zero with a message, instead of leaving an uncommitted change behind.

## One source of truth

Declare the same scripts everywhere and nothing else: `npm run verify` and `npm run parity`. The editor hook, the git
hooks, CI and the factory (`verify:` in `.factory/policy.yaml`) all run these, so what passes in one passes in all.

`--no-verify` is not available to agents (refused by the factory); a human skipping hooks is a human decision.

## Tests

`tests/Factory.Integration.Tests/Templates/HuskyHooksTests.cs` runs these hooks against a temp git repo with a stub `npm`.
