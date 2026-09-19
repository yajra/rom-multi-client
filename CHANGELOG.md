# Changelog

Release Please maintains new entries from Conventional Commits. Versions follow Semantic Versioning. Historical releases below were recorded before automation.

## [1.2.0](https://github.com/yajra/ROInstanceLauncher/compare/v1.1.0...v1.2.0) (2026-09-19)


### Features

* **launcher:** launch selected instances with check all ([03e7820](https://github.com/yajra/ROInstanceLauncher/commit/03e78203f235d13ddb8865c2eeca218a0f2191fa))

## [1.1.0](https://github.com/yajra/ROInstanceLauncher/compare/v1.0.2...v1.1.0) (2026-09-19)


### Features

* **release:** automate changelog and semantic version releases ([098430a](https://github.com/yajra/ROInstanceLauncher/commit/098430a92cc19f9e15a5aed9db5ca509b81dce4b))

## [1.0.2](https://github.com/yajra/ROInstanceLauncher/releases/tag/v1.0.2) (2026-09-19)

### Changes

- Remove incompatible Sandboxie integration and its setup controls.
- Migrate existing profiles to normal game launching while preserving labels and notes.
- Clarify that profile labels do not isolate remembered logins.

## [1.0.1](https://github.com/yajra/ROInstanceLauncher/releases/tag/v1.0.1) (2026-09-19)

### Changes

- Automatically create and verify persistent sandboxes before launch.
- Document the game's THEMIS rejection of sandboxed startup.

## [1.0.0](https://github.com/yajra/ROInstanceLauncher/releases/tag/v1.0.0) (2026-09-19)

### Features

- Manage game instances sharing one data installation.
- Save profile names, account labels, and server notes; apply distinct window titles.
- Monitor CPU and memory and warn when memory is low.
- Publish a tested standalone Windows executable through GitHub Actions.
