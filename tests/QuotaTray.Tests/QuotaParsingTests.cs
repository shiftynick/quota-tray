using System.Text.Json;

namespace QuotaTray.Tests;

public sealed class QuotaParsingTests
{
    [Fact]
    public void ClaudeParser_PreservesKnownUnknownAndScopedWindows()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "five_hour": {
                "utilization": 22.5,
                "resets_at": "2026-07-26T18:00:00Z"
              },
              "future_window": {
                "utilization": "40",
                "resets_at": "2026-07-28T18:00:00Z"
              },
              "extra_usage": {
                "is_enabled": false
              },
              "limits": [
                {
                  "percent": 60,
                  "resets_at": "2026-08-01T18:00:00Z",
                  "kind": "weekly_model",
                  "group": "weekly",
                  "scope": {
                    "model": {
                      "id": "fable",
                      "display_name": "Fable"
                    }
                  }
                }
              ]
            }
            """);

        var windows = ClaudeQuotaService.ParseUsageWindows(document.RootElement);

        Assert.Collection(
            windows,
            window =>
            {
                Assert.Equal("5-hour limit", window.Label);
                Assert.Equal(77.5, window.RemainingPercent);
            },
            window =>
            {
                Assert.Equal("7-day Fable", window.Label);
                Assert.Equal(40, window.RemainingPercent);
            },
            window =>
            {
                Assert.Equal("Future Window", window.Label);
                Assert.Equal(60, window.RemainingPercent);
            });
    }

    [Fact]
    public void CodexParser_MapsDurationsAndRemovesDefaultAlias()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "planType": "plus",
                "primary": {
                  "usedPercent": 25,
                  "windowDurationMins": 300,
                  "resetsAt": 1785092400
                },
                "secondary": {
                  "usedPercent": 50,
                  "windowDurationMins": 10080,
                  "resetsAt": 1785697200
                }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitName": "Codex",
                  "primary": {
                    "usedPercent": 25.1,
                    "windowDurationMins": 300,
                    "resetsAt": 1785092400
                  },
                  "secondary": {
                    "usedPercent": 50.1,
                    "windowDurationMins": 10080,
                    "resetsAt": 1785697200
                  }
                }
              }
            }
            """);

        var snapshot = CodexQuotaService.ParseResult(document.RootElement);

        Assert.Equal("Plus", snapshot.Plan);
        Assert.Collection(
            snapshot.Windows,
            window => Assert.Equal("5-hour limit", window.Label),
            window => Assert.Equal("7-day limit", window.Label));
    }
}
