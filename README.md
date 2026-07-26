# Quota Tray

![Quota Tray icon](assets/quota-tray.png)

A small, local-only Windows tray utility for viewing the remaining quota and
reset times associated with the Claude Code and Codex subscriptions already
signed in on your computer.

[![CI](https://github.com/shiftynick/quota-tray/actions/workflows/ci.yml/badge.svg)](https://github.com/shiftynick/quota-tray/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Features

- Claude and Codex quota windows in one compact tray flyout
- Remaining percentage and local reset time
- Five-minute background refresh and manual refresh
- Last-known data retained and clearly marked stale after failures
- Optional weekly pacing and estimated daily budget
- Multi-monitor-aware placement and single-instance behavior
- No telemetry, account service, or application-owned backend

## Privacy and provider access

Quota Tray runs locally:

- Claude Code's local OAuth access token is read from
  `%USERPROFILE%\.claude\.credentials.json`, or from `CLAUDE_CONFIG_DIR`, and
  sent only to Anthropic's usage endpoint. Quota Tray does not modify Claude's
  credentials or refresh token.
- Codex quota is requested through the installed `codex app-server`. Codex owns
  its credentials; Quota Tray does not read or modify them.
- Credentials and quota data are not logged or sent to the developer.

Claude's quota endpoint is not a stable public API contract and may change.
See [the integration guide](docs/quota-integration.md) for the exact requests,
parsing rules, and security boundaries.

## Install

Download `QuotaTray-win-x64.zip` from the
[latest release](https://github.com/shiftynick/quota-tray/releases/latest),
extract it, and run `QuotaTray.exe`.

Release builds are self-contained and do not require a separate .NET runtime.
The executable is currently unsigned, so Windows SmartScreen may display an
unrecognized-app warning.

## Requirements

- Windows 10 or Windows 11, x64
- Claude Code signed into a Claude subscription
- Codex installed and signed in with ChatGPT

Either provider can fail independently; the other will still be shown.

## Use

- Left-click the tray icon to show or hide the quota window.
- Right-click the tray icon to open, refresh, toggle pacing insights, or exit.
- Closing the window hides it to the notification area.
- Starting Quota Tray again activates the already-running instance.

### Optional pacing and daily budget

Enable **Pacing & daily budget** in the window or
**Show weekly pacing and daily budget** in the tray menu. The preference is
saved under `%LOCALAPPDATA%\QuotaTray`.

These figures are local estimates, not provider-reported daily quotas. For
quota windows of at least six days, Quota Tray compares current usage with
evenly paced usage and estimates:

- percentage points over or under pace;
- average usage per elapsed day; and
- average remaining percentage available per day until reset.

The feature is off by default because not everyone wants derived usage
guidance.

## Build

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
then run:

```powershell
dotnet restore .\QuotaTray.sln
dotnet build .\QuotaTray.sln -c Release --no-restore
dotnet test .\QuotaTray.sln -c Release --no-build
```

To run from source:

```powershell
.\run.ps1
```

To create the self-contained release ZIP and SHA-256 checksum:

```powershell
.\scripts\package.ps1
```

## Contributing

Bug reports and focused pull requests are welcome. Read
[CONTRIBUTING.md](CONTRIBUTING.md) before submitting a change. Please report
credential exposure or other security issues through the process in
[SECURITY.md](SECURITY.md).

## License and trademarks

Quota Tray is available under the [MIT License](LICENSE).

This project is not affiliated with, endorsed by, or sponsored by Anthropic or
OpenAI. Claude, Claude Code, OpenAI, ChatGPT, and Codex are trademarks of their
respective owners.
