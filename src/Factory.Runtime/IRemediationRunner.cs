namespace Factory.Runtime;

/// <summary>Names the toolchain a remediation is being asked to fix, e.g. "dotnet" because the
/// SDK is missing from PATH. Kept minimal deliberately: CheckStation does not yet consult this
/// abstraction, so the shape only needs to carry enough for a runner to decide what to run.</summary>
public sealed record ToolchainRequirement(string Name, string? MissingTool = null);

public sealed record RemediationResult(bool Found, bool Attempted, bool Succeeded, string? Output, string? Error)
{
    public static readonly RemediationResult NotFound = new(false, false, false, null, null);
}

/// <summary>
/// Discovers and runs a repository's documented remediation command — an install.sh, a setup
/// script — so a missing toolchain can be self-healed instead of just reported as broken.
/// Injectable so tests can substitute a fake instead of shelling out.
/// </summary>
public interface IRemediationRunner
{
    Task<RemediationResult> RemediateAsync(ToolchainRequirement requirement, CancellationToken ct = default);
}
