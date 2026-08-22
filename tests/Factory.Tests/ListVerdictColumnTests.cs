using Factory.Cli;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>The `passed` column reports criteria that were actually settled. Judged criteria are
/// deferred to review and never appear in a deterministic verdict, so counting them in the
/// denominator makes a fully verified item read as partly failed for the rest of its life.</summary>
[Collection("Console")]
public sealed class ListVerdictColumnTests : IDisposable
{
    private readonly string _dir = TempDir.Create();

    public void Dispose() => TempDir.Delete(_dir);

    private static AcceptanceCriterion Machine(string statement) => AcceptanceCriterion.Command(statement, "true");
    private static AcceptanceCriterion Judged(string statement) => AcceptanceCriterion.Judged(statement, "is it sensible");

    private string RunList(params WorkItem[] items)
    {
        using (var host = FactoryHost.Init(_dir, transport: new FakeTransport()))
        {
            foreach (var item in items) host.Submit(item);

            foreach (var item in items)
            {
                var machineResults = item.AcceptanceCriteria
                    .Where(c => c.Verification.IsDeterministic)
                    .Select(c => CriterionResult.Pass(c.Id, "passed"))
                    .ToList();

                if (machineResults.Count > 0)
                    host.Services.Record(new CriteriaVerified(item.Id, machineResults));
            }
        }

        using var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            Commands.List(CommandLine.Parse(["ls", "--dir", _dir]));
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void AnItemWhoseMachineCriteriaAllPassedReadsAsComplete()
    {
        var item = WorkItem.Create("mixed criteria") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [Machine("it builds"), Machine("tests pass"), Judged("it reads well")]
        };

        var output = RunList(item);

        // Two machine criteria were checked and both passed. Rendering "2/3" would blame the item
        // for a criterion that verification deliberately handed to the review station.
        Assert.Contains("2/2", output);
        Assert.DoesNotContain("2/3", output);
    }

    [Fact]
    public void AFailingMachineCriterionStillShowsAgainstTheMachineTotal()
    {
        var item = WorkItem.Create("one failed") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [Machine("it builds"), Machine("tests pass")]
        };

        using (var host = FactoryHost.Init(_dir, transport: new FakeTransport()))
        {
            host.Submit(item);
            host.Services.Record(new CriteriaVerified(item.Id, [
                CriterionResult.Pass(item.AcceptanceCriteria[0].Id, "passed"),
                CriterionResult.Fail(item.AcceptanceCriteria[1].Id, "exited 1")
            ]));
        }

        using var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try { Commands.List(CommandLine.Parse(["ls", "--dir", _dir])); }
        finally { Console.SetOut(original); }

        Assert.Contains("1/2", writer.ToString());
    }

    [Fact]
    public void AnItemWithOnlyJudgedCriteriaIsNotReportedAsMachineVerified()
    {
        var item = WorkItem.Create("judgement only") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [Judged("it reads well")]
        };

        var output = RunList(item);

        // Nothing a command can settle, so there is no machine verdict to report either way.
        Assert.Contains("judged", output);
    }
}
