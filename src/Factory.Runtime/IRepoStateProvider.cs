namespace Factory.Runtime;

/// <summary>
/// Reports the current master/HEAD commit SHA of the repository. Injectable so toolchain
/// baseline staleness checks can be tested against a fake instead of shelling to real git.
/// </summary>
public interface IRepoStateProvider
{
    Task<string> GetCurrentMasterShaAsync(CancellationToken ct = default);

    /// <summary>How many commits <paramref name="sha"/> is behind HEAD, or <c>null</c> when this
    /// repository does not contain that commit at all. The null case is what separates "the harness
    /// is this repository's own stale output" from "the harness was built somewhere else entirely",
    /// which must not warn.</summary>
    Task<int?> CommitsBehindHeadAsync(string sha, CancellationToken ct = default);
}
