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
}
