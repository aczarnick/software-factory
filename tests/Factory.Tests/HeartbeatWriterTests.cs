using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class HeartbeatWriterTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private FactoryPaths Paths => new(_dir);

    private static HeartbeatStatus Status(string statusText = "running") => new()
    {
        Pid = 4242,
        StartedAtUtc = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc),
        Status = statusText
    };

    [Fact]
    public async Task WriteAsyncProducesJsonDeserializableBackToMatchingStatus()
    {
        var paths = Paths;
        var status = Status();

        await new HeartbeatWriter(paths).WriteAsync(status);

        var json = await File.ReadAllTextAsync(paths.StatusFile);
        var read = FactoryJson.Read<HeartbeatStatus>(json);

        Assert.NotNull(read);
        Assert.Equal(status.Pid, read!.Pid);
        Assert.Equal(status.StartedAtUtc, read.StartedAtUtc);
        Assert.Equal(status.Status, read.Status);
    }

    [Fact]
    public async Task WriteAsyncReplacesExistingFileContents()
    {
        var paths = Paths;
        var writer = new HeartbeatWriter(paths);

        await writer.WriteAsync(Status("running"));
        await writer.WriteAsync(Status("stopped"));

        var json = await File.ReadAllTextAsync(paths.StatusFile);
        var read = FactoryJson.Read<HeartbeatStatus>(json);

        Assert.Equal("stopped", read!.Status);
    }

    [Fact]
    public async Task WriteAsyncLeavesNoTempFileBehind()
    {
        var paths = Paths;

        await new HeartbeatWriter(paths).WriteAsync(Status());

        var leftovers = Directory.GetFiles(paths.Root, "*.tmp");
        Assert.Empty(leftovers);
    }
}
