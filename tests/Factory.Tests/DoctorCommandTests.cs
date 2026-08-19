using Factory.Cli;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

[Collection("Console")]
public class DoctorCommandTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private readonly string _binDir = TempDir.Create();
    private readonly string? _originalClaudeBin = Environment.GetEnvironmentVariable("FACTORY_CLAUDE_BIN");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FACTORY_CLAUDE_BIN", _originalClaudeBin);
        TempDir.Delete(_dir);
        TempDir.Delete(_binDir);
    }

    /// <summary>Makes the repo detectable as a dotnet project, so toolchain detection has
    /// something to find without touching the real toolchain.</summary>
    private void MakeToolchainDetectable() =>
        File.WriteAllText(Path.Combine(_dir, "app.csproj"), "<Project />");

    /// <summary>Points the doctor's claude resolution at a fake, no-op executable — the same
    /// override the transport itself honours — so resolution is deterministic and never
    /// depends on (or mutates) the real PATH shared with other tests.</summary>
    private void PutClaudeOnPath()
    {
        var exe = Path.Combine(_binDir, "claude");
        File.WriteAllText(exe, "#!/bin/sh\nexit 0\n");

        // Windows has no executable bit and resolves by extension, so the mode is both
        // unsupported there and unnecessary. Guarded rather than suppressed: the analyser is
        // right that the call site was reachable on a platform that cannot run it.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        Environment.SetEnvironmentVariable("FACTORY_CLAUDE_BIN", exe);
    }

    /// <summary>Points claude resolution at a path with nothing there, so no executable can be
    /// resolved regardless of what happens to be installed on the host running the tests.</summary>
    private void RemoveClaudeFromPath() =>
        Environment.SetEnvironmentVariable("FACTORY_CLAUDE_BIN", Path.Combine(_binDir, "claude"));

    private static int RunDoctor(CommandLine cli, out string output)
    {
        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            return Commands.Doctor(cli);
        }
        finally
        {
            Console.SetOut(original);
            output = writer.ToString();
        }
    }

    [Fact]
    public void Healthy_Deployment_Returns_ExitCode_Zero()
    {
        MakeToolchainDetectable();
        PutClaudeOnPath();
        using (FactoryHost.Init(_dir, transport: new FakeTransport())) { }

        var cli = CommandLine.Parse(["doctor", "--dir", _dir]);
        var exitCode = RunDoctor(cli, out _);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Missing_Claude_Cli_Returns_NonZero_ExitCode()
    {
        MakeToolchainDetectable();
        RemoveClaudeFromPath();
        using (FactoryHost.Init(_dir, transport: new FakeTransport())) { }

        var cli = CommandLine.Parse(["doctor", "--dir", _dir]);
        var exitCode = RunDoctor(cli, out _);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Invalid_Blueprint_Returns_NonZero_ExitCode()
    {
        MakeToolchainDetectable();
        PutClaudeOnPath();

        FactoryPaths paths;
        using (var host = FactoryHost.Init(_dir, transport: new FakeTransport()))
            paths = host.Paths;

        // Corrupted after deployment: Init validates up front, so an invalid blueprint can
        // only reach disk the way a hand-edited file would.
        var broken = Blueprint.Standard() with { Pipeline = ["decompose", "does-not-exist"] };
        File.WriteAllText(paths.BlueprintFile, FactoryJson.Write(broken, pretty: true));

        var cli = CommandLine.Parse(["doctor", "--dir", _dir]);
        var exitCode = RunDoctor(cli, out _);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Output_Includes_All_Sections()
    {
        MakeToolchainDetectable();
        PutClaudeOnPath();
        using (FactoryHost.Init(_dir, transport: new FakeTransport())) { }

        var cli = CommandLine.Parse(["doctor", "--dir", _dir]);
        RunDoctor(cli, out var output);

        Assert.Contains("Toolchain", output);
        Assert.Contains("Claude CLI", output);
        Assert.Contains("Blueprint", output);
        Assert.Contains("Usage windows", output);
    }
}
