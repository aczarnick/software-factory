using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Whether the running harness is the build this repository currently describes.
///
/// The factory commits improvements to its own source, but the binary on PATH is whatever was last
/// installed. Nothing reconciled the two, so a factory could spend a week building features for
/// itself and go on running a binary that had none of them — reporting commands as missing that its
/// own source defines. Both facts were already recorded; this compares them.
/// </summary>
public sealed record HarnessStaleness(string? BuildCommit, string? HeadCommit, int CommitsBehind, bool SelfHosted)
{
    private static readonly HarnessStaleness NotSelfHosted = new(null, null, 0, false);

    /// <summary>True when this binary was built from an earlier commit of the repository it is now
    /// working on — the case where what it builds and what it runs have drifted apart.</summary>
    public bool IsStale => SelfHosted && CommitsBehind > 0;

    public string Describe =>
        $"running {Short(BuildCommit)}, repository is at {Short(HeadCommit)} — "
        + $"{CommitsBehind} commit{(CommitsBehind == 1 ? "" : "s")} behind. "
        + "Everything committed since is absent from this binary; run ./install.sh to rebuild it.";

    public static async Task<HarnessStaleness> ProbeAsync(
        string? buildCommit, IRepoStateProvider repo, CancellationToken ct = default)
    {
        // A build with no recorded commit was produced outside a checkout. There is nothing to
        // compare it against, and guessing would only produce a warning nobody can act on.
        if (string.IsNullOrWhiteSpace(buildCommit)) return NotSelfHosted;

        var behind = await repo.CommitsBehindHeadAsync(buildCommit, ct).ConfigureAwait(false);
        if (behind is null) return NotSelfHosted;

        var head = await repo.GetCurrentMasterShaAsync(ct).ConfigureAwait(false);
        return new HarnessStaleness(buildCommit, head, behind.Value, SelfHosted: true);
    }

    /// <summary>Probes the harness against the repository it is deployed into.</summary>
    public static Task<HarnessStaleness> ProbeAsync(string repoRoot, CancellationToken ct = default) =>
        ProbeAsync(FactoryVersion.Commit, new GitRepoStateProvider(repoRoot), ct);

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "an unknown commit" : sha[..Math.Min(12, sha.Length)];
}
