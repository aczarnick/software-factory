namespace Factory.Runtime;

/// <summary>
/// Runs the repository's documented remediation script — install.sh at the repo root by
/// default, or a configured override — through the same invocation/timeout/cancellation
/// pattern as <see cref="Shell"/>. All inputs arrive via the constructor so a fake
/// <see cref="IRemediationRunner"/> can stand in for this in tests.
/// </summary>
public sealed class DefaultRemediationRunner(string repoRoot, string? scriptPath = null, int timeoutSeconds = 900)
    : IRemediationRunner
{
    private readonly string _scriptPath = scriptPath ?? Path.Combine(repoRoot, "install.sh");

    public async Task<RemediationResult> RemediateAsync(ToolchainRequirement requirement, CancellationToken ct = default)
    {
        if (!File.Exists(_scriptPath)) return RemediationResult.NotFound;

        var result = await Shell.RunAsync($"sh \"{_scriptPath}\"", repoRoot, timeoutSeconds, ct)
            .ConfigureAwait(false);

        return new RemediationResult(Found: true, Attempted: true, result.Ok, result.Stdout, result.Stderr);
    }
}
