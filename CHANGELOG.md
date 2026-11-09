# Changelog

Release Please maintains new entries from Conventional Commits. Versions follow Semantic Versioning. Historical releases below were recorded before automation.

## [1.4.0](https://github.com/yajra/ROInstanceLauncher/compare/v1.3.0...v1.4.0) (2026-11-09)


### Features

* **instances:** add profile deletion and reliable close controls ([234ac78](https://github.com/yajra/ROInstanceLauncher/commit/234ac786559ec1fffde4640f58d36229385b7c28))

## [1.3.0](https://github.com/yajra/ROInstanceLauncher/compare/v1.2.1...v1.3.0) (2026-09-19)


### Features

* **updates:** check and install verified launcher releases ([66a0ae8](https://github.com/yajra/ROInstanceLauncher/commit/66a0ae8fe25751cbcc0ea221bb14b045c42b4648))

## [1.2.1](https://github.com/yajra/ROInstanceLauncher/compare/v1.2.0...v1.2.1) (2026-09-19)


### Bug Fixes

* **branding:** identify the launcher as Ragnarok M Eternal Love ([b6f2583](https://github.com/yajra/ROInstanceLauncher/commit/b6f2583c7e0703e8de9748e96ed40b1a4f53f9b9))
* **branding:** set the Windows executable display name ([03232b4](https://github.com/yajra/ROInstanceLauncher/commit/03232b4964ccc367071e482afbf88073e3a9cc6a))

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
