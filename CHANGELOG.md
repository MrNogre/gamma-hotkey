# Changelog

## [0.3.0] - 2026-10-13

### Added
- Panic restore: `Ctrl+Alt+Shift+Backspace` or the tray menu's "Restore now" puts back the original gamma on every display the app still controls.
- Displays with HDR turned on are skipped (`Skipped: HDR active`) and get no profile; a profile still applied when HDR is turned on is restored first.
- Live preview: dragging the gamma slider of the active profile updates the displays as you drag; the value is saved when you let go.
- Per-profile display targeting: switches on the profile card choose which displays the profile applies to; displays switched off are restored to their original gamma.
- A "None (baseline)" entry at the top of the profile list restores the original gamma and can have its own hotkey; the app starts on it, and panic restore, profile delete and display changes return to it.
- Per-profile brightness (−0.50 to +0.50) and contrast (0.50 to 1.50) next to gamma, with live preview; a combination beyond the Windows ramp limit shows `Failed: exceeds Windows ramp limit`.
- The tray menu lists "None (baseline)" and every profile; choosing one applies it.

### Changed
- The Apply profile button is gone: choosing a profile in the list applies it, and a value typed into a box is applied on Enter or when the box loses focus.
- Choosing the active profile again reapplies it, for example after another program changed the ramp.
- Thin scroll bars that follow the theme, a focus ring on the slider thumb, and no display switches while "None (baseline)" is selected.
- The footer no longer shows the "Close hides to tray" hint.

### Fixed
- Switching profiles with a hotkey saves the settings once instead of twice.
- A click on the slider track steps the value by 0.1 instead of jumping by 1.00.
- The main window fits the work area on small screens; the pages scroll instead of the Exit button falling off screen.
- A display whose hardware id cannot be read is no longer dropped from a profile once display switches are changed.
- A renamed profile shows its new name in the profile list at once.

## [0.2.0] - 2026-09-30

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
