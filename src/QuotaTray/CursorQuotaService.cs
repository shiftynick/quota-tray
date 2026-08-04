using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaTray;

public sealed class CursorQuotaService
{
    private const string ApiBase = "https://api2.cursor.sh";
    private const string OAuthClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
    private const string AccessTokenKey = "cursorAuth/accessToken";
    private const string RefreshTokenKey = "cursorAuth/refreshToken";
    private const string MembershipTypeKey = "cursorAuth/stripeMembershipType";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var auth = ReadLocalAuth();
        var accessToken = await EnsureAccessTokenAsync(auth, cancellationToken);
        using var request = CreateUsageRequest(accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode is 401 or 403)
        {
            accessToken = await RefreshAccessTokenAsync(auth.RefreshToken, cancellationToken);
            using var retry = CreateUsageRequest(accessToken);
            using var retryResponse = await Http.SendAsync(retry, cancellationToken);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Cursor quota request failed: {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}");
            }

            return await ParseResponseAsync(retryResponse, auth.Plan, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Cursor quota request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return await ParseResponseAsync(response, auth.Plan, cancellationToken);
    }

    private static async Task<ProviderSnapshot> ParseResponseAsync(
        HttpResponseMessage response,
        string? plan,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseUsage(document.RootElement, plan);
    }

    internal static ProviderSnapshot ParseUsage(JsonElement root, string? plan)
    {
        if (!root.TryGetProperty("planUsage", out var planUsage) ||
            planUsage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Cursor returned no plan usage. Sign in to Cursor and open the dashboard once.");
        }

        var windows = new List<QuotaWindowSnapshot>();
        var resetsAt = ReadUnixMilliseconds(root, "billingCycleEnd");
        var duration = ReadBillingDuration(root);

        if (TryBuildIncludedWindow(planUsage, resetsAt, duration, out var included))
        {
            windows.Add(included);
        }

        if (ClaudeQuotaService.TryReadDouble(planUsage, "autoPercentUsed", out var autoUsed))
        {
            windows.Add(new QuotaWindowSnapshot(
                "auto",
                "Auto + Composer",
                ClaudeQuotaService.Clamp(100 - autoUsed),
                resetsAt,
                duration));
        }

        if (ClaudeQuotaService.TryReadDouble(planUsage, "apiPercentUsed", out var apiUsed))
        {
            windows.Add(new QuotaWindowSnapshot(
                "api",
                "API models",
                ClaudeQuotaService.Clamp(100 - apiUsed),
                resetsAt,
                duration));
        }

        if (windows.Count == 0)
        {
            throw new InvalidOperationException(
                "Cursor returned plan usage without recognizable quota fields.");
        }

        return new ProviderSnapshot(
            "Cursor",
            string.IsNullOrWhiteSpace(plan) ? null : ClaudeQuotaService.Humanize(plan),
            windows,
            null,
            DateTimeOffset.UtcNow);
    }

    internal static bool TryBuildIncludedWindow(
        JsonElement planUsage,
        DateTimeOffset? resetsAt,
        TimeSpan? duration,
        out QuotaWindowSnapshot window)
    {
        window = default!;
        double remainingPercent;
        if (TryReadCents(planUsage, "limit", out var limit) && limit > 0)
        {
            double remainingCents;
            if (TryReadCents(planUsage, "remaining", out var remaining))
            {
                remainingCents = remaining;
            }
            else if (TryReadCents(planUsage, "includedSpend", out var includedSpend))
            {
                remainingCents = Math.Max(0, limit - includedSpend);
            }
            else
            {
                remainingCents = -1;
            }

            if (remainingCents >= 0)
            {
                remainingPercent = ClaudeQuotaService.Clamp(remainingCents / limit * 100);
                window = new QuotaWindowSnapshot(
                    "included",
                    "Included",
                    remainingPercent,
                    resetsAt,
                    duration);
                return true;
            }
        }

        if (ClaudeQuotaService.TryReadDouble(planUsage, "totalPercentUsed", out var totalUsed))
        {
            window = new QuotaWindowSnapshot(
                "included",
                "Included",
                ClaudeQuotaService.Clamp(100 - totalUsed),
                resetsAt,
                duration);
            return true;
        }

        return false;
    }

    private static TimeSpan? ReadBillingDuration(JsonElement root)
    {
        var start = ReadUnixMilliseconds(root, "billingCycleStart");
        var end = ReadUnixMilliseconds(root, "billingCycleEnd");
        if (start is null || end is null || end <= start)
        {
            return null;
        }

        return end.Value - start.Value;
    }

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var raw))
        {
            return null;
        }

        long millis;
        switch (raw.ValueKind)
        {
            case JsonValueKind.Number when raw.TryGetInt64(out millis):
                break;
            case JsonValueKind.String when long.TryParse(
                raw.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out millis):
                break;
            default:
                return null;
        }

        if (millis <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadCents(JsonElement element, string name, out double value) =>
        ClaudeQuotaService.TryReadDouble(element, name, out value);

    private static HttpRequestMessage CreateUsageRequest(string accessToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/aiserver.v1.DashboardService/GetCurrentPeriodUsage")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        return request;
    }

    private static async Task<string> EnsureAccessTokenAsync(
        CursorLocalAuth auth,
        CancellationToken cancellationToken)
    {
        if (!IsExpiredOrExpiring(auth.AccessToken))
        {
            return auth.AccessToken;
        }

        return await RefreshAccessTokenAsync(auth.RefreshToken, cancellationToken);
    }

    private static async Task<string> RefreshAccessTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/oauth/token")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    grant_type = "refresh_token",
                    client_id = OAuthClientId,
                    refresh_token = refreshToken
                }),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await Http.SendAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("shouldLogout", out var shouldLogout) &&
            shouldLogout.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                "Cursor sign-in expired. Open Cursor and sign in again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Cursor token refresh failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var accessToken = root.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Cursor sign-in expired. Open Cursor and sign in again.");
        }

        return accessToken;
    }

    internal static bool IsExpiredOrExpiring(string jwt, DateTimeOffset? now = null)
    {
        var exp = TryReadJwtExpiry(jwt);
        if (exp is null)
        {
            return false;
        }

        return exp.Value <= (now ?? DateTimeOffset.UtcNow).AddMinutes(2);
    }

    internal static DateTimeOffset? TryReadJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("exp", out var expElement) ||
                !expElement.TryGetInt64(out var expSeconds))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(expSeconds);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static CursorLocalAuth ReadLocalAuth(string? databasePath = null)
    {
        databasePath ??= GetDefaultDatabasePath();
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                "Cursor is not signed in on this computer. Open Cursor and sign in.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var accessToken = ReadItem(connection, AccessTokenKey);
            var refreshToken = ReadItem(connection, RefreshTokenKey);
            var plan = ReadItem(connection, MembershipTypeKey);

            if (string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new InvalidOperationException(
                    "Cursor is not signed in on this computer. Open Cursor and sign in.");
            }

            return new CursorLocalAuth(accessToken, refreshToken, plan);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                "Could not read Cursor sign-in state. Quit Cursor and try again, or sign in again.",
                ex);
        }
    }

    private static string? ReadItem(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    internal static string GetDefaultDatabasePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor",
            "User",
            "globalStorage",
            "state.vscdb");
}

internal sealed record CursorLocalAuth(
    string AccessToken,
    string RefreshToken,
    string? Plan);
