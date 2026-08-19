namespace Factory.Runtime;

/// <summary>
/// Default <see cref="IRepoStateProvider"/> that shells out to git via <see cref="Shell"/>. All
/// inputs arrive via the constructor so a fake <see cref="IRepoStateProvider"/> can stand in for
/// this in tests.
/// </summary>
public sealed class GitRepoStateProvider(string repoRoot) : IRepoStateProvider
{
    public async Task<string> GetCurrentMasterShaAsync(CancellationToken ct = default) =>
        (await Shell.GitAsync(repoRoot, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Stdout.Trim();

    public async Task<int?> CommitsBehindHeadAsync(string sha, CancellationToken ct = default)
    {
        // rev-list counts only commits it can reach from HEAD, so an unknown sha fails outright
        // rather than reporting a distance. That failure is the "not this repository" answer.
        var counted = await Shell.GitAsync(repoRoot, ct, "rev-list", "--count", $"{sha}..HEAD")
            .ConfigureAwait(false);

        return counted.ExitCode == 0 && int.TryParse(counted.Stdout.Trim(), out var behind)
            ? behind
            : null;
    }
}
