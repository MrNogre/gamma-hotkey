# Changelog

## [0.2.0] - Unreleased

### Added
- Hotkeys accept any combination of Ctrl, Alt and Shift; settings move to schema v2 and v1 files are migrated.
- Warning when a Ctrl+Alt shortcut would block an AltGr character on the current keyboard layout.
- Gamma is restored after unhandled errors and at session end; a daily log file records errors and display status.
- An unreadable settings file is kept as `settings.invalid-<timestamp>.json`, and each save keeps the previous file as `settings.json.bak`.
- Per-monitor DPI awareness through an application manifest.
- Release builds come with SHA-256 checksums and build provenance attestation.

### Changed
- Profile gamma is capped at 4.00, because drivers reject steeper ramps; saved higher values are clamped.

### Fixed
- Hotkeys are saved readably in the settings file (`Ctrl+Alt+G` instead of `Ctrl\u002BAlt\u002BG`).
- Keyboard layout character check uses a correct UTF-16 buffer.
- Validation messages show in the footer.

## [0.1.1] - 2026-09-29

### Changed
- New profiles default to neutral gamma 1.00; existing saved profiles remain unchanged.

## [0.1.0] - 2026-09-29

Initial public release: portable Windows x64 build, no installer or separate .NET runtime required.
SDR displays only; do not use with HDR enabled.
