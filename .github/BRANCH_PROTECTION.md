# Branch Protection Setup

Recommended settings for the `main` branch.

## Via GitHub UI

Settings > Branches > Add branch protection rule:

- **Branch name pattern:** `main`
- **Require a pull request before merging**
  - Required approving reviews: 1
- **Require status checks to pass before merging**
  - Required checks: `Build & Test`, `Format Check`
- **Require branches to be up to date before merging**
- **Do not allow force pushes**
- **Do not allow deletions**

## Via GitHub CLI

```bash
gh api repos/{owner}/{repo}/branches/main/protection -X PUT \
  -F "required_status_checks[strict]=true" \
  -F "required_status_checks[contexts][]=Build & Test" \
  -F "required_status_checks[contexts][]=Format Check" \
  -F "required_pull_request_reviews[required_approving_review_count]=1" \
  -F "enforce_admins=true" \
  -F "restrictions=null" \
  -F "allow_force_pushes=false" \
  -F "allow_deletions=false"
```
