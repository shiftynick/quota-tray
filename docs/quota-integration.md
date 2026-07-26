# Claude and Codex quota integration

This document describes how Quota Tray reads subscription quota information.
It covers subscription limits associated with Claude Code and ChatGPT sign-ins,
not API billing or token accounting.

## Support boundary

- Claude subscription quota requires Claude Code to be signed in with a Claude
  subscription OAuth credential.
- Codex subscription quota requires the Codex CLI to be installed and signed
  in with ChatGPT authentication.
- API keys are not substitutes for subscription OAuth credentials.
- Quota readings are best-effort. Provider fields and endpoints can change.
- Quota Tray reads Claude's credential file but never modifies it.
- Quota Tray delegates all Codex credential access and refresh to Codex.

## Normalized data

Each provider response becomes a `ProviderSnapshot` containing:

- provider name;
- plan label, when available;
- fetch timestamp;
- an error, when the refresh failed; and
- zero or more quota windows.

Each quota window contains:

- a stable local identifier;
- a display label;
- remaining percentage, clamped to 0–100;
- an absolute reset timestamp, when available; and
- the window duration, when known.

The UI consumes only this normalized model. Raw provider responses and
credentials are never exposed to the view model.

## Claude

### Credential discovery

On Windows, the default credential path is:

```text
%USERPROFILE%\.claude\.credentials.json
```

If `CLAUDE_CONFIG_DIR` is set, Quota Tray uses:

```text
<CLAUDE_CONFIG_DIR>\.credentials.json
```

The required portion of the file is:

```json
{
  "claudeAiOauth": {
    "accessToken": "<redacted>",
    "subscriptionType": "max"
  }
}
```

`accessToken` is used only in memory for the provider request.
`subscriptionType`, when present, supplies the plan label.

### Usage request

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <CLAUDE_ACCESS_TOKEN>
Accept: application/json
anthropic-beta: oauth-2025-04-20
```

The usage endpoint is not a stable public API contract. Failures must leave the
last successful snapshot visible and marked stale.

Representative redacted response:

```json
{
  "five_hour": {
    "utilization": 22.5,
    "resets_at": "2026-07-26T18:00:00Z"
  },
  "seven_day": {
    "utilization": 41,
    "resets_at": "2026-08-01T18:00:00Z"
  },
  "limits": [
    {
      "percent": 35,
      "resets_at": "2026-08-01T18:00:00Z",
      "kind": "weekly_model",
      "group": "weekly",
      "scope": {
        "model": {
          "id": "example-model",
          "display_name": "Example"
        }
      }
    }
  ]
}
```

### Parsing

For top-level quota windows:

1. Inspect every top-level object rather than relying on a fixed allowlist.
2. Accept the object when it has a finite numeric `utilization` and a parseable
   string `resets_at`.
3. Calculate `remainingPercent = clamp(100 - utilization, 0, 100)`.
4. Apply stable labels and ordering to known windows.
5. Preserve unknown valid windows using a humanized property name.

For scoped model limits under `limits`:

1. Read `percent` as percentage used.
2. Read `resets_at`.
3. Read `scope.model.display_name`.
4. Treat `group: weekly` or a `kind` beginning with `weekly` as a seven-day
   window.

`extra_usage`, `spend`, and other objects without the quota-window fields are
ignored.

### Caching and rate limits

- Successful Claude snapshots are cached for at least five minutes.
- HTTP 429 honors `Retry-After` when present.
- Without a usable `Retry-After`, Claude checks pause for ten minutes.
- Cached data remains visible during the cooldown.
- The current application does not refresh or write Claude Code tokens. If the
  sign-in expires, the user must run Claude Code `/login`.

## Codex

### Process

Quota Tray starts a short-lived local process:

```text
codex app-server
```

Messages are newline-delimited JSON written to standard input. Responses are
read from standard output. Standard error is drained without being displayed or
logged.

### RPC sequence

Initialize:

```json
{
  "method": "initialize",
  "id": 0,
  "params": {
    "clientInfo": {
      "name": "quota_tray",
      "title": "Quota Tray",
      "version": "0.1.0"
    }
  }
}
```

After the response to request `0`, notify initialization:

```json
{
  "method": "initialized",
  "params": {}
}
```

Read quota limits:

```json
{
  "method": "account/rateLimits/read",
  "id": 2,
  "params": {}
}
```

The application waits for the response whose `id` is `2`, ignoring unrelated
notifications.

### Response mapping

Representative response result:

```json
{
  "rateLimits": {
    "planType": "plus",
    "primary": {
      "usedPercent": 20,
      "windowDurationMins": 300,
      "resetsAt": 1785092400
    },
    "secondary": {
      "usedPercent": 35,
      "windowDurationMins": 10080,
      "resetsAt": 1785697200
    }
  },
  "rateLimitsByLimitId": {
    "example": {
      "limitName": "Example model",
      "secondary": {
        "usedPercent": 10,
        "windowDurationMins": 10080,
        "resetsAt": 1785697200
      }
    }
  }
}
```

Mapping rules:

- `usedPercent` is converted to a clamped remaining percentage.
- `resetsAt` is interpreted as Unix seconds.
- `300` minutes is labeled as a five-hour window.
- `10080` minutes is labeled as a seven-day window.
- Durations from 28 through 31 days are labeled monthly.
- Named limits are prefixed with `limitName`.
- A named `Codex` entry that aliases the default primary/secondary windows is
  removed.
- The plan label comes from `rateLimits.planType`.

The app-server child process is terminated after the response or when the
request is cancelled.

## Polling and errors

- Both providers refresh immediately on startup.
- Background refresh runs every five minutes.
- Manual and scheduled refreshes are deduplicated.
- Each provider has a 20-second application timeout.
- Provider refreshes run concurrently.
- One provider's failure does not discard the other provider's result.
- A failed refresh retains existing windows and marks them stale.
- Shutdown cancels in-flight refresh work.

## Weekly pacing and daily budget

Pacing insights are an optional local calculation and are disabled by default.
They are not daily quotas reported by Claude or Codex.

For windows of six days or longer:

```text
elapsedDays = durationDays - remainingDays
usedPercent = 100 - remainingPercent
targetUsed = elapsedDays / durationDays * 100
averagePerDay = usedPercent / elapsedDays
dailyBudget = remainingPercent / remainingDays
```

Values are guarded at the start and end of a window to avoid division by zero.
The calculation assumes usage is paced evenly across the rolling window.

## Security

- Never log OAuth tokens, authorization headers, raw credential files, raw
  provider responses, account identifiers, or email addresses.
- Never send credentials or quota data to an application-owned server.
- Resolve credential paths from the current user's profile or
  `CLAUDE_CONFIG_DIR`.
- Keep provider calls in the local trusted process.
- Use HTTPS, request timeouts, and bounded response handling.
- Treat provider failures as recoverable and never automatically log the user
  out.

## Manual verification

1. Compare Claude remaining percentages and reset times with Claude Code
   `/usage`.
2. Compare Codex values with the Codex usage/status surface.
3. Expire or remove one provider sign-in and confirm the other still refreshes.
4. Disconnect the network and confirm the last snapshot is retained as stale.
5. Confirm no credential value appears in the process command line, UI, logs,
   or crash output.
