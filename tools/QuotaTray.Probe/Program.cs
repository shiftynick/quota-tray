using QuotaTray;

var providers = new (string Name, Func<CancellationToken, Task<ProviderSnapshot>> Read)[]
{
    ("Claude", new ClaudeQuotaService().ReadAsync),
    ("Codex", new CodexQuotaService().ReadAsync),
    ("Cursor", new CursorQuotaService().ReadAsync)
};

var failed = false;
foreach (var provider in providers)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        var snapshot = await provider.Read(timeout.Token);
        Console.WriteLine(
            $"{provider.Name}: OK, {snapshot.Windows.Count} window(s): " +
            string.Join(", ", snapshot.Windows.Select(window => window.Label)));
        failed |= snapshot.Windows.Count == 0;
    }
    catch (Exception ex)
    {
        failed = true;
        Console.WriteLine($"{provider.Name}: FAILED: {ex}");
    }
}

return failed ? 1 : 0;
