using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaTray;

public sealed class ClaudeQuotaService
{
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly object _cacheLock = new();
    private ProviderSnapshot? _cachedSnapshot;
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    private static readonly Dictionary<string, (int Order, string Label, double DurationMinutes)> KnownWindows =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["five_hour"] = (0, "5-hour limit", 300),
            ["seven_day"] = (1, "7-day limit", 10080),
            ["seven_day_oauth_apps"] = (3, "7-day OAuth apps", 10080),
            ["seven_day_opus"] = (4, "7-day Opus", 10080),
            ["seven_day_sonnet"] = (5, "7-day Sonnet", 10080),
            ["seven_day_cowork"] = (6, "7-day Cowork", 10080)
        };

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_cacheLock)
        {
            if (now < _nextRequestAt)
            {
                if (_cachedSnapshot is not null)
                {
                    return _cachedSnapshot;
                }

                throw new InvalidOperationException(
                    $"Claude quota refresh paused until {_nextRequestAt.LocalDateTime:t}.");
            }
        }

        var configRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }

        var credentialPath = Path.Combine(configRoot, ".credentials.json");
        if (!File.Exists(credentialPath))
        {
            throw new InvalidOperationException(
                $"Claude credentials not found at {credentialPath}. Run claude and use /login.");
        }

        await using var credentialStream = File.OpenRead(credentialPath);
        using var credentialDocument = await JsonDocument.ParseAsync(
            credentialStream,
            cancellationToken: cancellationToken);

        if (!credentialDocument.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            !oauth.TryGetProperty("accessToken", out var tokenElement) ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException(
                "Claude Code is not signed in with a Claude subscription.");
        }

        var token = tokenElement.GetString()!;
        var plan = oauth.TryGetProperty("subscriptionType", out var planElement)
            ? Humanize(planElement.GetString() ?? "")
            : null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var response = await Http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == 429)
        {
            var cooldown = GetRateLimitCooldown(response.Headers.RetryAfter);
            ProviderSnapshot? cached;
            lock (_cacheLock)
            {
                _nextRequestAt = DateTimeOffset.UtcNow + cooldown;
                cached = _cachedSnapshot;
            }

            if (cached is not null)
            {
                return cached;
            }

            throw new HttpRequestException(
                $"Claude rate limited quota checks; retry after {_nextRequestAt.LocalDateTime:t}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Claude quota request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var snapshot = new ProviderSnapshot(
            "Claude",
            plan,
            ParseUsageWindows(document.RootElement),
            null,
            DateTimeOffset.UtcNow);

        lock (_cacheLock)
        {
            _cachedSnapshot = snapshot;
            _nextRequestAt = DateTimeOffset.UtcNow + MinimumRefreshInterval;
        }

        return snapshot;
    }

    internal static IReadOnlyList<QuotaWindowSnapshot> ParseUsageWindows(JsonElement root)
    {
        var windows = new List<(int Order, QuotaWindowSnapshot Window)>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !TryReadDouble(property.Value, "utilization", out var used) ||
                !TryReadDate(property.Value, "resets_at", out var resetsAt))
            {
                continue;
            }

            var known = KnownWindows.TryGetValue(property.Name, out var meta);
            windows.Add((
                known ? meta.Order : 100,
                new QuotaWindowSnapshot(
                    property.Name,
                    known ? meta.Label : Humanize(property.Name),
                    Clamp(100 - used),
                    resetsAt,
                    known ? TimeSpan.FromMinutes(meta.DurationMinutes) : null)));
        }

        AddScopedModelLimits(root, windows);
        return windows
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Window.Label)
            .Select(item => item.Window)
            .ToArray();
    }

    private static TimeSpan GetRateLimitCooldown(RetryConditionHeaderValue? retryAfter)
    {
        var cooldown = retryAfter?.Delta;
        if (cooldown is null && retryAfter?.Date is not null)
        {
            cooldown = retryAfter.Date.Value - DateTimeOffset.UtcNow;
        }

        if (cooldown is null || cooldown <= TimeSpan.Zero)
        {
            cooldown = DefaultRateLimitCooldown;
        }

        return cooldown < MinimumRefreshInterval
            ? MinimumRefreshInterval
            : cooldown.Value;
    }

    private static void AddScopedModelLimits(
        JsonElement root,
        ICollection<(int Order, QuotaWindowSnapshot Window)> windows)
    {
        if (!root.TryGetProperty("limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var limit in limits.EnumerateArray())
        {
            index++;
            if (limit.ValueKind != JsonValueKind.Object ||
                !TryReadDouble(limit, "percent", out var used) ||
                !TryReadDate(limit, "resets_at", out var resetsAt) ||
                !limit.TryGetProperty("scope", out var scope) ||
                scope.ValueKind != JsonValueKind.Object ||
                !scope.TryGetProperty("model", out var model) ||
                model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("display_name", out var displayNameElement))
            {
                continue;
            }

            var displayName = displayNameElement.GetString();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var kind = limit.TryGetProperty("kind", out var kindElement)
                ? kindElement.GetString() ?? ""
                : "";
            var group = limit.TryGetProperty("group", out var groupElement)
                ? groupElement.GetString() ?? ""
                : "";
            var isWeekly =
                group.Equals("weekly", StringComparison.OrdinalIgnoreCase) ||
                kind.StartsWith("weekly", StringComparison.OrdinalIgnoreCase);
            var modelId = model.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;
            var id = $"scoped:{kind}:{modelId ?? displayName}:{index}";

            windows.Add((
                2,
                new QuotaWindowSnapshot(
                    id,
                    isWeekly ? $"7-day {displayName}" : displayName,
                    Clamp(100 - used),
                    resetsAt,
                    isWeekly ? TimeSpan.FromDays(7) : null)));
        }
    }

    private static bool TryReadDate(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var raw) &&
               raw.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   raw.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out value);
    }

    internal static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var raw))
        {
            return false;
        }

        return raw.ValueKind switch
        {
            JsonValueKind.Number => raw.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                raw.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    internal static double Clamp(double value) => Math.Max(0, Math.Min(100, value));

    internal static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var spaced = Regex.Replace(value.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }
}

