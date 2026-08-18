namespace Factory.Runtime;

/// <summary>
/// Reports the current master/HEAD commit SHA of the repository. Injectable so toolchain
/// baseline staleness checks can be tested against a fake instead of shelling to real git.
/// </summary>
public interface IRepoStateProvider
{
    Task<string> GetCurrentMasterShaAsync(CancellationToken ct = default);
}
