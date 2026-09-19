# Project instructions

- Use Conventional Commits for every new commit and squash-merge title: `type(scope): description` (scope optional).
- Use `fix` for patch changes, `feat` for minor changes, and `!` or a `BREAKING CHANGE:` footer for major changes. Use `docs`, `test`, `ci`, `refactor`, and `chore` for other work.
- Do not rewrite historical commits merely to rename them.
- Release Please manages `CHANGELOG.md`, `version.txt`, `.release-please-manifest.json`, and the project version through a release PR. Do not manually bump versions or create release tags during normal development.
- Keep release build/test checks passing before publishing downloads. Preserve personal profiles and running games when updating the local installation.
