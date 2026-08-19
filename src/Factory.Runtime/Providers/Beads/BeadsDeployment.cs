namespace Factory.Runtime;

/// <summary>Idempotent beads setup. Safe to run on every open: <c>--init-if-missing</c> makes an
/// existing database a no-op without risking a destructive re-init, and the vocabulary writes are
/// last-write-wins.</summary>
public static class BeadsDeployment
{
    public static void EnsureInitialised(BeadsCli cli, string prefix, Action<string> log)
    {
        if (!cli.IsAvailable)
            throw new InvalidOperationException(
                "The beads backlog provider needs `bd` on PATH. Install it, or set " +
                "\"workItemStore\": { \"provider\": \"ledger\" } in .factory/factory.json.");

        var init = cli.Exec("init", "--init-if-missing", "--prefix", prefix);
        if (!init.Ok) throw new InvalidOperationException($"bd init failed: {init.Combined}");

        // The frozen category on draft and failed is what keeps proposals and failed work out of
        // `bd ready`, so a database without this vocabulary would dispatch both.
        Install(cli, "status.custom", BeadMapper.CustomStatuses, log);
        Install(cli, "types.custom", BeadMapper.CustomTypes, log);
    }

    private static void Install(BeadsCli cli, string key, string value, Action<string> log)
    {
        var result = cli.Exec("config", "set", key, value);
        if (!result.Ok) log($"could not install {key}: {result.Combined}");
    }
}
