using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Publishes a <see cref="HeartbeatStatus"/> to <see cref="FactoryPaths.StatusFile"/> via
/// temp-file-then-move, so an external reader never observes a partially written file.
/// </summary>
public sealed class HeartbeatWriter(FactoryPaths paths)
{
    public async Task WriteAsync(HeartbeatStatus status, CancellationToken ct = default)
    {
        var target = paths.StatusFile;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var temp = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileName(target)}.{Guid.NewGuid():n}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, FactoryJson.Write(status), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
