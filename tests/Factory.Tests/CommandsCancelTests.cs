using Factory.Cli;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>Redirects Console.Out, which is process-global — grouped into a shared,
/// non-parallel collection with other tests that do the same so they cannot race each other.</summary>
[CollectionDefinition("Console")]
public class ConsoleCollection;

[Collection("Console")]
public sealed class CommandsCancelTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void Cancel_transitions_the_item_to_cancelled_and_confirms_it()
    {
        WorkItem item;
        using (var host = FactoryHost.Init(_dir, transport: new FakeTransport()))
            item = host.Submit(WorkItem.Create("build the thing"));

        var cli = CommandLine.Parse(["cancel", item.Id, "--dir", _dir]);

        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        int exitCode;
        try
        {
            exitCode = Commands.Cancel(cli);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains(item.Id, writer.ToString());
        Assert.Contains("cancelled", writer.ToString());

        using var reopened = FactoryHost.Open(_dir);
        Assert.Equal(WorkItemState.Cancelled, reopened.Services.State.Items[item.Id].State);
    }
}
