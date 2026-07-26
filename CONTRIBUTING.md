# Contributing

Thanks for helping improve Quota Tray.

## Before opening an issue

- Search existing issues.
- Confirm the latest release still reproduces the problem.
- Compare provider values with Claude Code `/usage` or the Codex usage/status
  surface.
- Remove tokens, account identifiers, email addresses, and full credential
  paths from screenshots and logs.

## Development

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK
- Claude Code and/or Codex for live integration checks

Run the standard checks:

```powershell
dotnet restore .\QuotaTray.sln
dotnet format .\QuotaTray.sln --verify-no-changes --no-restore
dotnet build .\QuotaTray.sln -c Release --no-restore
dotnet test .\QuotaTray.sln -c Release --no-build
```

The live probe is optional because it requires local provider sign-ins:

```powershell
dotnet run --project .\tools\QuotaTray.Probe\QuotaTray.Probe.csproj -c Release
```

## Pull requests

- Keep changes focused.
- Add or update tests for parsing, calculations, settings, or error behavior.
- Update the README and integration guide when user-visible behavior changes.
- Do not include raw provider responses unless every credential and account
  identifier has been replaced with clearly synthetic data.
- Explain what changed, why it changed, and how it was verified.

By contributing, you agree that your contribution is licensed under the MIT
License.
