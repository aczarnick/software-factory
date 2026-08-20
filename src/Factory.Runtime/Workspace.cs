using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Isolation for in-flight work. Each item gets its own git worktree and branch, so parallel
/// items cannot collide and a failed item is discarded without ever touching the mainline.
/// Integration is the only step that writes to the user's checkout, and it happens only after
/// every gate has passed.
/// </summary>
public sealed class Workspace(string repoRoot, FactoryPaths paths) : IDisposable
{
    // Serialises merges only. Compiles are serialised separately, by the factory-wide
    // ToolchainGate on FactoryServices — the two never nest, so there is no ordering to deadlock.
    private readonly SemaphoreSlim _integrateGate = new(1, 1);

    public string RepoRoot { get; } = Path.GetFullPath(repoRoot);

    public bool IsGitRepo => Directory.Exists(Path.Combine(RepoRoot, ".git"));

    public static string BranchFor(WorkItem item) => $"factory/{item.Id}";

    public void Dispose() => _integrateGate.Dispose();

    /// <summary>Ensures the repository can host worktrees: initialised, with at least one commit.</summary>
    public async Task EnsureRepoAsync(CancellationToken ct = default)
    {
        if (!IsGitRepo)
        {
            await Shell.GitAsync(RepoRoot, ct, "init", "-q").ConfigureAwait(false);
            await Shell.GitAsync(RepoRoot, ct, "config", "user.email", "factory@local").ConfigureAwait(false);
            await Shell.GitAsync(RepoRoot, ct, "config", "user.name", "Software Factory").ConfigureAwait(false);
        }

        var head = await Shell.GitAsync(RepoRoot, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        if (!head.Ok)
        {
            // Worktrees need a commit to branch from.
            await Shell.GitAsync(RepoRoot, ct, "commit", "--allow-empty", "-q", "-m", "factory: initial commit")
                .ConfigureAwait(false);
        }
    }

    /// <summary>Creates (or reattaches to) the isolated working directory for an item.
    /// Falls back to working in place if worktree creation is not possible, so the factory
    /// still runs in environments where git is unavailable.</summary>
    public async Task<string> AcquireAsync(WorkItem item, CancellationToken ct = default)
    {
        await EnsureRepoAsync(ct).ConfigureAwait(false);

        var path = Path.Combine(paths.WorktreesDir, item.Id);
        if (Directory.Exists(path)) return path;

        Directory.CreateDirectory(paths.WorktreesDir);
        var branch = BranchFor(item);

        var add = await Shell.GitAsync(RepoRoot, ct, "worktree", "add", "-b", branch, path, "HEAD")
            .ConfigureAwait(false);

        if (!add.Ok)
        {
            // Branch may already exist from a previous attempt.
            var reuse = await Shell.GitAsync(RepoRoot, ct, "worktree", "add", path, branch).ConfigureAwait(false);
            if (!reuse.Ok) return RepoRoot;
        }

        return Directory.Exists(path) ? path : RepoRoot;
    }

    public async Task<string> DiffAsync(string workDir, CancellationToken ct = default)
    {
        await Shell.GitAsync(workDir, ct, "add", "-A").ConfigureAwait(false);
        var diff = await Shell.GitAsync(workDir, ct, "diff", "--cached", "--stat").ConfigureAwait(false);
        var full = await Shell.GitAsync(workDir, ct, "diff", "--cached").ConfigureAwait(false);
        return $"{diff.Stdout}\n\n{full.Stdout}";
    }

    public async Task<bool> HasChangesAsync(string workDir, CancellationToken ct = default)
    {
        var status = await Shell.GitAsync(workDir, ct, "status", "--porcelain").ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(status.Stdout);
    }

    /// <summary>Commits the worktree and merges it into the mainline. Serialised, because
    /// several items can finish at once and git will not tolerate concurrent merges.</summary>
    public async Task<(bool Ok, string Detail)> IntegrateAsync(
        WorkItem item, string workDir, string message, CancellationToken ct = default)
    {
        if (workDir == RepoRoot)
            return (true, "worked in place; nothing to merge");

        await _integrateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A dirty mainline is the user's working state, not a defect in the work. Detect
            // it before touching anything so the failure is actionable rather than a merge
            // abort message, and so verified work is never discarded over it.
            // Only tracked modifications matter: an untracked scratch file cannot be
            // overwritten by a merge, so blocking on one would be needless friction.
            var mainline = await Shell.GitAsync(RepoRoot, ct,
                "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(mainline.Stdout))
            {
                var dirty = mainline.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Take(5).Select(l => l.Trim());
                return (false,
                    "the working tree has uncommitted changes, so the merge would overwrite them: " +
                    string.Join(", ", dirty) +
                    ". Commit or stash them, then requeue this item with `factory activate`.");
            }

            await Shell.GitAsync(workDir, ct, "add", "-A").ConfigureAwait(false);

            var status = await Shell.GitAsync(workDir, ct, "status", "--porcelain").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(status.Stdout))
                return (false, "no changes were produced");

            var commit = await Shell.GitAsync(workDir, ct, "commit", "-q", "-m", message).ConfigureAwait(false);
            if (!commit.Ok && !commit.Combined.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return (false, $"commit failed: {commit.Combined.Trim()}");

            var branch = BranchFor(item);
            var merge = await Shell.GitAsync(RepoRoot, ct, "merge", "--no-ff", "-m",
                $"factory: integrate {item.Id} — {item.Title}", branch).ConfigureAwait(false);

            if (!merge.Ok)
            {
                await Shell.GitAsync(RepoRoot, ct, "merge", "--abort").ConfigureAwait(false);
                return (false, $"merge conflict: {merge.Combined.Trim()}");
            }

            await CleanupAsync(item, workDir, ct).ConfigureAwait(false);
            return (true, $"merged {branch}");
        }
        finally
        {
            _integrateGate.Release();
        }
    }

    public async Task DiscardAsync(WorkItem item, string workDir, CancellationToken ct = default)
    {
        if (workDir == RepoRoot) return;
        await CleanupAsync(item, workDir, ct).ConfigureAwait(false);
        await Shell.GitAsync(RepoRoot, ct, "branch", "-D", BranchFor(item)).ConfigureAwait(false);
    }

    private async Task CleanupAsync(WorkItem item, string workDir, CancellationToken ct)
    {
        await Shell.GitAsync(RepoRoot, ct, "worktree", "remove", "--force", workDir).ConfigureAwait(false);
        if (Directory.Exists(workDir))
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }
}
