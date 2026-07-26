## What changed

Describe the change and its user impact.

## Why

Explain the problem or motivation.

## Verification

- [ ] `dotnet format .\QuotaTray.sln --verify-no-changes --no-restore`
- [ ] `dotnet build .\QuotaTray.sln -c Release --no-restore`
- [ ] `dotnet test .\QuotaTray.sln -c Release --no-build`
- [ ] Live provider behavior checked when the change affects an integration

## Security

- [ ] No credentials, account identifiers, or raw provider responses are included
