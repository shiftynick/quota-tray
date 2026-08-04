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

    [Fact]
    public void CursorParser_MapsIncludedAndPoolWindowsFromCents()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "billingCycleStart": "1768399334000",
              "billingCycleEnd": "1771077734000",
              "planUsage": {
                "totalSpend": 23222,
                "includedSpend": 23222,
                "bonusSpend": 0,
                "remaining": 16778,
                "limit": 40000,
                "autoPercentUsed": 10.5,
                "apiPercentUsed": 46.444,
                "totalPercentUsed": 58.055
              }
            }
            """);

        var snapshot = CursorQuotaService.ParseUsage(document.RootElement, "ultra");

        Assert.Equal("Ultra", snapshot.Plan);
        Assert.Collection(
            snapshot.Windows,
            window =>
            {
                Assert.Equal("Included", window.Label);
                Assert.Equal(41.945, window.RemainingPercent, 3);
                Assert.Equal(
                    DateTimeOffset.FromUnixTimeMilliseconds(1771077734000),
                    window.ResetsAt);
                Assert.Equal(TimeSpan.FromMilliseconds(2678400000), window.Duration);
            },
            window =>
            {
                Assert.Equal("Auto + Composer", window.Label);
                Assert.Equal(89.5, window.RemainingPercent);
            },
            window =>
            {
                Assert.Equal("API models", window.Label);
                Assert.Equal(53.556, window.RemainingPercent, 3);
            });
    }

    [Fact]
    public void CursorParser_DerivesRemainingWhenFieldIsMissing()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "billingCycleStart": "1783476262000",
              "billingCycleEnd": "1786154662000",
              "planUsage": {
                "includedSpend": 2000,
                "limit": 2000,
                "autoPercentUsed": 39.57666666666667,
                "apiPercentUsed": 88.57777777777778,
                "totalPercentUsed": 45.96811594202899
              }
            }
            """);

        var snapshot = CursorQuotaService.ParseUsage(document.RootElement, "pro");

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(0, snapshot.Windows[0].RemainingPercent);
        Assert.Equal("Included", snapshot.Windows[0].Label);
        Assert.Equal(60.42333333333333, snapshot.Windows[1].RemainingPercent, 5);
        Assert.Equal(11.42222222222222, snapshot.Windows[2].RemainingPercent, 5);
    }

    [Fact]
    public void CursorJwtExpiry_DetectsExpiredTokens()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var jwt = BuildUnsignedJwt(past);

        Assert.True(CursorQuotaService.IsExpiredOrExpiring(jwt));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(past),
            CursorQuotaService.TryReadJwtExpiry(jwt));
    }

    private static string BuildUnsignedJwt(long expSeconds)
    {
        static string Encode(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode("{}")}.{Encode($"{{\"exp\":{expSeconds}}}")}.sig";
    }
}
