# Gamma Hotkey

[![Release](https://img.shields.io/github/v/release/MrNogre/gamma-hotkey)](https://github.com/MrNogre/gamma-hotkey/releases/latest)
[![CI](https://github.com/MrNogre/gamma-hotkey/actions/workflows/ci.yml/badge.svg)](https://github.com/MrNogre/gamma-hotkey/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/MrNogre/gamma-hotkey)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue)

A small Windows 10/11 app for adjusting display gamma. Create profiles, assign global hotkeys, and switch between them from the window or the tray icon.

It is most useful in games: when a scene is too dark to see, one key press raises the gamma, and another brings it back.

![Gamma Hotkey main window](screenshot.png)

## Features

- Gamma profiles from 0.50 to 4.00, applied to all connected displays.
- Global hotkeys per profile.
- Per-display status, confirmed by reading the ramp back after every write.
- Detects when another program changes the ramp and does not overwrite it.
- Restores the original ramp on exit, on unhandled errors and at session end.
- Tray operation, optional start with Windows, light/dark/system theme.
- Portable: a single executable, no installer or separate .NET runtime.

## Download and run

1. Download `gamma-hotkey-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/MrNogre/gamma-hotkey/releases/latest).
2. Extract it to a permanent folder and run `GammaHotkey.exe`. Keep the executable in place if you enable **Start with Windows**.
3. The executable is not code-signed, so SmartScreen may warn on first start: choose **More info → Run anyway**.

To verify the download, compare it with `SHA256SUMS.txt` from the release, or check its build provenance with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify gamma-hotkey-vX.Y.Z-win-x64.zip --repo MrNogre/gamma-hotkey
```

## Usage

Create a profile, set its gamma, and select **Apply profile**. Choosing a profile in the list only selects it for editing; **Apply profile** or the profile's hotkey applies it. Values above 1.00 brighten dark and mid tones, values below 1.00 darken them.

Hotkeys: click the shortcut box and press the combination. F1–F12 work alone or with Ctrl, Alt and Shift; letters and top-row digits need Ctrl or Alt. The numpad and the Windows key are not supported. Delete clears the shortcut, Escape keeps the current one. The app warns when a Ctrl+Alt shortcut would block an AltGr character on your keyboard layout.

Closing the window keeps the app in the tray. Choose **Exit** to restore the original display ramp.

## Safety and recovery

**Do not use with HDR enabled.** Gamma ramps are meant for SDR output; with HDR the result is undefined. Extreme values can make the screen hard to read, so test cautiously and keep Windows Display Settings at hand.

- On **Exit**, on an unhandled error and at sign-out or shutdown, the app restores the original ramp of every display it still controls.
- When a display is connected, removed or the PC resumes from sleep, the app restores the original ramps and captures them again; apply the profile again afterwards.
- If the screen stays unusable, restart Windows to reset the gamma ramp; signing out usually works too.

## How it works

The app reads each display's current gamma ramp through GDI (`GetDeviceGammaRamp`) and keeps it as the baseline. A profile maps the baseline through a gamma curve, checks that the result stays monotonic and within safe limits, and writes it with `SetDeviceGammaRamp`. A display is shown as applied only after the ramp read back from the driver matches the one written, within a small tolerance. Every few seconds the app checks that the ramp is still its own; if another program has changed it, the display is marked as overridden and the app leaves it alone until you apply a profile again. On restore, only ramps the app still owns are reset to the baseline.

## Troubleshooting

- **"Failed: gamma write rejected"** — the driver or Windows refused the ramp. Windows limits how far a ramp may deviate from linear; an advanced, system-wide registry value (`GdiICMGammaRange` under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM`) can raise that limit. Changing it needs administrator rights and is at your own risk; this app never changes it.
- **"Overridden"** — another program changed the ramp: GPU control panel color settings (NVIDIA, AMD, Intel), Night Light, f.lux, calibration loaders or some games. Turn that program off and apply the profile again.
- **"Hotkey unavailable"** — another program already uses that shortcut. Choose a different one.
- **Errors** — the log files in `%LOCALAPPDATA%\GammaHotkey\logs` record errors and display status changes.

## Settings and uninstall

Settings live in `%LOCALAPPDATA%\GammaHotkey\settings.json`. An unreadable file is kept as `settings.invalid-<timestamp>.json`, and the previous version of each save as `settings.json.bak`.

To uninstall, turn off **Start with Windows** (or remove the `GammaHotkey` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`), exit the app, then delete the executable and `%LOCALAPPDATA%\GammaHotkey`.

## Build from source

Requires the .NET 9 SDK.

```powershell
dotnet build GammaHotkey.sln -c Release
dotnet test GammaHotkey.sln -c Release
dotnet publish GammaHotkey/GammaHotkey.csproj -c Release -p:PublishProfile=win-x64
```

The single-file executable is written to `GammaHotkey/bin/publish/win-x64/`. See [CONTRIBUTING](.github/CONTRIBUTING.md) before opening a pull request, and [SECURITY](.github/SECURITY.md) for reporting vulnerabilities. Changes are listed in the [CHANGELOG](CHANGELOG.md).

## License

MIT, see [LICENSE](LICENSE). The bundled Silkscreen font is licensed under the [SIL Open Font License](GammaHotkey/Fonts/OFL.txt); see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).
