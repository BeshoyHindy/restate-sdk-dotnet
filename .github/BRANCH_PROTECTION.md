# Branch Protection

Policy enforced on `main`. This documents what is actually configured, so keep it in
sync when settings change.

## Policy

- All changes land through pull requests; direct pushes to `main` are blocked by the
  required checks below.
- Required status checks (must pass, branch must be up to date with `main`):
  - `Build & Test`
  - `Format Check`
  - `Integration Test`
  - `analyze` (CodeQL)
- Linear history required — squash merge is the only enabled merge method, so PR titles
  become commit subjects (use conventional-commit titles: `feat:`, `fix:`, `docs:`, ...).
- Force pushes and branch deletion on `main` are blocked.
- Rules apply to administrators too.
- No required review count while the project has a single maintainer. Revisit
  (set to 1) when a second maintainer joins.
- Merged head branches are deleted automatically (repository setting).

## Applying it

```bash
gh api repos/BeshoyHindy/restate-sdk-dotnet/branches/main/protection -X PUT --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["Build & Test", "Format Check", "Integration Test", "analyze"]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "required_linear_history": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true
}
JSON
```
