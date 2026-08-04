# Security policy

Quota Tray handles local subscription OAuth credentials and should be treated
as security-sensitive software.

## Supported versions

Security updates are provided for the latest release.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository. Do not
open a public issue for suspected credential exposure, token handling flaws, or
other vulnerabilities.

Include:

- affected version;
- reproduction steps;
- expected impact; and
- a suggested mitigation, if available.

Never include a live access token, refresh token, ID token, account identifier,
email address, or unredacted credential file.

## Security boundaries

- Claude credentials are read locally and sent only to Anthropic.
- Claude credential files are never modified.
- Codex owns all Codex credential access and refresh.
- Cursor credentials are read locally from `state.vscdb` and sent only to
  `api2.cursor.sh`. That database is never modified; token refresh stays in
  memory for the request.
- Quota and credential data are not sent to a Quota Tray backend.
- No telemetry is collected.

Provider quota APIs can change independently of Quota Tray. An integration
failure is not automatically a security incident, but any behavior that leaks
credentials should be reported privately.
