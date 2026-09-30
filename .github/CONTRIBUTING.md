# Contributing

Bug reports and focused pull requests are welcome. For larger changes, open an issue first.

## Build and test

Requires the .NET 9 SDK (version pinned in `global.json`).

```powershell
dotnet build GammaHotkey.sln -c Release
dotnet test GammaHotkey.sln -c Release
dotnet format GammaHotkey.sln --verify-no-changes
```

CI runs the same checks on every push to `main`.

## Guidelines

- Keep each pull request to one logical change and add tests for new logic.
- Use [Conventional Commits](https://www.conventionalcommits.org/) messages, for example `fix(gamma): ...`.
- Gamma safety comes first: never report a display as applied without a successful read-back, never overwrite ramps changed by other software, and restore only ramps the app still owns.
- The app must not change the `GdiICMGammaRange` registry value.
