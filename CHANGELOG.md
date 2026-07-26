# Changelog

All notable changes to Quota Tray will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.3] - 2026-07-26

### Changed

- Background polling now runs every 15 minutes with up to 10% jitter.
- Opening the flyout refreshes provider data when either snapshot is at least
  two minutes old.

## [0.1.2] - 2026-07-26

### Fixed

- Manual refresh now bypasses Claude's normal five-minute cache.
- Cached Claude data is explicitly marked stale when Anthropic rate limits
  quota checks, including the retry time.

## [0.1.1] - 2026-07-26

### Added

- Persisted always-on-top option in the flyout and tray menu.

### Changed

- The flyout now resizes to its content instead of scrolling quota rows inside
  the window.

## [0.1.0] - 2026-07-26

### Added

- Claude and Codex subscription quota display.
- Five-minute background refresh and manual refresh.
- Stale-data retention and provider-specific failure states.
- Optional persisted weekly pacing and daily-budget estimates.
- Custom tray flyout, original icon, multi-monitor placement, and
  single-instance activation.
- Windows CI, automated release packaging, tests, and public documentation.

[Unreleased]: https://github.com/shiftynick/quota-tray/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/shiftynick/quota-tray/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/shiftynick/quota-tray/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/shiftynick/quota-tray/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/shiftynick/quota-tray/releases/tag/v0.1.0
