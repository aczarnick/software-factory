using Factory.Runtime;

namespace Factory.Tests;

public class ShellRunTests
{
    [Fact]
    public void Run_captures_stdout_and_exit_code()
    {
        var result = Shell.Run("/bin/echo", ["hello"], Directory.GetCurrentDirectory());

        Assert.True(result.Ok);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void Run_reports_a_non_zero_exit()
    {
        var result = Shell.Run("/bin/sh", ["-c", "exit 3"], Directory.GetCurrentDirectory());

        Assert.False(result.Ok);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Run_passes_environment_variables_through()
    {
        var result = Shell.Run("/bin/sh", ["-c", "echo $FACTORY_PROBE"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["FACTORY_PROBE"] = "set" });

        Assert.Contains("set", result.Stdout);
    }

    [Fact]
    public void Run_times_out_rather_than_hanging()
    {
        var result = Shell.Run("/bin/sh", ["-c", "sleep 5"], Directory.GetCurrentDirectory(),
            timeoutSeconds: 1);

        Assert.True(result.TimedOut);
    }

    [Fact]
    public void Run_does_not_wait_for_a_lingering_grandchild_to_release_the_pipe()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = Shell.Run("/bin/sh", ["-c", "(sleep 8 & ) ; echo done"],
            Directory.GetCurrentDirectory(), timeoutSeconds: 30);

        started.Stop();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("done", result.Stdout);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6),
            $"Run waited {started.Elapsed.TotalSeconds:F1}s for a grandchild holding the pipe.");
    }
}
