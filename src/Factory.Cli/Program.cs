using Factory.Agents;
using Factory.Cli;
using Factory.Core;
using Factory.Evolution;
using Factory.Runtime;

var cli = CommandLine.Parse(args);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Output.Line();
    Output.Warn("stopping — finishing in-flight work, then draining");
    cancellation.Cancel();
};

try
{
    return cli.Command switch
    {
        "init" => Commands.Init(cli),
        "up" => await Commands.Up(cli, cancellation.Token),
        "build" => await Commands.Build(cli, cancellation.Token),
        "intake" => await Commands.Intake(cli, cancellation.Token),
        "add" => Commands.Add(cli),
        "activate" => Commands.Activate(cli),
        "status" => Commands.Status(cli),
        "ls" => Commands.List(cli),
        "show" => Commands.Show(cli),
        "link" => Commands.Link(cli),
        "evolve" => await Commands.Evolve(cli, cancellation.Token),
        "report" => Commands.Report(cli),
        "prompts" => Commands.Prompts(cli),
        "help" or "--help" or "-h" => Commands.Help(),
        "version" or "--version" => Commands.Version(),
        _ => Commands.Unknown(cli.Command)
    };
}
catch (OperationCanceledException)
{
    Output.Warn("cancelled");
    return 130;
}
catch (Exception ex)
{
    Output.Error(ex.Message);
    if (Environment.GetEnvironmentVariable("FACTORY_DEBUG") is not null)
        Output.Line(ex.ToString());
    return 1;
}