public sealed class CodexQuotaService
{
    private static readonly string ClientVersion =
        typeof(CodexQuotaService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "0.1.0";

    public async Task<ProviderSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = "cmd.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/d /s /c \"codex.cmd app-server\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // A UTF-8 BOM before the first JSON object prevents app-server from
            // recognizing the initialize request.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start codex app-server.");
        var stderrDrain = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await WriteMessageAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "quota_tray",
                        title = "Quota Tray",
                        version = ClientVersion
                    }
                }
            });

            await ReadResponseAsync(process, 0, cancellationToken);

            await WriteMessageAsync(process, new
            {
                method = "initialized",
                @params = new { }
            });
            await WriteMessageAsync(process, new
            {
                method = "account/rateLimits/read",
                id = 2,
                @params = new { }
            });

            var result = await ReadResponseAsync(process, 2, cancellationToken);
            return ParseResult(result);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup of the short-lived local helper.
            }

            try
            {
                await stderrDrain;
            }
            catch
            {
                // The process may be killed while stderr is being drained.
            }
        }
    }

    private static async Task WriteMessageAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"codex app-server exited before response {expectedId}.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number ||
                    id.GetInt32() != expectedId)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var rawMessage)
                        ? rawMessage.GetString()
                        : "Unknown Codex error";
                    throw new InvalidOperationException(message);
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new InvalidOperationException("Codex returned no result.");
                }

                return result.Clone();
            }
        }
    }

    internal static ProviderSnapshot ParseResult(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimits", out var mainLimits) ||
            mainLimits.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Codex returned no subscription rate limits. Sign in with ChatGPT in Codex.");
        }

        var windows = new List<QuotaWindowSnapshot>();
        AddLimitWindows(mainLimits, "", windows);

        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byLimitId.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var label = property.Value.TryGetProperty("limitName", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                AddLimitWindows(
                    property.Value,
                    string.IsNullOrWhiteSpace(label)
                        ? ClaudeQuotaService.Humanize(property.Name)
                        : label!,
                    windows);
            }
        }

        var plan = mainLimits.TryGetProperty("planType", out var planElement)
            ? ClaudeQuotaService.Humanize(planElement.GetString() ?? "")
            : null;

        var primaryWindows = windows
            .Where(window => window.Id.StartsWith('|'))
            .ToArray();
        var withoutDefaultAliases = windows
            .Where(window => !primaryWindows.Any(
                primary => IsDefaultCodexAlias(primary, window)))
            .ToArray();
        var distinct = withoutDefaultAliases
            .GroupBy(window => window.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return new ProviderSnapshot(
            "Codex",
            plan,
            distinct,
            null,
            DateTimeOffset.UtcNow);
    }

    private static bool IsDefaultCodexAlias(
        QuotaWindowSnapshot primary,
        QuotaWindowSnapshot candidate)
    {
        // app-server can report the default quota once as the primary limit and
        // again as a named "Codex" entry with slightly different raw timing or
        // precision. Its generated ID is the primary ID prefixed with "Codex";
        // separately named model limits therefore remain untouched.
        return !ReferenceEquals(primary, candidate) &&
               candidate.Id.Equals(
                   $"Codex{primary.Id}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void AddLimitWindows(
        JsonElement limit,
        string prefix,
        ICollection<QuotaWindowSnapshot> output)
    {
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!limit.TryGetProperty(key, out var window) ||
                window.ValueKind != JsonValueKind.Object ||
                !ClaudeQuotaService.TryReadDouble(window, "usedPercent", out var used))
            {
                continue;
            }

            var durationMinutes = ClaudeQuotaService.TryReadDouble(
                window,
                "windowDurationMins",
                out var duration)
                ? duration
                : 0;
            var durationLabel = DurationLabel(durationMinutes, key);
            var label = string.IsNullOrWhiteSpace(prefix)
                ? durationLabel
                : $"{prefix} · {durationLabel}";
            var resetsAt = ReadUnixSeconds(window, "resetsAt");
            var id = $"{prefix}|{key}|{durationMinutes:0}";

            output.Add(new QuotaWindowSnapshot(
                id,
                label,
                ClaudeQuotaService.Clamp(100 - used),
                resetsAt,
                durationMinutes > 0 ? TimeSpan.FromMinutes(durationMinutes) : null));
        }
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string name)
    {
        if (!ClaudeQuotaService.TryReadDouble(element, name, out var raw) ||
            raw <= 0 ||
            raw > long.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)raw);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string DurationLabel(double minutes, string fallback)
    {
        if (Math.Abs(minutes - 300) < 1)
        {
            return "5-hour limit";
        }

        if (Math.Abs(minutes - 10080) < 1)
        {
            return "7-day limit";
        }

        if (minutes >= 28 * 24 * 60 && minutes <= 31 * 24 * 60)
        {
            return "Monthly limit";
        }

        if (minutes >= 1440)
        {
            return $"{Math.Round(minutes / 1440):0}-day limit";
        }

        if (minutes >= 60)
        {
            return $"{Math.Round(minutes / 60):0}-hour limit";
        }

        return ClaudeQuotaService.Humanize(fallback) + " limit";
    }
}
